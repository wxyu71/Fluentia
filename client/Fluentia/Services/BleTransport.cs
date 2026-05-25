using System.Text.Json;
using System.Text.Json.Serialization;
using Fluentia.Models;

namespace Fluentia.Services;

/// <summary>
/// BLE transport for PC→mobile and mobile→PC communication.
///
/// Architecture:
/// - PC→mobile: writes to the GATT notify characteristic (via DesktopBlePairingService)
/// - mobile→PC: receives via the write characteristic (already handled by DesktopBlePairingService)
/// - Poll mechanism: mobile sends "poll" requests; PC responds with queued messages
///
/// This class implements IRelayTransport so RoomManager can use it as a second transport
/// alongside WebSocketService.
/// </summary>
public sealed class BleTransport : IRelayTransport
{
    private readonly DesktopBlePairingService _bleService;
    private readonly Queue<WsMessage> _outgoingQueue = new();
    private readonly object _queueLock = new();
    private bool _connected;
    private bool _disposed;

    public BleTransport(DesktopBlePairingService bleService)
    {
        _bleService = bleService ?? throw new ArgumentNullException(nameof(bleService));

        // Register to receive mobile→PC messages from the BLE write characteristic
        _bleService.EncryptedMessageReceived += OnMobileMessage;
        _bleService.PollReceived += OnPollReceived;
    }

    public event Action<WsMessage>? OnMessage;
    public event Action? OnConnected;
    public event Action<string>? OnDisconnected;

    public RelayTransportKind TransportKind => RelayTransportKind.BluetoothLowEnergy;
    public bool IsConnected => _connected && _bleService.IsAdvertising;

    /// <summary>
    /// "Connect" marks the BLE transport as ready. The actual GATT setup
    /// is handled by DesktopBlePairingService.StartAsync().
    /// </summary>
    public Task ConnectAsync(string endpoint)
    {
        _connected = true;
        OnConnected?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Send a message to mobile via BLE notify characteristic.
    /// If the notify characteristic has no subscribers, the message is queued
    /// and sent when mobile sends a poll request.
    /// </summary>
    public async Task SendAsync(WsMessage msg)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var envelope = new BleTransportEnvelope
            {
                Type = "encrypted",
                Payload = msg.Payload,
                Nonce = msg.Nonce,
                Seq = msg.Seq,
            };

            var sent = await _bleService.SendEnvelopeAsync(envelope);
            if (!sent)
            {
                // No BLE subscribers — queue for poll delivery
                lock (_queueLock)
                {
                    _outgoingQueue.Enqueue(msg);
                }
            }
        }
        catch
        {
            // Queue for retry on next poll
            lock (_queueLock)
            {
                _outgoingQueue.Enqueue(msg);
            }
        }
    }

    public void Disconnect()
    {
        if (!_connected) return;
        _connected = false;

        lock (_queueLock)
        {
            _outgoingQueue.Clear();
        }

        OnDisconnected?.Invoke("BLE transport disconnected");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _bleService.EncryptedMessageReceived -= OnMobileMessage;
        _bleService.PollReceived -= OnPollReceived;

        lock (_queueLock)
        {
            _outgoingQueue.Clear();
        }

        Disconnect();
    }

    /// <summary>
    /// Called when mobile sends a "poll" request via BLE write characteristic.
    /// Responds with any queued messages.
    /// </summary>
    private void OnPollReceived(string remotePublicKey)
    {
        lock (_queueLock)
        {
            while (_outgoingQueue.Count > 0)
            {
                var msg = _outgoingQueue.Dequeue();
                var envelope = new BleTransportEnvelope
                {
                    Type = "encrypted",
                    Payload = msg.Payload,
                    Nonce = msg.Nonce,
                    Seq = msg.Seq,
                };

                // Fire and forget — best effort
                _ = _bleService.SendEnvelopeAsync(envelope);
            }
        }
    }

    /// <summary>
    /// Called when mobile sends an encrypted message via BLE write characteristic.
    /// </summary>
    private void OnMobileMessage(WsMessage msg)
    {
        if (!_connected) return;
        OnMessage?.Invoke(msg);
    }
}

/// <summary>
/// JSON envelope for BLE transport messages.
/// </summary>
public class BleTransportEnvelope
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Payload { get; set; }

    [JsonPropertyName("nonce")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Nonce { get; set; }

    [JsonPropertyName("seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seq { get; set; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("approved")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Approved { get; set; }
}
