using Microsoft.Data.Sqlite;

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
        _connectionString = connectionString;
    }

    public void Migrate()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var versionCommand = conn.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > 1) throw new InvalidOperationException($"Unsupported database schema version {version}.");
        if (version == 1) return;
        // Preserve a consistent pre-upgrade copy of existing ScreenTime profile data.
        using var tablesCommand = conn.CreateCommand();
        tablesCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AppRules'";
        if (Convert.ToInt32(tablesCommand.ExecuteScalar()) > 0 && conn.DataSource != ":memory:")
        {
            var backupPath = conn.DataSource + ".pre-phase2.bak";
            if (!File.Exists(backupPath))
            {
                using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
                backup.Open();
                conn.BackupDatabase(backup);
            }
        }

        // Enable WAL mode for better concurrent read performance
        Execute(conn, "PRAGMA journal_mode=WAL;");
        using var tx = conn.BeginTransaction();

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
        tx.Commit();
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