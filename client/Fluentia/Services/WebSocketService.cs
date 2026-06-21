using System.IO;
using System.Net.WebSockets;
using System.Text;
using Fluentia.Models;

namespace Fluentia.Services;

public class WebSocketService : IRelayTransport
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _reconnectSync = new();
    private string? _serverUrl;
    private bool _intentionalClose;
    private int _reconnectAttempts;
    private const int InitialReconnectDelayMs = 200;
    private const int MaxReconnectDelayMs = 5000;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(10);
    private long _lastServerActivityUtcTicks = DateTime.UtcNow.Ticks;
    private int _disconnectHandled;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectLoopTask;
    private Task? _receiveTask;
    private Task? _heartbeatTask;

    public event Action<WsMessage>? OnMessage;
    public event Action? OnConnected;
    public event Action<string>? OnDisconnected;

    public RelayTransportKind TransportKind => RelayTransportKind.WebSocket;
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    private DateTime LastServerActivityUtc
    {
        get => new DateTime(Volatile.Read(ref _lastServerActivityUtcTicks), DateTimeKind.Utc);
        set => Volatile.Write(ref _lastServerActivityUtcTicks, value.Ticks);
    }

    public async Task ConnectAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        _serverUrl = serverUrl;
        _intentionalClose = false;
        _reconnectAttempts = 0;
        CancelReconnectLoop();
        await ConnectInternal(serverUrl, cancellationToken, notifyDisconnect: true);
    }

    private async Task<bool> ConnectInternal(string serverUrl, CancellationToken cancellationToken, bool notifyDisconnect)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            DisposeConnection(disposeUrl: false);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _webSocket = new ClientWebSocket();
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            LastServerActivityUtc = DateTime.UtcNow;

            await _webSocket.ConnectAsync(new Uri(serverUrl), _cts.Token);
            _reconnectAttempts = 0;
            _disconnectHandled = 0;
            LastServerActivityUtc = DateTime.UtcNow;
            DebugLogger.Log($"WS: connected to {serverUrl}");
            OnConnected?.Invoke();
            _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoop(_cts.Token));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _intentionalClose)
        {
            return false;
        }
        catch (Exception ex)
        {
            DisposeConnection(disposeUrl: false);
            DebugLogger.Log($"WS: connection failed: {ex.Message}");
            if (notifyDisconnect)
            {
                HandleDisconnect(ex.Message);
            }

            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void EnsureReconnectLoop()
    {
        if (_intentionalClose || string.IsNullOrWhiteSpace(_serverUrl))
        {
            return;
        }

        lock (_reconnectSync)
        {
            if (_reconnectLoopTask is { IsCompleted: false })
            {
                return;
            }

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();
            _reconnectLoopTask = Task.Run(() => ReconnectLoopAsync(_reconnectCts.Token));
        }
    }

    private static int GetReconnectDelayMs(int attempt)
    {
        var factor = Math.Pow(2, Math.Max(0, attempt - 1));
        return (int)Math.Min(InitialReconnectDelayMs * factor, MaxReconnectDelayMs);
    }

    private void HandleDisconnect(string reason)
    {
        if (_intentionalClose)
        {
            return;
        }

        if (System.Threading.Interlocked.Exchange(ref _disconnectHandled, 1) == 1)
        {
            return;
        }

        DebugLogger.Log($"WS: disconnected: {reason}");
        OnDisconnected?.Invoke(reason);
        EnsureReconnectLoop();
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_intentionalClose && !string.IsNullOrWhiteSpace(_serverUrl))
            {
                _reconnectAttempts++;
                var delayMs = GetReconnectDelayMs(_reconnectAttempts);
                DebugLogger.Log($"WS: reconnect attempt {_reconnectAttempts} in {delayMs}ms");
                await Task.Delay(delayMs, cancellationToken);

                if (_serverUrl == null || _intentionalClose)
                {
                    return;
                }

                if (await ConnectInternal(_serverUrl, cancellationToken, notifyDisconnect: false))
                {
                    DebugLogger.Log($"WS: reconnected successfully on attempt {_reconnectAttempts}");
                    return;
                }
                else
                {
                    DebugLogger.Log($"WS: reconnect attempt {_reconnectAttempts} failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_reconnectSync)
            {
                if (_reconnectLoopTask?.Id == Task.CurrentId)
                {
                    _reconnectLoopTask = null;
                    _reconnectCts?.Dispose();
                    _reconnectCts = null;
                }
            }
        }
    }

    private void CancelReconnectLoop()
    {
        lock (_reconnectSync)
        {
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = null;
            _reconnectLoopTask = null;
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        HandleDisconnect("Server closed connection");
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        continue; // skip binary frames
                    }

                    messageStream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text && messageStream.Length > 0)
                {
                    var json = Encoding.UTF8.GetString(messageStream.ToArray());
                    var msg = WsMessage.Deserialize(json);
                    if (msg != null)
                    {
                        LastServerActivityUtc = DateTime.UtcNow;
                        if (msg.Type == MsgTypes.Pong)
                        {
                            continue;
                        }
                        OnMessage?.Invoke(msg);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (WebSocketException ex)
        {
            HandleDisconnect(ex.Message);
        }
        catch (Exception ex)
        {
            HandleDisconnect(ex.Message);
        }
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, ct);
                if (_webSocket?.State != WebSocketState.Open)
                {
                    return;
                }

                if (DateTime.UtcNow - LastServerActivityUtc > HeartbeatTimeout)
                {
                    DebugLogger.Log($"WS: heartbeat timeout, last activity {DateTime.UtcNow - LastServerActivityUtc} ago");
                    try
                    {
                        _webSocket.Abort();
                    }
                    catch
                    {
                        // Safe to ignore: Abort may throw if socket is already closing
                    }

                    HandleDisconnect("Heartbeat timeout");
                    return;
                }

                await SendAsync(new WsMessage { Type = MsgTypes.Ping });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"WS: heartbeat error: {ex.Message}");
            HandleDisconnect(ex.Message);
        }
    }

    public async Task SendAsync(WsMessage msg, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_webSocket?.State != WebSocketState.Open) return;

            var json = msg.Serialize();
            var bytes = Encoding.UTF8.GetBytes(json);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _cts?.Token ?? CancellationToken.None);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                linkedCts.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void DisposeConnection(bool disposeUrl)
    {
        _cts?.Cancel();
        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    _webSocket.Abort();
                }
            }
            catch { /* ignore */ }
            _webSocket.Dispose();
            _webSocket = null;
        }
        var tasks = new[] { _receiveTask, _heartbeatTask }.Where(t => t != null).ToArray();
        if (tasks.Length > 0)
        {
            try { Task.WhenAll(tasks!).Wait(TimeSpan.FromSeconds(2)); }
            catch { /* ignore timeout */ }
        }
        _cts?.Dispose();
        _cts = null;
        if (disposeUrl) _serverUrl = null;
    }

    public void Disconnect()
    {
        _intentionalClose = true;
        CancelReconnectLoop();
        DisposeConnection(disposeUrl: false);
        OnDisconnected?.Invoke("Disconnected");
    }

    public void Dispose()
    {
        _intentionalClose = true;
        CancelReconnectLoop();
        DisposeConnection(disposeUrl: true);
        _sendLock.Dispose();
        _connectLock.Dispose();
    }
}
