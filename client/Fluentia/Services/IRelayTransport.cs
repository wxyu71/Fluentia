using Fluentia.Models;

namespace Fluentia.Services;

public enum RelayTransportKind
{
    WebSocket,
    BluetoothLowEnergy,
}

public interface IRelayTransport : IDisposable
{
    event Action<WsMessage>? OnMessage;
    event Action? OnConnected;
    event Action<string>? OnDisconnected;

    RelayTransportKind TransportKind { get; }
    bool IsConnected { get; }

    Task ConnectAsync(string endpoint);
    Task SendAsync(WsMessage msg);
    void Disconnect();
}