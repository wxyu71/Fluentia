using System.Net.WebSockets;
using System.Text;
using Fluentia.Models;

namespace Fluentia.Services;

public class WebSocketService : IDisposable
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private string? _serverUrl;
    private bool _intentionalClose;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 10;
    private const int InitialReconnectDelayMs = 200;
    private const int MaxReconnectDelayMs = 1500;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMilliseconds(2500);
    private DateTime _lastServerActivityUtc = DateTime.UtcNow;
    private int _disconnectHandled;

    public event Action<WsMessage>? OnMessage;
    public event Action? OnConnected;
    public event Action<string>? OnDisconnected;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string serverUrl)
    {
        _serverUrl = serverUrl;
        _intentionalClose = false;
        _reconnectAttempts = 0;
        await ConnectInternal(serverUrl);
    }

    private async Task ConnectInternal(string serverUrl)
    {
        Dispose(disposeUrl: false);
        _cts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _lastServerActivityUtc = DateTime.UtcNow;
        _disconnectHandled = 0;

        try
        {
            await _webSocket.ConnectAsync(new Uri(serverUrl), _cts.Token);
            _reconnectAttempts = 0;
            _lastServerActivityUtc = DateTime.UtcNow;
            OnConnected?.Invoke();
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
            _ = Task.Run(() => HeartbeatLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            HandleDisconnect(ex.Message);
        }
    }

    private void ScheduleReconnect()
    {
        if (_intentionalClose || _serverUrl == null) return;
        if (_reconnectAttempts >= MaxReconnectAttempts) return;

        _reconnectAttempts++;
        var delayMs = GetReconnectDelayMs(_reconnectAttempts);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            if (!_intentionalClose && _serverUrl != null)
            {
                await ConnectInternal(_serverUrl);
            }
        });
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
        ScheduleReconnect();
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

    private void Dispose(bool disposeUrl)
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

    public void Dispose()
    {
        _intentionalClose = true;
        Dispose(disposeUrl: true);
    }
}
