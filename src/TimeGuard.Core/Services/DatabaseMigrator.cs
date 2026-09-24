using Microsoft.Data.Sqlite;
using Dapper;
using System.Globalization;
using TimeGuard.Models;

namespace TimeGuard.Services;

/// <summary>
/// Creates the SQLite schema on first run and applies future migrations.
/// Call Migrate() once at application startup before any other DB access.
/// </summary>
public class DatabaseMigrator
{
    private readonly string _connectionString;

    public DatabaseMigrator(string connectionString)
    {
        RejectLegacyPath(new SqliteConnectionStringBuilder(connectionString).DataSource);
        _connectionString = connectionString;
    }

    internal static void RejectLegacyPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == ":memory:") return;
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TimeGuard");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(legacy + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ScreenTime schema upgrades cannot open the installed TimeGuard profile.");
    }

    public void Migrate()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Execute(conn, "PRAGMA foreign_keys=ON;");
        Execute(conn, "PRAGMA synchronous=FULL;");

        using var versionCommand = conn.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > 3) throw new InvalidOperationException($"Unsupported database schema version {version}.");
        if (version == 3) return;
        // Preserve a consistent pre-upgrade copy of existing ScreenTime profile data.
        using var tablesCommand = conn.CreateCommand();
        tablesCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AppRules'";
        if (Convert.ToInt32(tablesCommand.ExecuteScalar()) > 0 && conn.DataSource != ":memory:")
        {
            foreach (var suffix in version == 0 ? new[] { ".pre-phase2.bak", ".pre-phase3.bak", ".pre-phase4.bak" }
                : version == 1 ? new[] { ".pre-phase3.bak", ".pre-phase4.bak" } : new[] { ".pre-phase4.bak" })
            {
                var backupPath = conn.DataSource + suffix;
                if (!File.Exists(backupPath))
                {
                    using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
                    backup.Open();
                    conn.BackupDatabase(backup);
                }
            }
        }

        // Enable WAL mode for better concurrent read performance
        Execute(conn, "PRAGMA journal_mode=WAL;");
        using var tx = conn.BeginTransaction();
        if (version == 0)
        {
        Execute(conn, tx, """
            CREATE TABLE IF NOT EXISTS Settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS AppRules (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName         TEXT    NOT NULL,
                DisplayName         TEXT    NOT NULL,
                DailyLimitMins      INTEGER NOT NULL DEFAULT 0,
                WindowStart         TEXT,
                WindowEnd           TEXT,
                BreakEveryMins      INTEGER NOT NULL DEFAULT 0,
                BreakDurationMins   INTEGER NOT NULL DEFAULT 0,
                Enabled             INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS DailyUsage (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Date        TEXT    NOT NULL,
                ProcessName TEXT    NOT NULL,
                UsageMins   REAL    NOT NULL DEFAULT 0,
                Blocked     INTEGER NOT NULL DEFAULT 0,
                WarningSent INTEGER NOT NULL DEFAULT 0,
                UNIQUE(Date, ProcessName)
            );

            CREATE TABLE IF NOT EXISTS Settings_v1_applied (
                Id INTEGER PRIMARY KEY
            );

            CREATE TABLE IF NOT EXISTS Sessions (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName         TEXT    NOT NULL,
                StartTime           TEXT    NOT NULL,
                EndTime             TEXT,
                TimeSinceBreakMins  REAL    NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS AppRuleDaySchedules (
                RuleId          INTEGER NOT NULL,
                DayOfWeek       INTEGER NOT NULL,
                DailyLimitMins  INTEGER NOT NULL DEFAULT 0,
                WindowStart     TEXT,
                WindowEnd       TEXT,
                PRIMARY KEY (RuleId, DayOfWeek)
            );

            CREATE INDEX IF NOT EXISTS idx_dailyusage_date ON DailyUsage(Date);
            CREATE INDEX IF NOT EXISTS idx_sessions_process ON Sessions(ProcessName, EndTime);
        """);

        // Existing history compatibility columns (predate ScreenTime phases).
        AddColumnIfMissing(conn, tx, "Sessions", "WindowTitle", "TEXT");
        AddColumnIfMissing(conn, tx, "Sessions", "IsPassive",   "INTEGER NOT NULL DEFAULT 0");

        Execute(conn, tx, """
            INSERT INTO AppRuleDaySchedules (RuleId, DayOfWeek, DailyLimitMins, WindowStart, WindowEnd)
            SELECT r.Id, d.DayOfWeek, r.DailyLimitMins, r.WindowStart, r.WindowEnd
            FROM AppRules r
            CROSS JOIN (
                SELECT 0 AS DayOfWeek
                UNION ALL SELECT 1
                UNION ALL SELECT 2
                UNION ALL SELECT 3
                UNION ALL SELECT 4
                UNION ALL SELECT 5
                UNION ALL SELECT 6
            ) d
            WHERE NOT EXISTS (
                SELECT 1
                FROM AppRuleDaySchedules s
                WHERE s.RuleId = r.Id
            );
        """);
        // A collision aborts the whole migration; never silently merge conflicting rules/usage.
        foreach (var table in new[] { "AppRules", "DailyUsage", "Sessions" })
            Execute(conn, tx, $"""
                UPDATE {table} SET ProcessName = lower(trim(ProcessName));
                UPDATE {table} SET ProcessName = substr(ProcessName, 1, length(ProcessName) - 4)
                    WHERE ProcessName LIKE '%.exe';
                """);
        Execute(conn, tx, """
            CREATE UNIQUE INDEX idx_rules_appkey ON AppRules(ProcessName COLLATE NOCASE);
            CREATE UNIQUE INDEX idx_usage_appkey_date ON DailyUsage(Date, ProcessName COLLATE NOCASE);
            PRAGMA user_version=1;
            """);
        }
        if (version < 2) ApplyPhase3(conn, tx);
        ApplyPhase4(conn, tx);
        tx.Commit();
    }

    private static void ApplyPhase4(SqliteConnection conn, SqliteTransaction tx)
    {
        const string sql = """
            ALTER TABLE DailyUsage ADD COLUMN GraceSeconds INTEGER NOT NULL DEFAULT 0 CHECK(typeof(GraceSeconds)='integer' AND GraceSeconds >= 0 AND GraceSeconds <= ObservedSeconds);
            CREATE TABLE GraceEpisodes (
                Id TEXT PRIMARY KEY NOT NULL,
                AppKey TEXT NOT NULL CHECK(length(AppKey)>0 AND AppKey=lower(trim(AppKey)) AND AppKey NOT LIKE '%.exe'),
                QuotaDate TEXT NOT NULL,
                StartedAtUtcTicks INTEGER NOT NULL CHECK(typeof(StartedAtUtcTicks)='integer' AND StartedAtUtcTicks>0),
                ExpiresAtUtcTicks INTEGER NOT NULL CHECK(typeof(ExpiresAtUtcTicks)='integer' AND ExpiresAtUtcTicks>StartedAtUtcTicks),
                Phase INTEGER NOT NULL CHECK(typeof(Phase)='integer' AND Phase IN (0,1,2)),
                EndedAtUtcTicks INTEGER,
                CHECK((Phase=0 AND EndedAtUtcTicks IS NULL) OR (Phase<>0 AND EndedAtUtcTicks IS NOT NULL AND typeof(EndedAtUtcTicks)='integer' AND EndedAtUtcTicks>=StartedAtUtcTicks)),
                UNIQUE(AppKey,QuotaDate), UNIQUE(Id,AppKey)
            );
            CREATE UNIQUE INDEX idx_grace_active_app ON GraceEpisodes(AppKey) WHERE Phase=0;
            CREATE TABLE GraceProcesses (
                EpisodeId TEXT NOT NULL, AppKey TEXT NOT NULL,
                ProcessId INTEGER NOT NULL CHECK(typeof(ProcessId)='integer' AND ProcessId>0),
                StartTimeUtcTicks INTEGER NOT NULL CHECK(typeof(StartTimeUtcTicks)='integer' AND StartTimeUtcTicks>0),
                SessionId INTEGER NOT NULL CHECK(typeof(SessionId)='integer' AND SessionId>=0),
                PRIMARY KEY(EpisodeId,AppKey,ProcessId,StartTimeUtcTicks,SessionId),
                FOREIGN KEY(EpisodeId,AppKey) REFERENCES GraceEpisodes(Id,AppKey)
            );
            PRAGMA user_version=3;
            """;
        // Execute each DDL statement separately so any preparation failure aborts the transaction.
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries)) Execute(conn, tx, statement);
    }

    private static void ApplyPhase3(SqliteConnection conn, SqliteTransaction tx)
    {
        Execute(conn, tx, """
            CREATE TABLE BlockedPeriods (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RuleId INTEGER NOT NULL REFERENCES AppRules(Id) ON DELETE CASCADE,
                StartDayOfWeek INTEGER NOT NULL CHECK(typeof(StartDayOfWeek)='integer' AND StartDayOfWeek BETWEEN 0 AND 6),
                StartMinute INTEGER NOT NULL CHECK(typeof(StartMinute)='integer' AND StartMinute BETWEEN 0 AND 1439),
                EndMinute INTEGER NOT NULL CHECK(typeof(EndMinute)='integer' AND EndMinute BETWEEN 0 AND 1439),
                EndDayOffset INTEGER NOT NULL CHECK(typeof(EndDayOffset)='integer' AND EndDayOffset IN (0,1)),
                Enabled INTEGER NOT NULL DEFAULT 1 CHECK(typeof(Enabled)='integer' AND Enabled IN (0,1)),
                CHECK(typeof(EndDayOffset)='integer' AND EndDayOffset * 1440 + EndMinute - StartMinute BETWEEN 1 AND 1440)
            );
            CREATE INDEX idx_blockedperiods_rule_day ON BlockedPeriods(RuleId, StartDayOfWeek);
            ALTER TABLE DailyUsage ADD COLUMN ObservedSeconds INTEGER NOT NULL DEFAULT 0 CHECK(typeof(ObservedSeconds)='integer' AND ObservedSeconds >= 0);
            ALTER TABLE DailyUsage ADD COLUMN QuotaSeconds INTEGER NOT NULL DEFAULT 0 CHECK(typeof(QuotaSeconds)='integer' AND QuotaSeconds >= 0);
            UPDATE DailyUsage SET ObservedSeconds=CAST(round(max(0,UsageMins)*60) AS INTEGER),
                QuotaSeconds=CAST(round(max(0,UsageMins)*60) AS INTEGER);
            """);
        var rows = conn.Query<(int RuleId, int Day, string? Start, string? End)>(
            "SELECT RuleId, DayOfWeek, WindowStart, WindowEnd FROM AppRuleDaySchedules", transaction: tx).ToArray();
        foreach (var row in rows)
        {
            if (row.Start is null && row.End is null) continue;
            if (!TimeOnly.TryParseExact(row.Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                !TimeOnly.TryParseExact(row.End, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || start > end)
                throw new InvalidOperationException($"Rule {row.RuleId}, day {row.Day}: invalid legacy allowed window; migration rolled back.");
            // Legacy end was inclusive at an instant. Round outward to include its
            // entire final minute; this adds less than 60 seconds of availability.
            var first = start.Hour * 60 + start.Minute;
            var after = end.Hour * 60 + end.Minute + 1;
            void Insert(int begin, int finish, int offset)
            {
                var period = new BlockedPeriod { RuleId = row.RuleId, StartDayOfWeek = (DayOfWeek)row.Day,
                    StartMinute = begin, EndMinute = finish, EndDayOffset = offset };
                period.Validate();
                conn.Execute("""
                    INSERT INTO BlockedPeriods(RuleId,StartDayOfWeek,StartMinute,EndMinute,EndDayOffset,Enabled)
                    VALUES(@RuleId,@StartDayOfWeek,@StartMinute,@EndMinute,@EndDayOffset,@Enabled)
                    """, period, tx);
            }
            if (first > 0) Insert(0, first, 0);
            if (after < 1440) Insert(after, 0, 1);
        }
        Execute(conn, tx, "PRAGMA user_version=2;");
    }
    private static void Execute(SqliteConnection conn, string sql) => Execute(conn, null, sql);

    private static void Execute(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteConnection conn, SqliteTransaction tx, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.Transaction = tx;
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        var exists = (long)(check.ExecuteScalar() ?? 0L);
        if (exists == 0) Execute(conn, tx, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }
}
