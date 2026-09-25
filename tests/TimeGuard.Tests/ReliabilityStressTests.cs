using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using TimeGuard.Models;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class ReliabilityStressTests
{
    [Fact]
    public async Task RepeatedObservations_KeepUsageExact_AndCloseEveryOwnedSession()
    {
        using var p = new TempProfile(); var db = new DatabaseService(p.Runtime.Paths); var clock = new TestClock();
        var helper = new ProcessInstance("helper", 123, 456, 1); var running = true;
        var durations = new List<double>();
        await using var monitor = new MonitorService(db, new(), new() { Rules = [new() { ProcessName = "helper", DailyLimitMinutes = 60 }] },
            processes: new FakeProcesses(() => running ? [helper] : []), terminator: new FakeTerminator(), time: clock);
        await monitor.TickAsync();
        for (var i = 0; i < 1000; i++)
        {
            // Measured continuity for ten ticks, then a confirmed exit and a new observation.
            clock.Now = clock.Now.AddSeconds(1);
            var timer = Stopwatch.StartNew(); await monitor.TickAsync(); durations.Add(timer.Elapsed.TotalMilliseconds);
            if (i % 10 == 9)
            {
                running = false; await monitor.TickAsync(); running = true; await monitor.TickAsync();
            }
        }
        await monitor.StopAsync();
        var usage = Assert.Single(db.LoadLog(DateOnly.FromDateTime(clock.Now.DateTime)).Entries);
        Assert.Equal(1000, usage.QuotaSeconds); Assert.Equal(1000, usage.ObservedSeconds); Assert.Equal(0, usage.GraceSeconds);
        Assert.Empty(db.LoadGraceEpisodes());
        using var connection = new SqliteConnection($"Data Source={p.Runtime.Paths.DatabasePath}"); connection.Open();
        // Session history uses wall-clock timestamps; the measured-usage clock is deliberately accelerated.
        Assert.Equal(101, connection.ExecuteScalar<int>("SELECT count(*) FROM Sessions"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT count(*) FROM Sessions WHERE EndTime IS NULL"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "phase7-tick-stress.json"),
            JsonSerializer.Serialize(new { Observations = 1000, TotalObservations = 1201, Configuration = "Fake time/process; real SQLite; unlimited-speed stress; one app; 100 exits/restarts",
                MeanMilliseconds = durations.Average(), MaximumMilliseconds = durations.Max(),
                P95Milliseconds = durations.Order().ElementAt(949) }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
