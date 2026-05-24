using System.IO;

namespace Fluentia.Services;

/// <summary>
/// Simple file logger for BLE diagnostics. Writes to ble-debug.log
/// next to the running executable.
/// </summary>
public static class BleLog
{
    private static readonly object Lock = new();
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "ble-debug.log");

    public static void Write(string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
            lock (Lock)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Never let logging crash the app.
        }
    }
}
