using Dapper;
using Microsoft.Data.Sqlite;
using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>Best-effort receipts have their own SQLite file and writer lock, never an attached policy database.</summary>
internal sealed class NotificationReceiptStore
{
    private readonly string _connectionString;
    private readonly string _legacyReadConnectionString;
    private readonly bool _memory;
    // Tests register a connection-local SQL function used by a real INSERT trigger.
    internal Action<SqliteConnection>? ConfigureConnectionForTesting { get; set; }

    internal NotificationReceiptStore(string policyConnectionString)
    {
        var source = new SqliteConnectionStringBuilder(policyConnectionString);
        _memory = source.Mode == SqliteOpenMode.Memory || source.DataSource == ":memory:";
        var destination = new SqliteConnectionStringBuilder
        {
            DataSource = _memory ? "ScreenTime-notices-" + Guid.NewGuid().ToString("N")
                : Path.GetFullPath(source.DataSource) + ".notifications.db",
            Mode = _memory ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = _memory ? SqliteCacheMode.Shared : SqliteCacheMode.Private
        };
        DatabaseMigrator.RejectLegacyPath(destination.DataSource);
        _connectionString = destination.ToString();
        // Import is SELECT-only, outside the destination transaction; never ATTACH the policy database.
        if (!_memory) { source.Mode = SqliteOpenMode.ReadOnly; source.Cache = SqliteCacheMode.Private; }
        _legacyReadConnectionString = source.ToString();
    }

    internal bool TryRecord(NotificationRequest request)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute("PRAGMA synchronous=FULL");
        Initialize(conn);
        ConfigureConnectionForTesting?.Invoke(conn);
        var superseding = request.Kind switch
        {
            NotificationKind.QuotaTenMinutes => request.ReceiptKey.Replace(":QuotaTenMinutes", ":QuotaFiveMinutes"),
            NotificationKind.GraceStarted => request.ReceiptKey.Replace(":GraceStarted", ":GraceFiveMinutes"),
            _ => request.ReceiptKey
        };
        return conn.Execute("""
            INSERT INTO NotificationReceipts(ReceiptKey, AppKey, Kind, RequestedAtUtcTicks)
            SELECT @ReceiptKey, @AppKey, @Kind, @Ticks
            WHERE NOT EXISTS(SELECT 1 FROM NotificationReceipts WHERE ReceiptKey=@superseding)
            ON CONFLICT(ReceiptKey) DO NOTHING
            """, new { request.ReceiptKey, request.AppKey, Kind = (int)request.Kind,
                Ticks = request.CreatedAtUtc.UtcTicks, superseding }) == 1;
    }

    private void Initialize(SqliteConnection conn)
    {
        var version = conn.ExecuteScalar<int>("PRAGMA user_version");
        if (version > 1) throw new InvalidOperationException($"Unsupported notification schema version {version}.");
        if (version == 1) return;
        if (!_memory && conn.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master WHERE type='table'") > 0)
        {
            var backupPath = conn.DataSource + ".pre-v1.bak";
            if (!File.Exists(backupPath))
            {
                using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
                backup.Open();
                conn.BackupDatabase(backup);
            }
        }
        ReceiptRow[] legacy;
        using (var source = new SqliteConnection(_legacyReadConnectionString))
        {
            source.Open();
            legacy = source.Query<ReceiptRow>("SELECT ReceiptKey,AppKey,Kind,RequestedAtUtcTicks FROM NotificationReceipts").ToArray();
        }
        conn.Execute("PRAGMA journal_mode=WAL");
        using var tx = conn.BeginTransaction();
        // Another profile-scoped service may have completed initialization while we read the source.
        version = conn.ExecuteScalar<int>("PRAGMA user_version", transaction: tx);
        if (version > 1) throw new InvalidOperationException($"Unsupported notification schema version {version}.");
        if (version == 1) { tx.Commit(); return; }
        conn.Execute("""
            CREATE TABLE NotificationReceipts (
                ReceiptKey TEXT PRIMARY KEY NOT NULL,
                AppKey TEXT NOT NULL,
                Kind INTEGER NOT NULL CHECK(Kind BETWEEN 0 AND 4),
                RequestedAtUtcTicks INTEGER NOT NULL CHECK(RequestedAtUtcTicks>0)
            );
            """, transaction: tx);
        foreach (var row in legacy)
            conn.Execute("INSERT INTO NotificationReceipts VALUES(@ReceiptKey,@AppKey,@Kind,@RequestedAtUtcTicks)", row, tx);
        conn.Execute("PRAGMA user_version=1", transaction: tx);
        tx.Commit(); // Copy and version commit together; failure preserves both the source and an uninitialized destination.
    }

    private sealed record ReceiptRow(string ReceiptKey, string AppKey, long Kind, long RequestedAtUtcTicks);
}
