using System.Text.Json;
using Fluentia.Models;

namespace Fluentia.Services;

/// <summary>
/// Manages the room lifecycle: connect to server, create room, handle messages, key exchange.
/// </summary>
public class RoomManager : IDisposable
{
    private readonly WebSocketService _ws;
    private readonly CryptoService _crypto;
    private string? _currentToken;
    private string? _serverUrl;

    public event Action<string>? OnRoomCreated;       // token
    public event Action<string>? OnMobileConnected;    // deviceId
    public event Action? OnMobileDisconnected;
    public event Action? OnEncryptionReady;
    public event Action<InputCommand>? OnInputCommand;
    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnError;

    public string? CurrentToken => _currentToken;
    public string? ServerUrl => _serverUrl;
    public bool EncryptionReady => _crypto.IsReady;

    public RoomManager()
    {
        _ws = new WebSocketService();
        _crypto = new CryptoService();

        _ws.OnConnected += () =>
        {
            OnStatusChanged?.Invoke("Connected to server");
            _ = _ws.SendAsync(new WsMessage { Type = MsgTypes.CreateRoom, Version = MsgTypes.ProtocolVersion });
        };

        _ws.OnDisconnected += (reason) =>
        {
            OnStatusChanged?.Invoke($"Disconnected: {reason}");
        };

        _ws.OnMessage += HandleMessage;
    }

    public async Task ConnectAsync(string serverUrl)
    {
        _serverUrl = serverUrl;
        _crypto.Reset();
        OnStatusChanged?.Invoke("Connecting...");
        await _ws.ConnectAsync(serverUrl);
    }

    public async Task RefreshRoom()
    {
        _crypto.Reset();
        if (_ws.IsConnected)
        {
            await _ws.SendAsync(new WsMessage { Type = MsgTypes.CreateRoom, Version = MsgTypes.ProtocolVersion });
        }
    }

    /// <summary>
    /// Generates the QR code data string containing server URL, token, and public key.
    /// </summary>
    public string? GetQRData()
    {
        if (_currentToken == null || _serverUrl == null) return null;

        var data = new
        {
            s = _serverUrl,
            t = _currentToken,
            k = _crypto.PublicKeyBase64,
        };
        return JsonSerializer.Serialize(data);
    }

    private void HandleMessage(WsMessage msg)
    {
        switch (msg.Type)
        {
            case MsgTypes.RoomCreated:
                _currentToken = msg.Token;
                OnRoomCreated?.Invoke(msg.Token!);
                OnStatusChanged?.Invoke($"Room ready: {msg.Token?[..8]}...");
                break;

            case MsgTypes.PeerJoined:
                OnMobileConnected?.Invoke(msg.DeviceId ?? "unknown");
                OnStatusChanged?.Invoke($"Mobile connected: {msg.DeviceId?[..8]}...");
                break;

            case MsgTypes.PeerLeft:
                _crypto.Reset();
                // Re-generate key pair but keep the room
                OnMobileDisconnected?.Invoke();
                OnStatusChanged?.Invoke("Mobile disconnected");
                break;

            case MsgTypes.KeyExchange:
                HandleKeyExchange(msg);
                break;

            case MsgTypes.Encrypted:
                HandleEncrypted(msg);
                break;

            case MsgTypes.Error:
                OnError?.Invoke(msg.Error ?? "Unknown error");
                break;
        }
    }

    private void HandleKeyExchange(WsMessage msg)
    {
        if (msg.PublicKey == null) return;

        _crypto.SetPeerPublicKey(msg.PublicKey);
        OnEncryptionReady?.Invoke();
        OnStatusChanged?.Invoke("E2E encryption established 🔒");

        // Send our public key back as confirmation
        _ = _ws.SendAsync(new WsMessage
        {
            Type = MsgTypes.KeyExchange,
            PublicKey = _crypto.PublicKeyBase64,
        });
    }

    private void HandleEncrypted(WsMessage msg)
    {
        if (msg.Payload == null || msg.Nonce == null) return;
        if (!_crypto.IsReady) return;

        try
        {
            string plaintext;
            if (msg.Seq.HasValue && _crypto.RatchetReady)
            {
                // Ratcheted decryption (forward secrecy)
                plaintext = _crypto.DecryptRatcheted(msg.Payload, msg.Nonce, msg.Seq.Value);
            }
            else
            {
                // Legacy crypto_box decryption
                plaintext = _crypto.Decrypt(msg.Payload, msg.Nonce);
            }

            var cmd = InputCommand.Deserialize(plaintext);
            if (cmd != null)
            {
                if (cmd.Type == "ratchet_init" && cmd.Seed != null)
                {
                    _crypto.InitRatchet(cmd.Seed);
                    OnStatusChanged?.Invoke("Forward secrecy established 🔐");
                    return;
                }
                OnInputCommand?.Invoke(cmd);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Decryption error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _ws.Dispose();
    }
}
