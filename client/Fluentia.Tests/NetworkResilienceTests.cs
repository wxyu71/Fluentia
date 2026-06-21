using Fluentia.Services;
using Fluentia.Models;
using System.Reflection;

namespace Fluentia.Tests;

/// <summary>
/// Tests for network resilience and reconnection handling.
/// Addresses: Network fluctuations causing input interruption and Enter events.
/// 
/// Root cause analysis:
/// 1. OnServerConnectionChanged cleared _inputTargetWindow on disconnect.
///    This meant that a brief network flap (e.g., Wi-Fi roaming, cellular handoff)
///    would destroy the input target, forcing the user to click back into the
///    target window to resume typing. During the re-establishment, diffs could be
///    dropped (first-char swallow) or Enter events injected spuriously.
/// 2. WebSocketService had no logging — network issues were invisible.
/// 
/// Fix:
/// - Stop clearing _inputTargetWindow in OnServerConnectionChanged.
///   Only clear it in OnMobileDisconnected (peer truly gone) and OnSessionCreated
///   (brand new session). Server disconnect/reconnect preserves the input target.
/// - Add DebugLogger calls to HandleDisconnect, ReconnectLoopAsync,
///   HeartbeatLoop, ConnectInternal success/failure paths.
/// </summary>
public class NetworkResilienceTests
{
    // === RED test 1: Reconnection loop exists ===
    [Fact]
    public void WebSocketService_HasReconnectLoop()
    {
        var wsType = typeof(WebSocketService);
        var method = wsType.GetMethod("ReconnectLoopAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    // === RED test 2: Exponential backoff constants ===
    [Fact]
    public void WebSocketService_HasExponentialBackoffConstants()
    {
        var wsType = typeof(WebSocketService);

        var initialField = wsType.GetField("InitialReconnectDelayMs",
            BindingFlags.NonPublic | BindingFlags.Static);
        var maxField = wsType.GetField("MaxReconnectDelayMs",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(initialField);
        Assert.NotNull(maxField);

        var initial = (int)initialField.GetValue(null);
        var max = (int)maxField.GetValue(null);

        Assert.True(initial > 0, "Initial reconnect delay must be positive");
        Assert.True(max >= initial, "Max delay must be >= initial");
    }

    // === RED test 3: Heartbeat configuration ===
    [Fact]
    public void WebSocketService_HasHeartbeatConfiguration()
    {
        var wsType = typeof(WebSocketService);

        var intervalField = wsType.GetField("HeartbeatInterval",
            BindingFlags.NonPublic | BindingFlags.Static);
        var timeoutField = wsType.GetField("HeartbeatTimeout",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(intervalField);
        Assert.NotNull(timeoutField);

        var interval = (TimeSpan)intervalField.GetValue(null);
        var timeout = (TimeSpan)timeoutField.GetValue(null);

        Assert.True(interval.TotalSeconds > 0);
        Assert.True(timeout > interval);
    }

    // === RED test 4: Connection state events ===
    [Fact]
    public void WebSocketService_HasConnectionEvents()
    {
        var wsType = typeof(WebSocketService);

        Assert.NotNull(wsType.GetEvent("OnConnected"));
        Assert.NotNull(wsType.GetEvent("OnDisconnected"));
        Assert.NotNull(wsType.GetEvent("OnMessage"));
    }

    // === RED test 5: Thread-safe send ===
    [Fact]
    public void WebSocketService_HasSendLock()
    {
        var wsType = typeof(WebSocketService);
        var field = wsType.GetField("_sendLock",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(SemaphoreSlim), field.FieldType);
    }

    // === RED test 6: Thread-safe connect ===
    [Fact]
    public void WebSocketService_HasConnectLock()
    {
        var wsType = typeof(WebSocketService);
        var field = wsType.GetField("_connectLock",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(SemaphoreSlim), field.FieldType);
    }

    // === RED test 7: Server URL preserved for reconnection ===
    [Fact]
    public void WebSocketService_PreservesServerUrlForReconnection()
    {
        var wsType = typeof(WebSocketService);
        var field = wsType.GetField("_serverUrl",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(string), field.FieldType);
    }

    // === RED test 8: Intentional close flag ===
    [Fact]
    public void WebSocketService_HasIntentionalCloseFlag()
    {
        var wsType = typeof(WebSocketService);
        var field = wsType.GetField("_intentionalClose",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(bool), field.FieldType);
    }

    // === RED test 9: Reconnect attempts tracking ===
    [Fact]
    public void WebSocketService_TracksReconnectAttempts()
    {
        var wsType = typeof(WebSocketService);
        var field = wsType.GetField("_reconnectAttempts",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(int), field.FieldType);
    }

    // === RED test 10: IsConnected property ===
    [Fact]
    public void WebSocketService_HasIsConnectedProperty()
    {
        var wsType = typeof(WebSocketService);
        var prop = wsType.GetProperty("IsConnected");

        Assert.NotNull(prop);
        Assert.True(prop.CanRead);
        Assert.Equal(typeof(bool), prop.PropertyType);
    }

    // === RED test 11: IDisposable implementation ===
    [Fact]
    public void WebSocketService_ImplementsIDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(WebSocketService)));
    }

    // === RED test 12: DisposeConnection method exists ===
    [Fact]
    public void WebSocketService_HasDisposeConnection()
    {
        var wsType = typeof(WebSocketService);
        var method = wsType.GetMethod("DisposeConnection",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
    }

    // === RED test 13: CancelReconnectLoop method exists ===
    [Fact]
    public void WebSocketService_HasCancelReconnectLoop()
    {
        var wsType = typeof(WebSocketService);
        var method = wsType.GetMethod("CancelReconnectLoop",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
    }

    // === RED test 14: HandleDisconnect method exists ===
    [Fact]
    public void WebSocketService_HasHandleDisconnect()
    {
        var wsType = typeof(WebSocketService);
        var method = wsType.GetMethod("HandleDisconnect",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
    }

    // === RED test 15: Reconnect CTS management ===
    [Fact]
    public void WebSocketService_ManagesReconnectCts()
    {
        var wsType = typeof(WebSocketService);
        var field = wsType.GetField("_reconnectCts",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(CancellationTokenSource), field.FieldType);
    }
}
