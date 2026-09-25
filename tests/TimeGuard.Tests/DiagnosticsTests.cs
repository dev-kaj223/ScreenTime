using System.Text.Json;
using TimeGuard.Services;
using Xunit;

namespace TimeGuard.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void ConcurrentOversizedFailures_RemainParseableAndRetainOnlyOneBoundedBackup()
    {
        using var profile = new TempProfile();
        var logger = new JsonFileLogger(profile.Runtime, maxBytes: 4096);
        var error = new IOException(new string('x', 100000), new IOException(new string('y', 100000)));
        Parallel.For(0, 200, _ => logger.Write("Error", "StressFailure", error));
        var files = Directory.GetFiles(profile.Runtime.Paths.LogsDirectory);
        Assert.Equal(2, files.Length);
        foreach (var file in files)
        {
            Assert.InRange(new FileInfo(file).Length, 1, 40000); // One bounded record may exceed the rotation threshold.
            foreach (var line in File.ReadLines(file))
            {
                using var record = JsonDocument.Parse(line);
                Assert.Equal(16384, record.RootElement.GetProperty("exception").GetProperty("message").GetString()!.Length);
            }
        }
    }

    [Fact]
    public void Logger_RecordsStructuredExceptionAndRunContext()
    {
        using var profile = new TempProfile();
        Exception error;
        try { throw new InvalidOperationException("diagnostic failure", new IOException("inner failure")); }
        catch (Exception ex) { error = ex; }
        new JsonFileLogger(profile.Runtime).Write("Error", "DiagnosticTest", error);
        using var record = JsonDocument.Parse(File.ReadAllText(profile.Runtime.Paths.LogPath));
        var root = record.RootElement;
        Assert.Equal(TimeSpan.Zero, root.GetProperty("timestampUtc").GetDateTimeOffset().Offset);
        Assert.Equal("Error", root.GetProperty("level").GetString());
        Assert.Equal("DiagnosticTest", root.GetProperty("eventName").GetString());
        Assert.Equal(profile.Runtime.RunId, root.GetProperty("runId").GetString());
        Assert.Equal("Test", root.GetProperty("profile").GetString());
        var exception = root.GetProperty("exception");
        Assert.Equal(typeof(InvalidOperationException).FullName, exception.GetProperty("type").GetString());
        Assert.Equal("diagnostic failure", exception.GetProperty("message").GetString());
        Assert.Contains(nameof(Logger_RecordsStructuredExceptionAndRunContext), exception.GetProperty("stackTrace").GetString());
        Assert.Equal(typeof(IOException).FullName, exception.GetProperty("innerType").GetString());
    }

    [Fact]
    public void UnwritableLogDirectory_DoesNotThrowOrWriteOutsideProfile()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(profile.Runtime.Paths.Root);
        File.WriteAllText(profile.Runtime.Paths.LogsDirectory, "path deliberately occupied by a file");
        var logger = new JsonFileLogger(profile.Runtime);
        Assert.Null(Record.Exception(() => logger.Write("Error", "FailureTest", new IOException("test failure"))));
        Assert.Single(Directory.GetFiles(profile.Runtime.Paths.Root));
    }

    [Fact]
    public void Logger_RotatesToOneBoundedBackup()
    {
        using var profile = new TempProfile();
        var logger = new JsonFileLogger(profile.Runtime, maxBytes: 1);
        for (var i = 0; i < 10; i++) logger.Write("Information", "RotationTest");
        Assert.Equal(2, Directory.GetFiles(profile.Runtime.Paths.LogsDirectory).Length);
        Assert.Single(File.ReadAllLines(profile.Runtime.Paths.LogPath));
        Assert.Single(File.ReadAllLines(profile.Runtime.Paths.LogPath + ".1"));
    }
}
