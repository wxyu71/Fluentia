using System.Text.Json;
using Fluentia.Models;

namespace Fluentia.Services;

public sealed record PersistedDesktopSession(
    string Token,
    string PublicKey,
    string PrivateKey,
    bool TrustedSessionEstablished);

public sealed record BleDesktopSessionInfo(
    string ServerUrl,
    string Token,
    string PublicKey);

public sealed record VersionRequirementResult(bool IsCompatible, string? Message);

/// <summary>
/// Manages the session lifecycle: connect to server, create session, handle messages, key exchange.
/// </summary>
public class RoomManager : IDisposable
{
    private readonly IRelayTransport _transport;
    private readonly CryptoService _crypto;
    private string? _currentToken;
    private string? _serverUrl;
    private int _mobileExpirySecs = 60; // default; overridden by server /api/config
    private int _sessionMaxAgeDays = 7;
    private bool _fileTransferEnabled = false; // controlled by server MAX_FILE_MB
    private int _maxFileMB = 0;
    private bool _encryptionConfirmed;
    private bool _trustedSessionEstablished;
    private DateTimeOffset? _sessionExpiresAtUtc;

    public event Action<string>? OnSessionCreated;     // token
    public event Action<string>? OnMobileConnected;    // deviceId
    public event Action? OnMobileDisconnected;
    public event Action? OnEncryptionReady;
    public event Action? OnSessionRecovered;
    public event Action<InputCommand>? OnInputCommand;
    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnError;
    public event Action<string>? OnVersionIncompatible;
    public event Action<string>? OnDeviceCodeCreated;  // code
    public event Action<string, string, string>? OnDeviceCodePending; // code, verifyId, userAgent
    public event Action<bool>? OnServerConnectionChanged;
    public event Action<RelayTransportKind>? OnTransportKindChanged;

    public string? CurrentToken => _currentToken;
    public string? ServerUrl => _serverUrl;
    public bool EncryptionReady => _encryptionConfirmed;
    public int MobileExpirySecs => _mobileExpirySecs;
    public int SessionMaxAgeDays => _sessionMaxAgeDays;
    public bool FileTransferEnabled => _fileTransferEnabled;
    public int MaxFileMB => _maxFileMB;
    public bool IsConnected => _transport.IsConnected;
    public bool HasTrustedSession => _trustedSessionEstablished;
    public DateTimeOffset? SessionExpiresAtUtc => _sessionExpiresAtUtc;
    public RelayTransportKind ActiveTransportKind => _transport.TransportKind;

    public BleDesktopSessionInfo? GetBleSessionInfo()
    {
        if (string.IsNullOrWhiteSpace(_serverUrl) || string.IsNullOrWhiteSpace(_currentToken))
        {
            return null;
        }

        return new BleDesktopSessionInfo(_serverUrl, _currentToken, _crypto.PublicKeyBase64);
    }

    public string CreateBleVerificationCode(string remotePublicKeyBase64) =>
        _crypto.CreateBleVerificationCode(remotePublicKeyBase64);

    public void HandleBleEncryptedMessage(WsMessage msg)
    {
        if (msg.Type != MsgTypes.Encrypted)
        {
            return;
        }

        BleLog.Write($"[BLE→PC] payload={msg.Payload?.Length ?? 0}B nonce={msg.Nonce?.Length ?? 0}B seq={msg.Seq?.ToString() ?? "null"} cryptoReady={_crypto.IsReady} ratchetReady={_crypto.RatchetReady}");
        HandleEncrypted(msg);
    }

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
        if (!_encryptionConfirmed || !_transport.IsConnected) return false;

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
            await _transport.SendAsync(msg);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public RoomManager()
        : this(new WebSocketService())
    {
    }

