using System.Net.WebSockets;
using System.Text;
using Fluentia.Models;

namespace Fluentia.Services;

public class WebSocketService : IDisposable
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<WsMessage>? OnMessage;
    public event Action? OnConnected;
    public event Action<string>? OnDisconnected;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string serverUrl)
    {
        Dispose();
        _cts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();

        try
        {
            await _webSocket.ConnectAsync(new Uri(serverUrl), _cts.Token);
            OnConnected?.Invoke();
            _ = Task.Run(() => ReceiveLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            OnDisconnected?.Invoke(ex.Message);
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
                    OnDisconnected?.Invoke("Server closed connection");
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
        }
        catch (Exception ex)
        {
            OnDisconnected?.Invoke(ex.Message);
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

    public void Dispose()
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
    }
}
