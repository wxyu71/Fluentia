using System.Text.Json;
using Fluentia.Models;

namespace Fluentia.Services;

/// <summary>
/// Manages the session lifecycle: connect to server, create session, handle messages, key exchange.
/// </summary>
public class RoomManager : IDisposable
{
    private readonly WebSocketService _ws;
    private readonly CryptoService _crypto;
    private string? _currentToken;
    private string? _serverUrl;
    private int _mobileExpirySecs = 60; // default; overridden by server /api/config

    public event Action<string>? OnSessionCreated;     // token
    public event Action<string>? OnMobileConnected;    // deviceId
    public event Action? OnMobileDisconnected;
    public event Action? OnEncryptionReady;
    public event Action<InputCommand>? OnInputCommand;
    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnError;
    public event Action<string>? OnDeviceCodeCreated;  // code
    public event Action<string, string, string>? OnDeviceCodePending; // code, verifyId, userAgent

    public string? CurrentToken => _currentToken;
    public string? ServerUrl => _serverUrl;
    public bool EncryptionReady => _crypto.IsReady;
    public int MobileExpirySecs => _mobileExpirySecs;

    /// <summary>
    /// Send an encrypted command from PC to mobile.
    /// </summary>
    public async Task SendToMobileAsync(string jsonPayload)
    {
        if (!_crypto.IsReady || !_ws.IsConnected) return;

        try
        {
            WsMessage msg;
            if (_crypto.SendRatchetReady)
            {
                var (payload, nonce, seq) = _crypto.EncryptRatcheted(jsonPayload);
                msg = new WsMessage { Type = MsgTypes.Encrypted, Payload = payload, Nonce = nonce, Seq = seq };
            }
            else
            {
                var (payload, nonce) = _crypto.Encrypt(jsonPayload);
                msg = new WsMessage { Type = MsgTypes.Encrypted, Payload = payload, Nonce = nonce };
            }
            await _ws.SendAsync(msg);
        }
        catch { /* best-effort */ }
    }

    public RoomManager()
    {
        _ws = new WebSocketService();
        _crypto = new CryptoService();

        _ws.OnConnected += () =>
        {
            OnStatusChanged?.Invoke("Connected to server");
            _ = _ws.SendAsync(new WsMessage { Type = MsgTypes.CreateSession, Version = MsgTypes.ProtocolVersion });
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
        // Fetch server config (non-blocking — best effort)
        _ = FetchServerConfigAsync(serverUrl);
        await _ws.ConnectAsync(serverUrl);
    }

    private async Task FetchServerConfigAsync(string wsUrl)
    {
        try
        {
            // Convert ws:// or wss:// to http:// or https://
            var httpUrl = wsUrl
                .Replace("/ws", "/api/config")
                .Replace("wss://", "https://")
                .Replace("ws://", "http://");
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync(httpUrl);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("mobileExpirySecs", out var expEl))
                _mobileExpirySecs = expEl.GetInt32();
        }
        catch { /* best-effort; use defaults */ }
    }

    public async Task RefreshSession()
    {
        _crypto.Reset();
        if (_ws.IsConnected)
        {
            await _ws.SendAsync(new WsMessage { Type = MsgTypes.CreateSession, Version = MsgTypes.ProtocolVersion });
        }
    }

    public async Task RequestDeviceCode()
    {
        if (_ws.IsConnected)
        {
            await _ws.SendAsync(new WsMessage { Type = MsgTypes.DeviceCodeRequest });
        }
    }

    public async Task ConfirmDeviceCode(string code)
    {
        if (_ws.IsConnected)
        {
            await _ws.SendAsync(new WsMessage
            {
                Type = MsgTypes.DeviceCodeConfirm,
                DeviceCode = code,
                PublicKey = _crypto.PublicKeyBase64,
            });
        }
    }

    public async Task RejectDeviceCode(string code)
    {
        if (_ws.IsConnected)
        {
            await _ws.SendAsync(new WsMessage { Type = MsgTypes.DeviceCodeReject, DeviceCode = code });
        }
    }

    public string? GetQRData()
    {
        if (_currentToken == null || _serverUrl == null) return null;
        // Compact format: F1|token|key (no server URL — mobile derives from page origin)
        return $"F1|{_currentToken}|{_crypto.PublicKeyBase64}";
    }

    /// <summary>
    /// Returns the full connection info JSON for manual pairing / device code auth.
    /// </summary>
    public string? GetFullConnectionInfo()
    {
        if (_currentToken == null || _serverUrl == null) return null;
        var data = new { s = _serverUrl, t = _currentToken, k = _crypto.PublicKeyBase64 };
        return JsonSerializer.Serialize(data);
    }

    private void HandleMessage(WsMessage msg)
    {
        switch (msg.Type)
        {
            case MsgTypes.SessionCreated:
                _currentToken = msg.Token;
                OnSessionCreated?.Invoke(msg.Token!);
                OnStatusChanged?.Invoke($"Session ready: {msg.Token?[..8]}...");
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

            case MsgTypes.DeviceCodeCreated:
                OnDeviceCodeCreated?.Invoke(msg.DeviceCode ?? "");
                break;

            case MsgTypes.DeviceCodePending:
                OnDeviceCodePending?.Invoke(msg.DeviceCode ?? "", msg.VerifyId ?? "", msg.UserAgent ?? "Unknown");
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
                plaintext = _crypto.DecryptRatcheted(msg.Payload, msg.Nonce, msg.Seq.Value);
            }
            else
            {
                plaintext = _crypto.Decrypt(msg.Payload, msg.Nonce);
            }

            var cmd = InputCommand.Deserialize(plaintext);
            if (cmd != null)
            {
                if (cmd.Type == "ratchet_init" && cmd.Seed != null)
                {
                    _crypto.InitRatchet(cmd.Seed);
                    OnStatusChanged?.Invoke("Forward secrecy established");

                    // Initialize PC's send ratchet for backward security
                    var pcSeed = _crypto.InitSendRatchet();
                    var initCmd = System.Text.Json.JsonSerializer.Serialize(
                        new { type = "pc_ratchet_init", seed = pcSeed });
                    var (payload, nonce) = _crypto.Encrypt(initCmd);
                    _ = _ws.SendAsync(new WsMessage
                    {
                        Type = MsgTypes.Encrypted,
                        Payload = payload,
                        Nonce = nonce,
                    });
                    OnStatusChanged?.Invoke("Bidirectional secrecy established");
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
