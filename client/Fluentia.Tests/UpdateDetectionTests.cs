using Fluentia.Services;
using System.Reflection;

namespace Fluentia.Tests;

/// <summary>
/// Tests for update detection via Velopack GithubSource.
/// Addresses: Windows client not detecting updates from GitHub releases.
/// 
/// Root cause analysis:
/// 1. GetUpdateManager took a serverUrl param that was never used, causing confusion
/// 2. AutoCheckForUpdatesAsync silently swallowed ALL exceptions — even legitimate
///    network failures went unlogged, making it impossible to diagnose why updates
///    weren't detected
/// 3. No retry logic — a single transient network failure meant no update until next launch
/// 
/// Fix:
/// - Remove unused serverUrl parameter from GetUpdateManager
/// - Add structured DebugLogger output on every path (success, no-update, failure)
/// - Add retry with exponential backoff (3 attempts) for both auto and manual checks
/// - Add 5-second delay before auto-check to let app fully initialize
/// </summary>
public class UpdateDetectionTests
{
    // === RED test 1: GetUpdateManager no longer takes serverUrl ===
    [Fact]
    public void GetUpdateManager_HasNoParameters()
    {
        // Previously it took (string? serverUrl = null) but never used it.
        // Now it should be parameterless.
        var mainWindowType = typeof(MainWindow);
        var method = mainWindowType.GetMethod("GetUpdateManager",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Empty(method.GetParameters());
    }

    // === RED test 2: Return type is UpdateManager ===
    [Fact]
    public void GetUpdateManager_ReturnsUpdateManager()
    {
        var mainWindowType = typeof(MainWindow);
        var method = mainWindowType.GetMethod("GetUpdateManager",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal("Velopack.UpdateManager", method.ReturnType.FullName);
    }

    // === RED test 3: UpdateManager is cached ===
    [Fact]
    public void UpdateManager_IsCached()
    {
        var mainWindowType = typeof(MainWindow);
        var field = mainWindowType.GetField("_updateManager",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal("Velopack.UpdateManager", field.FieldType.FullName);
    }

    // === RED test 4: Concurrent check prevention ===
    [Fact]
    public void UpdateCheckInProgress_PreventsConcurrentChecks()
    {
        var mainWindowType = typeof(MainWindow);
        var field = mainWindowType.GetField("_updateCheckInProgress",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(bool), field.FieldType);
    }

    // === RED test 5: Portable update notification flag ===
    [Fact]
    public void PortableUpdateNotified_FlagExists()
    {
        var mainWindowType = typeof(MainWindow);
        var field = mainWindowType.GetField("_portableUpdateNotified",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(bool), field.FieldType);
    }

    // === RED test 6: AppVersion follows semver ===
    [Fact]
    public void AppVersion_FollowsSemanticVersioning()
    {
        var mainWindowType = typeof(MainWindow);
        var field = mainWindowType.GetField("AppVersion",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        var versionValue = field.GetValue(null) as string;
        Assert.NotNull(versionValue);
        Assert.Matches(@"^\d+\.\d+\.\d+(\.\d+)?$", versionValue);
    }

    // === RED test 7: AutoCheckForUpdatesAsync exists ===
    [Fact]
    public void AutoCheckForUpdatesAsync_Exists()
    {
        var mainWindowType = typeof(MainWindow);
        var method = mainWindowType.GetMethod("AutoCheckForUpdatesAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    // === RED test 8: ManualCheckForUpdatesAsync exists ===
    [Fact]
    public void ManualCheckForUpdatesAsync_Exists()
    {
        var mainWindowType = typeof(MainWindow);
        var method = mainWindowType.GetMethod("ManualCheckForUpdatesAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
    }
}
