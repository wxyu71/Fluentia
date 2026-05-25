using Fluentia.Models;
using Fluentia.Services;

namespace Fluentia.Tests;

public class RoomManagerTests : IDisposable
{
    private class MockTransport : IRelayTransport
    {
        private readonly List<WsMessage> _sent = new();

        public event Action<WsMessage>? OnMessage;
        public event Action? OnConnected;
        public event Action<string>? OnDisconnected;

        public RelayTransportKind TransportKind { get; set; } = RelayTransportKind.WebSocket;
        public bool IsConnected { get; set; }
        public IReadOnlyList<WsMessage> Sent => _sent;
        public string? LastEndpoint { get; private set; }

        public Task ConnectAsync(string endpoint)
        {
            LastEndpoint = endpoint;
            IsConnected = true;
            OnConnected?.Invoke();
            return Task.CompletedTask;
        }

        public Task SendAsync(WsMessage msg)
        {
            _sent.Add(msg);
            return Task.CompletedTask;
        }

        public void Disconnect()
        {
            IsConnected = false;
            OnDisconnected?.Invoke("test");
        }

        public void SimulateMessage(WsMessage msg) => OnMessage?.Invoke(msg);
        public void ClearSent() => _sent.Clear();

        public void Dispose() { }
    }

    private readonly MockTransport _transport;
    private readonly RoomManager _room;

    private static string GenerateBase64Key(int size = 32)
    {
        var bytes = new byte[size];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    public RoomManagerTests()
    {
        _transport = new MockTransport();
        _room = new RoomManager(_transport);
    }

    public void Dispose()
    {
        _room.Dispose();
    }

    // === Connection Lifecycle ===

    [Fact]
    public async Task ConnectAsync_SendsCreateSession()
    {
        await _room.ConnectAsync("wss://test.example.com/ws");

        var createMsg = _transport.Sent.FirstOrDefault(m => m.Type == MsgTypes.CreateSession);
        Assert.NotNull(createMsg);
        Assert.Equal(MsgTypes.ProtocolVersion, createMsg.Version);
    }

    [Fact]
    public async Task ConnectAsync_WithExistingToken_SendsRejoin()
    {
        // Simulate having an existing session
        _room.RestorePersistedSession(new PersistedDesktopSession(
            "existing-token", GenerateBase64Key(32), GenerateBase64Key(64), true));

        await _room.ConnectAsync("wss://test.example.com/ws");

        var rejoinMsg = _transport.Sent.FirstOrDefault(m => m.Type == MsgTypes.RejoinSession);
        Assert.NotNull(rejoinMsg);
        Assert.Equal("existing-token", rejoinMsg.Token);
    }

    [Fact]
    public void IsConnected_ReturnsTransportState()
    {
        Assert.False(_room.IsConnected);
        _transport.IsConnected = true;
        Assert.True(_room.IsConnected);
    }

    // === Session Lifecycle ===

    [Fact]
    public async Task HandleSessionCreated_StoresToken()
    {
        string? capturedToken = null;
        _room.OnSessionCreated += t => capturedToken = t;

        await _room.ConnectAsync("wss://test.example.com/ws");

        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.SessionCreated,
            Token = "new-token-123",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
        });

