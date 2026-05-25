namespace Fluentia.Services;

/// <summary>
/// Message types for transport routing decisions.
/// </summary>
public enum TransportMessageType
{
    /// <summary>Input commands (diff, enter, backspace). Latency-sensitive, prefer BLE.</summary>
    Input,
    /// <summary>File transfer chunks. Bandwidth-sensitive, always prefer WS.</summary>
    File,
    /// <summary>Key exchange and handshake. Reliability-sensitive, single channel.</summary>
    Handshake,
    /// <summary>Control messages (clipboard, ble_auth). Use best available.</summary>
    Control,
}

/// <summary>
/// Evaluates BLE and WebSocket health scores and selects the best transport.
/// Mirrors the mobile TransportHealthMonitor logic for desktop parity.
/// </summary>
public class DesktopTransportHealth
{
    private int _bleScore = 50;
    private int _wsScore = 50;
    private int _bleConsecutiveFailures;
    private int _bleRssi = -50;
    private int _wsRtt = 100;
    private bool _wsConnected;
    private double _batteryLevel = 1.0;
    private bool _batteryCharging = true;

    private bool _bleDown;
    private bool _wsDown;

    // Thresholds
    private const int BleFailureThreshold = 3;
    private const int BleRssiDegraded = -80;
    private const int WsRttDegraded = 1000;
    private const double BatteryLow = 0.20;
    private const double BatteryCritical = 0.10;

    public int BleScore => _bleScore;
    public int WsScore => _wsScore;

    // --- BLE health updates ---

    public void OnBleSuccess()
    {
        _bleConsecutiveFailures = 0;
        _bleDown = false;
        RecalculateBleScore();
    }

    public void OnBleFailure()
    {
        _bleConsecutiveFailures++;
        if (_bleConsecutiveFailures >= BleFailureThreshold)
        {
            _bleDown = true;
            _bleScore = 0;
        }
        else
        {
            RecalculateBleScore();
        }
    }

    public void UpdateBleRssi(int rssi)
    {
        _bleRssi = rssi;
        RecalculateBleScore();
    }

    // --- WS health updates ---

    public void UpdateWsRtt(int rttMs)
    {
        _wsRtt = rttMs;
        _wsConnected = true;
        _wsDown = false;
        RecalculateWsScore();
    }

    public void SetWsConnected(bool connected)
    {
        _wsConnected = connected;
        if (!connected)
        {
            _wsDown = true;
            _wsScore = 0;
        }
        else
        {
            _wsDown = false;
            RecalculateWsScore();
        }
    }

    // --- Battery ---

    public void UpdateBattery(double level, bool charging)
    {
        _batteryLevel = Math.Clamp(level, 0, 1);
        _batteryCharging = charging;
        RecalculateBleScore();
        RecalculateWsScore();
    }

    // --- Routing ---

    public bool IsTransportAvailable(RelayTransportKind kind) =>
        kind == RelayTransportKind.BluetoothLowEnergy
            ? !_bleDown && _bleScore > 0
            : !_wsDown && _wsScore > 0;

    /// <summary>
    /// Select the best transport for the given message type.
    /// Returns null if no transport is available.
    /// </summary>
    public RelayTransportKind? SelectTransport(TransportMessageType messageType)
    {
        var bleAvail = IsTransportAvailable(RelayTransportKind.BluetoothLowEnergy);
        var wsAvail = IsTransportAvailable(RelayTransportKind.WebSocket);

        if (!bleAvail && !wsAvail) return null;
        if (!bleAvail) return RelayTransportKind.WebSocket;
        if (!wsAvail) return RelayTransportKind.BluetoothLowEnergy;

        // Critical battery: prefer BLE (lower power)
        if (_batteryLevel < BatteryCritical && !_batteryCharging)
            return RelayTransportKind.BluetoothLowEnergy;

        return messageType switch
        {
            // Input: prefer BLE for low latency
            TransportMessageType.Input => _bleScore >= 40
                ? RelayTransportKind.BluetoothLowEnergy
                : RelayTransportKind.WebSocket,

            // File: always prefer WS (bandwidth)
            TransportMessageType.File => _wsScore >= 40
                ? RelayTransportKind.WebSocket
                : RelayTransportKind.BluetoothLowEnergy,

            // Handshake: single most reliable channel
            TransportMessageType.Handshake => _bleScore > _wsScore
                ? RelayTransportKind.BluetoothLowEnergy
                : RelayTransportKind.WebSocket,

            // Control: best available
            _ => _bleScore > _wsScore
                ? RelayTransportKind.BluetoothLowEnergy
                : RelayTransportKind.WebSocket,
        };
    }

    public void MarkDown(RelayTransportKind kind)
    {
        if (kind == RelayTransportKind.BluetoothLowEnergy)
        {
            _bleDown = true;
            _bleScore = 0;
        }
        else
        {
            _wsDown = true;
            _wsScore = 0;
        }
    }

    // --- Private ---

    private void RecalculateBleScore()
    {
        if (_bleDown) { _bleScore = 0; return; }

        var score = 50;
        score += _bleRssi >= -50 ? 20 : _bleRssi >= -65 ? 10 : _bleRssi >= -80 ? -10 : -30;
        score -= _bleConsecutiveFailures * 15;
        if (_batteryLevel < BatteryCritical && !_batteryCharging) score -= 10;
        _bleScore = Math.Clamp(score, 0, 100);
    }

    private void RecalculateWsScore()
    {
        if (_wsDown || !_wsConnected) { _wsScore = 0; return; }

        var score = 60;
        score += _wsRtt < 100 ? 20 : _wsRtt < 300 ? 10 : _wsRtt < WsRttDegraded ? 0 : -20;
        if (!_batteryCharging)
        {
            if (_batteryLevel < BatteryCritical) score -= 40;
            else if (_batteryLevel < BatteryLow) score -= 15;
        }
        _wsScore = Math.Clamp(score, 0, 100);
    }
}
