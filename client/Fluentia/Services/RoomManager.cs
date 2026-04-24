using System.Text.Json;
using Fluentia.Models;

namespace Fluentia.Services;

public sealed record PersistedDesktopSession(
    string Token,
    string PublicKey,
    string PrivateKey,
    bool TrustedSessionEstablished);

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
    private int _sessionMaxAgeDays = 7;
    private bool _fileTransferEnabled = false; // controlled by server MAX_FILE_MB
    private int _maxFileMB = 0;
    private bool _encryptionConfirmed;
    private bool _trustedSessionEstablished;

    public event Action<string>? OnSessionCreated;     // token
    public event Action<string>? OnMobileConnected;    // deviceId
    public event Action? OnMobileDisconnected;
    public event Action? OnEncryptionReady;
    public event Action? OnSessionRecovered;
    public event Action<InputCommand>? OnInputCommand;
    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnError;
    public event Action<string>? OnDeviceCodeCreated;  // code
    public event Action<string, string, string>? OnDeviceCodePending; // code, verifyId, userAgent
    public event Action<bool>? OnServerConnectionChanged;

    public string? CurrentToken => _currentToken;
    public string? ServerUrl => _serverUrl;
    public bool EncryptionReady => _encryptionConfirmed;
    public int MobileExpirySecs => _mobileExpirySecs;
    public int SessionMaxAgeDays => _sessionMaxAgeDays;
    public bool FileTransferEnabled => _fileTransferEnabled;
    public int MaxFileMB => _maxFileMB;
    public bool IsConnected => _ws.IsConnected;
    public bool HasTrustedSession => _trustedSessionEstablished;

    public PersistedDesktopSession? ExportPersistedSession()
    {
        if (string.IsNullOrWhiteSpace(_currentToken))
        {
            return null;
        }

        return new PersistedDesktopSession(
            _currentToken,
            _crypto.PublicKeyBase64,
            _crypto.PrivateKeyBase64,
            _trustedSessionEstablished);
    }

    public void RestorePersistedSession(PersistedDesktopSession session)
    {
        _currentToken = session.Token;
        _crypto.ImportKeyPair(session.PublicKey, session.PrivateKey);
        _trustedSessionEstablished = session.TrustedSessionEstablished;
        _encryptionConfirmed = false;
    }

    /// <summary>
    /// Send an encrypted command from PC to mobile.
    /// </summary>
    public async Task<bool> SendToMobileAsync(string jsonPayload)
    {
        if (!_encryptionConfirmed || !_ws.IsConnected) return false;

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
            return true;
        }
        catch
        {
            return false;
        }
    }

    public RoomManager()
    {
        _ws = new WebSocketService();
        _crypto = new CryptoService();

        _ws.OnConnected += () =>
        {
            OnServerConnectionChanged?.Invoke(true);
            OnStatusChanged?.Invoke("Connected to server");
            // If we have an existing token, try to rejoin (grace period).
            // Otherwise, create a new session.
            if (_currentToken != null)
            {
                _rejoinPending = true;
                _ = _ws.SendAsync(new WsMessage
                {
                    Type = MsgTypes.RejoinSession,
                    Token = _currentToken,
                    Version = MsgTypes.ProtocolVersion,
                });
            }
            else
            {
                _ = _ws.SendAsync(new WsMessage { Type = MsgTypes.CreateSession, Version = MsgTypes.ProtocolVersion });
            }
        };

        _ws.OnDisconnected += (reason) =>
        {
            OnServerConnectionChanged?.Invoke(false);
            OnStatusChanged?.Invoke($"Disconnected: {reason}");
        };

        _ws.OnMessage += HandleMessage;
    }

    public async Task ConnectAsync(string serverUrl)
    {
        _serverUrl = serverUrl;
        // Don't reset crypto — we want to keep keys for rejoin.
        // Crypto is only reset when we create a truly new session (RefreshSession or first connect).
        if (_currentToken == null)
        {
            _crypto.Reset();
            _encryptionConfirmed = false;
            _trustedSessionEstablished = false;
        }
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
            if (doc.RootElement.TryGetProperty("sessionMaxAgeDays", out var maxAgeEl))
                _sessionMaxAgeDays = maxAgeEl.GetInt32();
            if (doc.RootElement.TryGetProperty("fileTransfer", out var ftEl))
                _fileTransferEnabled = ftEl.GetBoolean();
            if (doc.RootElement.TryGetProperty("maxFileMB", out var mbEl))
                _maxFileMB = mbEl.GetInt32();
        }
        catch { /* best-effort; use defaults */ }
    }

    public async Task RefreshSession()
    {
        _crypto.Reset();
        _encryptionConfirmed = false;
        _trustedSessionEstablished = false;
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

    private bool _rejoinPending; // true while waiting for rejoin_session response

    private void HandleMessage(WsMessage msg)
    {
        switch (msg.Type)
        {
            case MsgTypes.SessionCreated:
                // Validate server protocol version
                if (!string.IsNullOrEmpty(msg.Version) && msg.Version != MsgTypes.ProtocolVersion)
                {
                    OnError?.Invoke($"Protocol mismatch: client {MsgTypes.ProtocolVersion}, server {msg.Version}. Please update.");
                    return;
                }
                _currentToken = msg.Token;
                _encryptionConfirmed = false;
                _trustedSessionEstablished = false;
                _rejoinPending = false;
                OnSessionCreated?.Invoke(msg.Token!);
                OnStatusChanged?.Invoke($"Session ready: {msg.Token?[..8]}...");
                break;

            case MsgTypes.Rejoined:
                // Successfully reclaimed session within grace period.
                _rejoinPending = false;
                OnStatusChanged?.Invoke($"Reconnected to session: {msg.Token?[..8]}...");
                OnSessionRecovered?.Invoke();
                // Token is the same, crypto keys are the same — mobile is still connected.
                // If mobile is connected, server will send peer_joined.
                // If mobile was disconnected, PC keeps waiting.
                break;

            case MsgTypes.PeerJoined:
                OnMobileConnected?.Invoke(msg.DeviceId ?? "unknown");
                _encryptionConfirmed = false;
                OnStatusChanged?.Invoke($"Mobile connected: {msg.DeviceId?[..8]}...");
                break;

            case MsgTypes.PeerLeft:
                // Don't reset crypto — keep our keypair so mobile can reconnect
                // with the same QR-authenticated key and re-establish encryption.
                _crypto.ResetPeerState();
                _encryptionConfirmed = false;
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
                // If rejoin failed (session expired/not found), fall back to creating a new session.
                if (_rejoinPending)
                {
                    _rejoinPending = false;
                    _currentToken = null;
                    _crypto.Reset();
                    _encryptionConfirmed = false;
                    _trustedSessionEstablished = false;
                    _ = _ws.SendAsync(new WsMessage { Type = MsgTypes.CreateSession, Version = MsgTypes.ProtocolVersion });
                    break;
                }
                OnError?.Invoke(msg.Error ?? "Unknown error");
                break;
        }
    }

    private void HandleKeyExchange(WsMessage msg)
    {
        if (msg.PublicKey == null) return;

        _crypto.ResetPeerState();
        _crypto.SetPeerPublicKey(msg.PublicKey);
        _encryptionConfirmed = false;
        OnStatusChanged?.Invoke("Key exchange complete");

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
                    OnStatusChanged?.Invoke("Secure channel pending confirmation");

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
                    return;
                }

                if (cmd.Type == "handshake_ack")
                {
                    if (!_encryptionConfirmed)
                    {
                        _encryptionConfirmed = true;
                        _trustedSessionEstablished = true;
                        OnEncryptionReady?.Invoke();
                        OnStatusChanged?.Invoke("E2E encryption established");
                    }
                    return;
                }

                OnInputCommand?.Invoke(cmd);
            }
        }
        catch (Exception)
        {
            OnError?.Invoke("Decryption failed");
        }
    }

    public void Dispose()
    {
        _ws.Dispose();
    }
}