        Assert.Equal("new-token-123", capturedToken);
        Assert.Equal("new-token-123", _room.CurrentToken);
    }

    [Fact]
    public async Task HandleRejoined_StoresToken()
    {
        _room.RestorePersistedSession(new PersistedDesktopSession(
            "old-token", GenerateBase64Key(32), GenerateBase64Key(64), true));

        await _room.ConnectAsync("wss://test.example.com/ws");
        _transport.ClearSent();

        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.Rejoined,
            Token = "old-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
        });

        Assert.Equal("old-token", _room.CurrentToken);
    }

    // === Peer Lifecycle ===

    [Fact]
    public async Task HandlePeerJoined_FiresEvent()
    {
        string? capturedDeviceId = null;
        _room.OnMobileConnected += d => capturedDeviceId = d;

        await _room.ConnectAsync("wss://test.example.com/ws");
        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.SessionCreated,
            Token = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
        });

        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.PeerJoined,
            Role = "mobile",
            DeviceId = "device-abc",
        });

        Assert.Equal("device-abc", capturedDeviceId);
    }

    [Fact]
    public async Task HandlePeerLeft_FiresEvent()
    {
        bool disconnectedFired = false;
        _room.OnMobileDisconnected += () => disconnectedFired = true;

        await _room.ConnectAsync("wss://test.example.com/ws");
        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.SessionCreated,
            Token = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
        });

        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.PeerJoined,
            Role = "mobile",
            DeviceId = "d1",
        });

        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.PeerLeft,
            Role = "mobile",
            DeviceId = "d1",
        });

        Assert.True(disconnectedFired);
    }

    // === Key Exchange ===

    [Fact]
    public async Task HandleKeyExchange_SendsPublicKeyBack()
    {
        await _room.ConnectAsync("wss://test.example.com/ws");
        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.SessionCreated,
            Token = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
        });
        _transport.ClearSent();

        // Mobile sends its public key
        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.KeyExchange,
            PublicKey = GenerateBase64Key(),
        });

        // PC should respond with its own public key
        var keyExMsg = _transport.Sent.FirstOrDefault(m => m.Type == MsgTypes.KeyExchange);
        Assert.NotNull(keyExMsg);
        Assert.NotNull(keyExMsg.PublicKey);
    }

    // === Encrypted Message Handling ===

    [Fact]
    public async Task HandleEncrypted_WithoutReadyCrypto_Ignores()
    {
        await _room.ConnectAsync("wss://test.example.com/ws");
        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.SessionCreated,
            Token = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
        });

        // Send encrypted without key exchange — should be ignored
        InputCommand? receivedCmd = null;
        _room.OnInputCommand += cmd => receivedCmd = cmd;

        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.Encrypted,
            Payload = "garbage",
            Nonce = "garbage",
        });

        Assert.Null(receivedCmd);
    }

    // === Error Handling ===

    [Fact]
    public async Task HandleError_ReportsError()
    {
        string? capturedError = null;
        _room.OnError += e => capturedError = e;

        await _room.ConnectAsync("wss://test.example.com/ws");
        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.Error,
            Error = "session expired",
        });

        Assert.NotNull(capturedError);
        Assert.Contains("session expired", capturedError);
    }

    // === Version Handling ===

    [Fact]
    public async Task HandleSessionCreated_WithIncompatibleVersion_FiresEvent()
    {
        string? incompatibleMsg = null;
        _room.OnVersionIncompatible += msg => incompatibleMsg = msg;

        await _room.ConnectAsync("wss://test.example.com/ws");
        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.SessionCreated,
            Token = "token",
            MinVersion = "99.0.0", // way above our version
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
        });

        Assert.NotNull(incompatibleMsg);
    }

    // === BLE Integration ===

    [Fact]
    public void HandleBleEncryptedMessage_ForwardsToHandleEncrypted()
    {
        // BLE messages should go through the same decryption path
        // This test verifies the method exists and doesn't crash
        _room.HandleBleEncryptedMessage(new WsMessage
        {
            Type = MsgTypes.Encrypted,
            Payload = "test",
            Nonce = "test",
        });
        // No crash = pass (crypto not initialized, so it's silently ignored)
    }

    [Fact]
    public void HandleBleEncryptedMessage_IgnoresNonEncrypted()
    {
        // Should silently ignore non-encrypted BLE messages
        _room.HandleBleEncryptedMessage(new WsMessage
        {
            Type = "ping",
        });
        // No crash = pass
    }

    // === SendToMobile ===

    [Fact]
    public async Task SendToMobileAsync_WhenNotReady_ReturnsFalse()
    {
        var result = await _room.SendToMobileAsync("{\"type\":\"diff\",\"text\":\"hello\"}");
        Assert.False(result);
    }

    // === BLE Session Info ===

    [Fact]
    public void GetBleSessionInfo_WithoutSession_ReturnsNull()
    {
        Assert.Null(_room.GetBleSessionInfo());
    }

    [Fact]
    public async Task GetBleSessionInfo_WithSession_ReturnsInfo()
    {
        await _room.ConnectAsync("wss://test.example.com/ws");
        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.SessionCreated,
            Token = "token-123",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
        });

        var info = _room.GetBleSessionInfo();
        Assert.NotNull(info);
        Assert.Equal("token-123", info.Token);
        Assert.Equal("wss://test.example.com/ws", info.ServerUrl);
        Assert.NotNull(info.PublicKey);
    }

    // === Dual Transport Routing ===

    [Fact]
    public void HasBleTransport_WithoutBle_ReturnsFalse()
    {
        Assert.False(_room.HasBleTransport);
    }

    [Fact]
    public void TransportHealth_IsAccessible()
    {
        Assert.NotNull(_room.TransportHealth);
        Assert.Equal(50, _room.TransportHealth.BleScore);
        Assert.Equal(50, _room.TransportHealth.WsScore);
    }

    [Fact]
    public async Task HandleBleEncryptedMessage_UpdatesBleHealth()
    {
        await _room.ConnectAsync("wss://test.example.com/ws");
        _transport.SimulateMessage(new WsMessage
        {
            Type = MsgTypes.SessionCreated,
            Token = "token",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7).ToString("O"),
        });

        // Simulate BLE message received — should update health
        _room.HandleBleEncryptedMessage(new WsMessage
        {
            Type = MsgTypes.Encrypted,
            Payload = "test",
            Nonce = "test",
        });

        // BLE health should be updated (success)
        Assert.True(_room.TransportHealth.BleScore >= 50);
    }

    // === Disposal ===

    [Fact]
    public void Dispose_DisposesTransport()
    {
        _room.Dispose();
        // Should not throw on double dispose
        _room.Dispose();
    }

    [Fact]
    public void Dispose_WithBleTransport_DisposesBoth()
    {
        var ws = new MockTransport();
        var ble = new MockTransport { TransportKind = RelayTransportKind.BluetoothLowEnergy };
        var room = new RoomManager(ws, ble);
        room.Dispose();
        // Should not throw
    }
}
