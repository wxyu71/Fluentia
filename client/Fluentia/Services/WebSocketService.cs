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
    private DateTime _lastServerActivityUtc = DateTime.UtcNow;
    private int _disconnectHandled;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectLoopTask;

    public event Action<WsMessage>? OnMessage;
    public event Action? OnConnected;
    public event Action<string>? OnDisconnected;

    public RelayTransportKind TransportKind => RelayTransportKind.WebSocket;
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string serverUrl)
    {
        _serverUrl = serverUrl;
        _intentionalClose = false;
        _reconnectAttempts = 0;
        CancelReconnectLoop();
        await ConnectInternal(serverUrl, CancellationToken.None, notifyDisconnect: true);
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
            _lastServerActivityUtc = DateTime.UtcNow;

            await _webSocket.ConnectAsync(new Uri(serverUrl), _cts.Token);
            _reconnectAttempts = 0;
            _disconnectHandled = 0;
            _lastServerActivityUtc = DateTime.UtcNow;
            OnConnected?.Invoke();
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
            _ = Task.Run(() => HeartbeatLoop(_cts.Token));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _intentionalClose)
        {
            return false;
        }
        catch (Exception ex)
        {
            DisposeConnection(disposeUrl: false);
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
                await Task.Delay(delayMs, cancellationToken);

                if (_serverUrl == null || _intentionalClose)
                {
                    return;
                }

                if (await ConnectInternal(_serverUrl, cancellationToken, notifyDisconnect: false))
                {
                    return;
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
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    HandleDisconnect("Server closed connection");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var msg = WsMessage.Deserialize(json);
                    if (msg != null)
                    {
                        _lastServerActivityUtc = DateTime.UtcNow;
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

                if (DateTime.UtcNow - _lastServerActivityUtc > HeartbeatTimeout)
                {
                    try
                    {
                        _webSocket.Abort();
                    }
                    catch
                    {
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
            HandleDisconnect(ex.Message);
        }
    }

    public async Task SendAsync(WsMessage msg)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        await _sendLock.WaitAsync();
        try
        {
            var json = msg.Serialize();
            var bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                _cts?.Token ?? CancellationToken.None);
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
                    _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "closing",
                        CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
                }
            }
            catch { /* ignore */ }
            _webSocket.Dispose();
            _webSocket = null;
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
    }
}
