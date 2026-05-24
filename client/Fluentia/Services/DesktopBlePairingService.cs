using System.Text;
using System.Text.Json;
using Fluentia.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Fluentia.Services;

public sealed record BlePairingRequest(string VerificationCode, string DeviceLabel, string DeviceId, string RemotePublicKey);

public sealed class DesktopBlePairingService : IDisposable
{
    private static readonly Guid ServiceUuid = Guid.Parse("21e2f7d4-4dc0-4b0d-a145-5f9b6459be10");
    private static readonly Guid NotifyCharacteristicUuid = Guid.Parse("21e2f7d4-4dc0-4b0d-a145-5f9b6459be11");
    private static readonly Guid WriteCharacteristicUuid = Guid.Parse("21e2f7d4-4dc0-4b0d-a145-5f9b6459be12");

    private readonly Func<BleDesktopSessionInfo?> _sessionProvider;
    private readonly Action<WsMessage>? _encryptedMessageSink;
    private readonly Action<string>? _statusSink;
    private readonly SemaphoreSlim _pairingGate = new(1, 1);
    private readonly HashSet<string> _authorizedRemotePublicKeys = new(StringComparer.Ordinal);
    private readonly object _authorizationGate = new();
    private readonly HashSet<string> _notificationReadyPublicKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BleEnvelope> _pendingVerifiedEnvelopes = new(StringComparer.Ordinal);
    private readonly object _notificationGate = new();
    private static readonly TimeSpan NotificationSubscriberWait = TimeSpan.FromMilliseconds(150);
    private const int NotificationSubscriberRetryCount = 10;

    private GattServiceProvider? _serviceProvider;
    private GattLocalCharacteristic? _notifyCharacteristic;
    private GattLocalCharacteristic? _writeCharacteristic;
    private bool _started;

    public DesktopBlePairingService(
        Func<BleDesktopSessionInfo?> sessionProvider,
        Action<WsMessage>? encryptedMessageSink = null,
        Action<string>? statusSink = null)
    {
        _sessionProvider = sessionProvider;
        _encryptedMessageSink = encryptedMessageSink;
        _statusSink = statusSink;
    }

    public bool IsAdvertising => _started;

    public void AuthorizeRemotePublicKey(string remotePublicKey)
    {
        if (string.IsNullOrWhiteSpace(remotePublicKey))
        {
            return;
        }

        lock (_authorizationGate)
        {
            _authorizedRemotePublicKeys.Add(remotePublicKey);
        }
    }

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        var providerResult = await GattServiceProvider.CreateAsync(ServiceUuid);
        if (providerResult.Error != BluetoothError.Success)
        {
            throw new InvalidOperationException($"BLE service provider unavailable: {providerResult.Error}");
        }

        _serviceProvider = providerResult.ServiceProvider;

