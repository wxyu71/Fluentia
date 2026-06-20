using System.IO;

namespace Fluentia.Services;

/// <summary>
/// Thread-safe file-based debug logger with strict on/off semantics.
/// When <see cref="Enabled"/> is false, <see cref="Log"/> returns immediately
/// with zero allocations — a single volatile bool read.
/// </summary>
public static class DebugLogger
{
    private static volatile bool _enabled;
    private static readonly object _lock = new();
    private static string? _logDir;
    private static string? _logPath;
    private const long MaxLogBytes = 2 * 1024 * 1024; // 2 MB rotation threshold

    /// <summary>
    /// Whether debug logging is active. Setting to false guarantees zero
    /// overhead on subsequent <see cref="Log"/> calls.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value)
            {
                EnsureInitialized();
            }
        }
    }

    /// <summary>Full path to the current debug log file.</summary>
    public static string LogPath
    {
        get
        {
            EnsureInitialized();
            return _logPath!;
        }
    }

    /// <summary>
    /// Override the log directory for testing. Pass null to reset to default.
    /// Must be called before <see cref="Enabled"/> is set to true.
    /// </summary>
    internal static void SetLogDirectoryForTesting(string? directory)
    {
        lock (_lock)
        {
            if (directory == null)
            {
                _logDir = null;
                _logPath = null;
            }
            else
            {
                _logDir = directory;
                _logPath = Path.Combine(directory, "debug.log");
            }
        }
    }

    private static void EnsureInitialized()
    {
        if (_logDir != null) return;

        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Fluentia", "logs");
        _logPath = Path.Combine(_logDir, "debug.log");
    }

    /// <summary>
    /// Write a timestamped line to the debug log.
    /// Returns immediately (zero cost) when <see cref="Enabled"/> is false.
    /// </summary>
    public static void Log(string message)
    {
        // Fast path: single volatile read, no lock, no allocation.
        if (!_enabled) return;

        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";

            lock (_lock)
            {
                EnsureInitialized();
                Directory.CreateDirectory(_logDir!);
                RotateIfNeeded();
                File.AppendAllText(_logPath!, line);
            }
        }
        catch
        {
            // Logging must never crash the application.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_logPath)) return;

            var info = new FileInfo(_logPath);
            if (info.Length < MaxLogBytes) return;

            var oldPath = Path.Combine(_logDir!, "debug.log.old");
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            File.Move(_logPath!, oldPath);
        }
        catch
        {
            // Rotation failure is non-critical; keep appending.
        }
    }
}
