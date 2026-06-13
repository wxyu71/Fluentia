using Fluentia.Services;

namespace Fluentia.Tests;

/// <summary>
/// Security-focused tests for TextInjector addressing code review findings:
/// [M9] Input sanitization, [L22] Error handling
/// </summary>
public class TextInjectorSecurityTests
{
    [Fact]
    public void TypeText_ReturnsBool()
    {
        // [M9] TypeText should return bool indicating success
        var method = typeof(TextInjector).GetMethod("TypeText");
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);
    }

    [Fact]
    public void TypeText_AcceptsMaxLengthParameter()
    {
        // [M9] TypeText should accept maxLength parameter
        var method = typeof(TextInjector).GetMethod("TypeText");
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Contains(parameters, p => p.Name == "maxLength");
    }

    [Fact]
    public void TypeText_DefaultMaxLengthIsReasonable()
    {
        // [M9] Default maxLength should be 10000
        var method = typeof(TextInjector).GetMethod("TypeText");
        var maxLenParam = method!.GetParameters().First(p => p.Name == "maxLength");
        Assert.Equal(10000, maxLenParam.DefaultValue);
    }
}