        var notifyParameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Notify,
            UserDescription = "Fluentia BLE notifications",
            ReadProtectionLevel = GattProtectionLevel.Plain,
            WriteProtectionLevel = GattProtectionLevel.Plain,
        };

        var notifyResult = await _serviceProvider.Service.CreateCharacteristicAsync(NotifyCharacteristicUuid, notifyParameters);
        if (notifyResult.Error != BluetoothError.Success)
        {
            throw new InvalidOperationException($"BLE notify characteristic unavailable: {notifyResult.Error}");
        }

        _notifyCharacteristic = notifyResult.Characteristic;
        _notifyCharacteristic.SubscribedClientsChanged += NotifyCharacteristic_SubscribedClientsChanged;

        var writeParameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
            UserDescription = "Fluentia BLE pairing channel",
            ReadProtectionLevel = GattProtectionLevel.Plain,
            WriteProtectionLevel = GattProtectionLevel.Plain,
        };

        var writeResult = await _serviceProvider.Service.CreateCharacteristicAsync(WriteCharacteristicUuid, writeParameters);
        if (writeResult.Error != BluetoothError.Success)
        {
            throw new InvalidOperationException($"BLE write characteristic unavailable: {writeResult.Error}");
        }

        _writeCharacteristic = writeResult.Characteristic;
        _writeCharacteristic.WriteRequested += WriteCharacteristic_WriteRequested;

        _serviceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true,
        });

        _started = true;
        _statusSink?.Invoke("BLE pairing host ready");
    }

    public void Dispose()
    {
        if (_notifyCharacteristic is not null)
        {
            _notifyCharacteristic.SubscribedClientsChanged -= NotifyCharacteristic_SubscribedClientsChanged;
        }

        if (_writeCharacteristic is not null)
        {
            _writeCharacteristic.WriteRequested -= WriteCharacteristic_WriteRequested;
        }

        _serviceProvider?.StopAdvertising();
        _pairingGate.Dispose();
    }

    private async void WriteCharacteristic_WriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request?.Value is null)
            {
                BleLog.Write("[BLE] WriteRequested: null value");
                return;
            }

            var rawLen = request.Value.Length;
            var json = ReadString(request.Value);
            request.Respond();

            BleLog.Write($"[BLE] WriteRequested rawLen={rawLen} jsonLen={json.Length} json={json[..Math.Min(json.Length, 80)]}");

            var envelope = JsonSerializer.Deserialize<BleEnvelope>(json);
            if (envelope is null)
            {
                BleLog.Write("[BLE] WriteRequested: null envelope");
                await SendAsync(new BleEnvelope { Type = "error", Payload = "Invalid BLE payload." });
                return;
            }

            if (string.Equals(envelope.Type, MsgTypes.Encrypted, StringComparison.Ordinal))
            {
                HandleEncryptedEnvelope(envelope);
                return;
            }

            if (string.Equals(envelope.Type, "notify_ready", StringComparison.Ordinal))
            {
                await HandleNotifyReadyAsync(envelope);
                return;
            }

            if (!string.Equals(envelope.Type, "client_hello", StringComparison.Ordinal))
            {
                _statusSink?.Invoke($"BLE ignored message: {envelope.Type}");
                return;
            }

            await HandleClientHelloAsync(envelope);
        }
        catch (Exception ex)
        {
            _statusSink?.Invoke($"BLE write failed: {ex.Message}");
            try
            {
                await SendAsync(new BleEnvelope { Type = "error", Payload = "BLE pairing failed on PC." });
            }
            catch
            {
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task HandleClientHelloAsync(BleEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.PublicKey))
        {
            await SendAsync(new BleEnvelope { Type = "error", Payload = "Missing mobile public key." });
            return;
        }

        if (!ConsumeAuthorizedRemotePublicKey(envelope.PublicKey))
        {
            await SendAsync(new BleEnvelope { Type = "error", Payload = "Authorize BLE from the encrypted session first." });
            _statusSink?.Invoke("BLE pairing blocked: missing encrypted-session authorization");
            return;
        }

        await _pairingGate.WaitAsync();
        try
        {
            var session = _sessionProvider();
            if (session is null)
            {
                await SendAsync(new BleEnvelope { Type = "error", Payload = "PC pairing session is not ready yet." });
                return;
            }

            var deviceId = envelope.Payload ?? string.Empty;
            var deviceLabel = string.IsNullOrWhiteSpace(deviceId)
                ? "Nearby BLE device"
                : $"BLE nearby device · {deviceId}";

            _statusSink?.Invoke($"BLE pairing request: {deviceLabel}");

            var verifiedEnvelope = new BleEnvelope
            {
                Type = "verified",
                Approved = true,
                Token = session.Token,
                ServerUrl = session.ServerUrl,
                PublicKey = session.PublicKey,
                Version = MsgTypes.ProtocolVersion,
            };

            if (IsNotificationReady(envelope.PublicKey))
            {
                await SendAsync(verifiedEnvelope);
                _statusSink?.Invoke("BLE pairing approved");
            }
            else
            {
                CachePendingVerifiedEnvelope(envelope.PublicKey, verifiedEnvelope);
                _statusSink?.Invoke("BLE verified response pending notify_ready");
            }
        }
        finally
        {
            _pairingGate.Release();
        }
    }

    private bool ConsumeAuthorizedRemotePublicKey(string remotePublicKey)
    {
        lock (_authorizationGate)
        {
            return _authorizedRemotePublicKeys.Remove(remotePublicKey);
        }
    }

    private async Task HandleNotifyReadyAsync(BleEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.PublicKey))
        {
            await SendAsync(new BleEnvelope { Type = "error", Payload = "Missing mobile public key for notify_ready." });
            return;
        }

        MarkNotificationReady(envelope.PublicKey);
        _statusSink?.Invoke("BLE notify_ready received");

        var pendingEnvelope = ConsumePendingVerifiedEnvelope(envelope.PublicKey);
        if (pendingEnvelope is null)
        {
            return;
        }

        await SendAsync(pendingEnvelope);
        _statusSink?.Invoke("BLE pairing approved");
    }

    private void CachePendingVerifiedEnvelope(string remotePublicKey, BleEnvelope envelope)
    {
        lock (_notificationGate)
        {
            _pendingVerifiedEnvelopes[remotePublicKey] = envelope;
        }
    }

    private BleEnvelope? ConsumePendingVerifiedEnvelope(string remotePublicKey)
    {
        lock (_notificationGate)
        {
            if (!_pendingVerifiedEnvelopes.Remove(remotePublicKey, out var envelope))
            {
                return null;
            }

            return envelope;
        }
    }

    private void MarkNotificationReady(string remotePublicKey)
    {
        lock (_notificationGate)
        {
            _notificationReadyPublicKeys.Add(remotePublicKey);
        }
    }

    private bool IsNotificationReady(string remotePublicKey)
    {
        lock (_notificationGate)
        {
            return _notificationReadyPublicKeys.Contains(remotePublicKey);
        }
    }

    private void NotifyCharacteristic_SubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        _statusSink?.Invoke($"BLE notify subscribers: {sender.SubscribedClients.Count}");
    }

    private void HandleEncryptedEnvelope(BleEnvelope envelope)
    {
        if (_encryptedMessageSink is null || string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Nonce))
        {
            BleLog.Write($"[BLE] HandleEncryptedEnvelope: sink={_encryptedMessageSink != null} payload={envelope.Payload?.Length ?? 0} nonce={envelope.Nonce?.Length ?? 0}");
            return;
        }

        BleLog.Write($"[BLE] HandleEncryptedEnvelope: forwarding payload={envelope.Payload.Length}B seq={envelope.Seq?.ToString() ?? "null"}");
        _encryptedMessageSink(new WsMessage
        {
            Type = MsgTypes.Encrypted,
            Payload = envelope.Payload,
            Nonce = envelope.Nonce,
            Seq = envelope.Seq,
        });
    }

    private async Task SendAsync(BleEnvelope envelope)
    {
        if (_notifyCharacteristic is null)
        {
            throw new InvalidOperationException("BLE notify characteristic is not initialized.");
        }

        await WaitForSubscribedClientAsync(_notifyCharacteristic);

        if (_notifyCharacteristic.SubscribedClients.Count == 0)
        {
            _statusSink?.Invoke($"BLE notify aborted: no subscribed clients for {envelope.Type}");
            throw new InvalidOperationException("No BLE subscribers are listening for notifications.");
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
        var writer = new DataWriter();
        writer.WriteBytes(bytes);
        var results = await _notifyCharacteristic.NotifyValueAsync(writer.DetachBuffer());

        _statusSink?.Invoke($"BLE notify {envelope.Type}: subscribers={_notifyCharacteristic.SubscribedClients.Count}, results={results.Count}");

        var delivered = false;
        foreach (var result in results)
        {
            _statusSink?.Invoke($"BLE notify result: status={result.Status}, bytes={result.BytesSent}, protocolError={result.ProtocolError}");
            if (result.Status == GattCommunicationStatus.Success)
            {
                delivered = true;
            }
        }

        if (!delivered)
        {
            throw new InvalidOperationException($"BLE notification delivery failed for {envelope.Type}.");
        }
    }

    private async Task WaitForSubscribedClientAsync(GattLocalCharacteristic characteristic)
    {
        if (characteristic.SubscribedClients.Count > 0)
        {
            return;
        }

        for (var attempt = 0; attempt < NotificationSubscriberRetryCount; attempt++)
        {
            await Task.Delay(NotificationSubscriberWait);
            if (characteristic.SubscribedClients.Count > 0)
            {
                _statusSink?.Invoke($"BLE notify subscriber became ready after {attempt + 1} wait(s)");
                return;
            }
        }
    }

    private static string ReadString(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class BleEnvelope
    {
        public string Type { get; set; } = string.Empty;
        public string? PublicKey { get; set; }
        public string? Token { get; set; }
        public string? ServerUrl { get; set; }
        public string? Payload { get; set; }
        public string? Nonce { get; set; }
        public int? Seq { get; set; }
        public string? Code { get; set; }
        public bool Approved { get; set; }
        public string? Version { get; set; }
    }
}