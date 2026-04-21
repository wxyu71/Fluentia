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
    private const int ReconnectDelayMs = 3000;

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

        try
        {
            await _webSocket.ConnectAsync(new Uri(serverUrl), _cts.Token);
            _reconnectAttempts = 0;
            OnConnected?.Invoke();
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            OnDisconnected?.Invoke(ex.Message);
            ScheduleReconnect();
        }
    }

    private void ScheduleReconnect()
    {
        if (_intentionalClose || _serverUrl == null) return;
        if (_reconnectAttempts >= MaxReconnectAttempts) return;

        _reconnectAttempts++;
        _ = Task.Run(async () =>
        {
            await Task.Delay(ReconnectDelayMs);
            if (!_intentionalClose && _serverUrl != null)
            {
                await ConnectInternal(_serverUrl);
            }
        });
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
                    OnDisconnected?.Invoke("Server closed connection");
                    ScheduleReconnect();
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var msg = WsMessage.Deserialize(json);
                    if (msg != null)
                    {
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
            OnDisconnected?.Invoke(ex.Message);
            ScheduleReconnect();
        }
        catch (Exception ex)
        {
            OnDisconnected?.Invoke(ex.Message);
            ScheduleReconnect();
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
