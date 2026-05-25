using Fluentia.Services;

namespace Fluentia.Tests;

public class DesktopTransportHealthTests
{
    private readonly DesktopTransportHealth _health = new();

    [Fact]
    public void InitialScores_AreNeutral()
    {
        Assert.Equal(50, _health.BleScore);
        Assert.Equal(50, _health.WsScore);
    }

    [Fact]
    public void BothTransports_AvailableInitially()
    {
        Assert.True(_health.IsTransportAvailable(RelayTransportKind.BluetoothLowEnergy));
        Assert.True(_health.IsTransportAvailable(RelayTransportKind.WebSocket));
    }

    [Fact]
    public void BleScore_DropsOnConsecutiveFailures()
    {
        _health.OnBleFailure();
        Assert.True(_health.BleScore < 70); // dropped from initial
        _health.OnBleFailure();
        _health.OnBleFailure(); // hits threshold
        Assert.Equal(0, _health.BleScore);
        Assert.False(_health.IsTransportAvailable(RelayTransportKind.BluetoothLowEnergy));
    }

    [Fact]
    public void BleScore_RecoversOnSuccess()
    {
        _health.OnBleFailure();
        _health.OnBleFailure();
        var before = _health.BleScore;
        _health.OnBleSuccess();
        Assert.True(_health.BleScore > before);
    }

    [Fact]
    public void WsScore_DropsWhenDisconnected()
    {
        _health.SetWsConnected(false);
        Assert.Equal(0, _health.WsScore);
        Assert.False(_health.IsTransportAvailable(RelayTransportKind.WebSocket));
    }

    [Fact]
    public void WsScore_ImprovesOnLowRtt()
    {
        _health.UpdateWsRtt(50);
        Assert.True(_health.WsScore > 50);
    }

    [Fact]
    public void SelectTransport_Input_PrefersBle()
    {
        _health.UpdateBleRssi(-40); // good signal
        _health.UpdateWsRtt(100);
        Assert.Equal(RelayTransportKind.BluetoothLowEnergy, _health.SelectTransport(TransportMessageType.Input));
    }

    [Fact]
    public void SelectTransport_File_PrefersWs()
    {
        _health.UpdateBleRssi(-40);
        _health.UpdateWsRtt(100);
        Assert.Equal(RelayTransportKind.WebSocket, _health.SelectTransport(TransportMessageType.File));
    }

    [Fact]
    public void SelectTransport_SkipsDownTransport()
    {
        _health.MarkDown(RelayTransportKind.BluetoothLowEnergy);
        Assert.Equal(RelayTransportKind.WebSocket, _health.SelectTransport(TransportMessageType.Input));
    }

    [Fact]
    public void SelectTransport_ReturnsNull_WhenBothDown()
    {
        _health.MarkDown(RelayTransportKind.BluetoothLowEnergy);
        _health.MarkDown(RelayTransportKind.WebSocket);
        Assert.Null(_health.SelectTransport(TransportMessageType.Input));
    }

    [Fact]
    public void SelectTransport_LowBattery_PrefersBle()
    {
        _health.UpdateBattery(0.08, false);
        // Even for file transfer, BLE should be preferred at critical battery
        Assert.Equal(RelayTransportKind.BluetoothLowEnergy, _health.SelectTransport(TransportMessageType.File));
    }

    [Fact]
    public void SelectTransport_BleDegraded_UsesWsForInput()
    {
        _health.UpdateBleRssi(-90); // very poor signal
        _health.OnBleFailure();
        _health.UpdateWsRtt(100);
        Assert.Equal(RelayTransportKind.WebSocket, _health.SelectTransport(TransportMessageType.Input));
    }
}
