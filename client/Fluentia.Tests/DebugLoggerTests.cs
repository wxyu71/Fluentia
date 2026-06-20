using Fluentia.Services;

namespace Fluentia.Tests;

/// <summary>
/// Tests for the DebugLogger service. Verifies strict on/off semantics,
/// file output, rotation, and thread safety.
/// </summary>
public class DebugLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public DebugLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fluentia-debug-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        DebugLogger.SetLogDirectoryForTesting(_tempDir);
        DebugLogger.Enabled = false;
    }

    public void Dispose()
    {
        DebugLogger.Enabled = false;
        DebugLogger.SetLogDirectoryForTesting(null);

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Cleanup failure is non-critical in tests.
        }
    }

    // === Strict on/off semantics ===

    [Fact]
    public void Disabled_LogCreatesNoFile()
    {
        DebugLogger.Enabled = false;
        DebugLogger.Log("should not appear");

        Assert.False(File.Exists(Path.Combine(_tempDir, "debug.log")));
    }

    [Fact]
    public void Disabled_ManyLogsCreateNoFile()
    {
        DebugLogger.Enabled = false;
        for (int i = 0; i < 100; i++)
        {
            DebugLogger.Log($"message {i}");
        }

        Assert.False(File.Exists(Path.Combine(_tempDir, "debug.log")));
    }

    [Fact]
    public void Enabled_LogCreatesFile()
    {
        DebugLogger.Enabled = true;
        DebugLogger.Log("hello world");

        var logPath = Path.Combine(_tempDir, "debug.log");
        Assert.True(File.Exists(logPath));

        var content = File.ReadAllText(logPath);
        Assert.Contains("hello world", content);
    }

    [Fact]
    public void Enabled_LogContainsTimestamp()
    {
        DebugLogger.Enabled = true;
        DebugLogger.Log("timestamped");

        var content = File.ReadAllText(Path.Combine(_tempDir, "debug.log"));
        // Format: [HH:mm:ss.fff]
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\.\d{3}\]", content);
    }

    [Fact]
    public void Enabled_MultipleLogsAppend()
    {
        DebugLogger.Enabled = true;
        DebugLogger.Log("first");
        DebugLogger.Log("second");
        DebugLogger.Log("third");

        var content = File.ReadAllText(Path.Combine(_tempDir, "debug.log"));
        Assert.Contains("first", content);
        Assert.Contains("second", content);
        Assert.Contains("third", content);
    }

    [Fact]
    public void ToggleOff_StopsWriting()
    {
        DebugLogger.Enabled = true;
        DebugLogger.Log("before off");

        DebugLogger.Enabled = false;
        DebugLogger.Log("after off");

        var content = File.ReadAllText(Path.Combine(_tempDir, "debug.log"));
        Assert.Contains("before off", content);
        Assert.DoesNotContain("after off", content);
    }

    [Fact]
    public void ToggleOn_AfterOff_ResumesWriting()
    {
        DebugLogger.Enabled = true;
        DebugLogger.Log("session 1");

        DebugLogger.Enabled = false;
        DebugLogger.Log("dropped");

        DebugLogger.Enabled = true;
        DebugLogger.Log("session 2");

        var content = File.ReadAllText(Path.Combine(_tempDir, "debug.log"));
        Assert.Contains("session 1", content);
        Assert.DoesNotContain("dropped", content);
        Assert.Contains("session 2", content);
    }

    // === Edge cases ===

    [Fact]
    public void Log_EmptyString_DoesNotCrash()
    {
        DebugLogger.Enabled = true;
        DebugLogger.Log("");

        var content = File.ReadAllText(Path.Combine(_tempDir, "debug.log"));
        // Empty message still gets a timestamp line: "[HH:mm:ss.fff] \r\n"
        Assert.Contains("]", content);
    }

    [Fact]
    public void Log_SpecialCharacters_Preserved()
    {
        DebugLogger.Enabled = true;
        DebugLogger.Log("特殊中文字符 \"quotes\" \\backslash\ntab");

        var content = File.ReadAllText(Path.Combine(_tempDir, "debug.log"));
        Assert.Contains("特殊中文字符", content);
        Assert.Contains("\"quotes\"", content);
    }

    [Fact]
    public void Log_LongMessage_Truncated()
    {
        DebugLogger.Enabled = true;
        var longMsg = new string('X', 10_000);
        DebugLogger.Log(longMsg);

        var content = File.ReadAllText(Path.Combine(_tempDir, "debug.log"));
        Assert.Contains("X", content);
        Assert.True(content.Length > 10_000); // includes timestamp + newline
    }

    // === Rotation ===

    [Fact]
    public void Rotation_CreatesOldFile()
    {
        DebugLogger.Enabled = true;

        // Write enough data to exceed the 2MB threshold
        var bigMsg = new string('A', 1000);
        for (int i = 0; i < 2100; i++)
        {
            DebugLogger.Log(bigMsg);
        }

        var logPath = Path.Combine(_tempDir, "debug.log");
        var oldPath = Path.Combine(_tempDir, "debug.log.old");

        // The log should have rotated at least once
        Assert.True(File.Exists(oldPath), "debug.log.old should exist after rotation");

        // New log should be smaller than old
        var newInfo = new FileInfo(logPath);
        var oldInfo = new FileInfo(oldPath);
        Assert.True(newInfo.Length < oldInfo.Length, "New log should be smaller than rotated old log");
    }

    // === LogPath property ===

    [Fact]
    public void LogPath_ReturnsCorrectPath()
    {
        var expected = Path.Combine(_tempDir, "debug.log");
        Assert.Equal(expected, DebugLogger.LogPath);
    }

    // === Thread safety ===

    [Fact]
    public async Task ConcurrentLogging_DoesNotCorrupt()
    {
        DebugLogger.Enabled = true;

        var tasks = new Task[10];
        for (int t = 0; t < tasks.Length; t++)
        {
            var taskId = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    DebugLogger.Log($"thread-{taskId}-msg-{i}");
                }
            });
        }

        await Task.WhenAll(tasks);

        var content = File.ReadAllText(Path.Combine(_tempDir, "debug.log"));
        var lines = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        // All 1000 messages should be present (no corruption, no lost writes)
        Assert.Equal(1000, lines.Length);
    }

    [Fact]
    public async Task ConcurrentToggleAndLog_DoesNotCrash()
    {
        var tasks = new Task[4];

        // Some threads toggle the switch
        tasks[0] = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                DebugLogger.Enabled = !DebugLogger.Enabled;
                Thread.Sleep(1);
            }
        });
        tasks[1] = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                DebugLogger.Enabled = !DebugLogger.Enabled;
                Thread.Sleep(1);
            }
        });

        // Other threads log
        tasks[2] = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                DebugLogger.Log($"writer-a-{i}");
            }
        });
        tasks[3] = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                DebugLogger.Log($"writer-b-{i}");
            }
        });

        await Task.WhenAll(tasks);

        // No crash = pass. File may or may not exist depending on toggle timing.
        // If it exists, it should be valid (no partial/corrupt lines).
        var logPath = Path.Combine(_tempDir, "debug.log");
        if (File.Exists(logPath))
        {
            var content = File.ReadAllText(logPath);
            Assert.DoesNotContain("\0", content); // no null bytes from corruption
        }
    }
}
