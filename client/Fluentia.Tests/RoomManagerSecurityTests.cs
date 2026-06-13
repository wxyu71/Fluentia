using Fluentia.Services;

namespace Fluentia.Tests;

/// <summary>
/// Security-focused tests for RoomManager addressing code review findings:
/// [H5] Fire-and-forget exceptions, [M14] CancellationToken, [M15] Broad catch
/// </summary>
public class RoomManagerSecurityTests
{
    [Fact]
    public void RequestDeviceCode_MethodExists()
    {
        // [M14] Verify RequestDeviceCode accepts CancellationToken
        var method = typeof(RoomManager).GetMethod("RequestDeviceCode");
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Contains(parameters, p => p.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void ConfirmDeviceCode_MethodExists()
    {
        // [M14] Verify ConfirmDeviceCode accepts CancellationToken
        var method = typeof(RoomManager).GetMethod("ConfirmDeviceCode");
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Contains(parameters, p => p.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void RejectDeviceCode_MethodExists()
    {
        // [M14] Verify RejectDeviceCode accepts CancellationToken
        var method = typeof(RoomManager).GetMethod("RejectDeviceCode");
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Contains(parameters, p => p.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void RoomManager_HasOnErrorEvent()
    {
        // [M15] Verify OnError event exists for error reporting
        var evt = typeof(RoomManager).GetEvent("OnError");
        Assert.NotNull(evt);
    }
}