    public RoomManager(IRelayTransport transport)
    {
        _transport = transport;
        _crypto = new CryptoService();

        _transport.OnConnected += () =>
        {
            OnServerConnectionChanged?.Invoke(true);
            OnTransportKindChanged?.Invoke(_transport.TransportKind);
            OnStatusChanged?.Invoke("Connected to server");
            // If we have an existing token, try to rejoin (grace period).
            // Otherwise, create a new session.
            if (_currentToken != null)
            {
                _rejoinPending = true;
                _ = _transport.SendAsync(new WsMessage
                {
                    Type = MsgTypes.RejoinSession,
                    Token = _currentToken,
                    Version = MsgTypes.ProtocolVersion,
                });
            }
            else
            {
                _ = _transport.SendAsync(new WsMessage { Type = MsgTypes.CreateSession, Version = MsgTypes.ProtocolVersion });
            }
        };

        _transport.OnDisconnected += (reason) =>
        {
            OnServerConnectionChanged?.Invoke(false);
            OnStatusChanged?.Invoke($"Disconnected: {reason}");
        };

        _transport.OnMessage += HandleMessage;
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
            _sessionExpiresAtUtc = null;
        }
        OnStatusChanged?.Invoke("Connecting...");
        // Fetch server config (non-blocking — best effort)
        _ = FetchServerConfigAsync(serverUrl);
        await _transport.ConnectAsync(serverUrl);
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
        _sessionExpiresAtUtc = null;
        if (_transport.IsConnected)
        {
            await _transport.SendAsync(new WsMessage { Type = MsgTypes.CreateSession, Version = MsgTypes.ProtocolVersion });
        }
    }

    public async Task RequestDeviceCode()
    {
        if (_transport.IsConnected)
        {
            await _transport.SendAsync(new WsMessage { Type = MsgTypes.DeviceCodeRequest });
        }
    }

    public async Task ConfirmDeviceCode(string code)
    {
        if (_transport.IsConnected)
        {
            await _transport.SendAsync(new WsMessage
            {
                Type = MsgTypes.DeviceCodeConfirm,
                DeviceCode = code,
                PublicKey = _crypto.PublicKeyBase64,
            });
        }
    }

    public async Task RejectDeviceCode(string code)
    {
        if (_transport.IsConnected)
        {
            await _transport.SendAsync(new WsMessage { Type = MsgTypes.DeviceCodeReject, DeviceCode = code });
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
        var versionRequirement = ValidateVersionRequirement(msg);
        if (!versionRequirement.IsCompatible)
        {
            var error = versionRequirement.Message ?? $"Version incompatible. Minimum required version is {msg.MinVersion}.";
            _transport.Disconnect();
            _currentToken = null;
            _encryptionConfirmed = false;
            _trustedSessionEstablished = false;
            _sessionExpiresAtUtc = null;
            OnVersionIncompatible?.Invoke(error);
            OnError?.Invoke(error);
            return;
        }

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
                _sessionExpiresAtUtc = ParseSessionExpiry(msg.ExpiresAt);
                _rejoinPending = false;
                OnSessionCreated?.Invoke(msg.Token!);
                OnStatusChanged?.Invoke($"Session ready: {msg.Token?[..8]}...");
                break;

            case MsgTypes.Rejoined:
                // Successfully reclaimed session within grace period.
                _rejoinPending = false;
                _sessionExpiresAtUtc = ParseSessionExpiry(msg.ExpiresAt) ?? _sessionExpiresAtUtc;
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
                // Skip ResetPeerState to preserve ratchet for BLE fallback.
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
                // Don't reset crypto — keep keypair and ratchet so BLE can continue working.
                if (_rejoinPending)
                {
                    _rejoinPending = false;
                    _currentToken = null;
                    _encryptionConfirmed = false;
                    _trustedSessionEstablished = false;
                    _sessionExpiresAtUtc = null;
                    _ = _transport.SendAsync(new WsMessage { Type = MsgTypes.CreateSession, Version = MsgTypes.ProtocolVersion });
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
        _ = _transport.SendAsync(new WsMessage
        {
            Type = MsgTypes.KeyExchange,
            PublicKey = _crypto.PublicKeyBase64,
        });
    }

    private void HandleEncrypted(WsMessage msg)
    {
        if (msg.Payload == null || msg.Nonce == null) return;
        if (!_crypto.IsReady)
        {
            BleLog.Write($"[BLE→PC] DROPPED: crypto not ready (peerKey={_crypto.PublicKeyBase64?.Length ?? 0}B)");
            return;
        }

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
                BleLog.Write($"[BLE→PC] decrypted OK type={cmd.Type} textLen={cmd.Text?.Length ?? 0}");

                if (cmd.Type == "ratchet_init" && cmd.Seed != null)
                {
                    _crypto.InitRatchet(cmd.Seed);
                    OnStatusChanged?.Invoke("Secure channel pending confirmation");

                    // Initialize PC's send ratchet for backward security
                    var pcSeed = _crypto.InitSendRatchet();
                    var initCmd = System.Text.Json.JsonSerializer.Serialize(
                        new { type = "pc_ratchet_init", seed = pcSeed });
                    var (payload, nonce) = _crypto.Encrypt(initCmd);
                    _ = _transport.SendAsync(new WsMessage
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
            else
            {
                BleLog.Write("[BLE→PC] decrypted but cmd is null");
            }
        }
        catch (Exception ex)
        {
            BleLog.Write($"[BLE→PC] decrypt FAILED: {ex.Message}");
            OnError?.Invoke("Decryption failed");
        }
    }

    public void Dispose()
    {
        _transport.Dispose();
    }

    private static DateTimeOffset? ParseSessionExpiry(string? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(expiresAt))
        {
            return null;
        }

        return DateTimeOffset.TryParse(expiresAt, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private static VersionRequirementResult ValidateVersionRequirement(WsMessage msg)
    {
        if (string.IsNullOrWhiteSpace(msg.MinVersion))
        {
            return new VersionRequirementResult(true, null);
        }

        if (!Version.TryParse(MsgTypes.ProtocolVersion, out var currentVersion) ||
            !Version.TryParse(msg.MinVersion, out var minimumVersion))
        {
            return new VersionRequirementResult(true, null);
        }

        return currentVersion < minimumVersion
            ? new VersionRequirementResult(false, $"Version incompatible. Minimum required version is {minimumVersion}.")
            : new VersionRequirementResult(true, null);
    }
}
