using System.Reflection;

namespace Fluentia.Tests;

/// <summary>
/// Tests for SanitizeFileName addressing code review findings:
/// [M10] File path traversal, reserved device names
/// </summary>
public class SanitizeFileNameTests
{
    private static string InvokeSanitize(string input)
    {
        // SanitizeFileName is private static in MainWindow
        var mainWindowType = typeof(Fluentia.MainWindow);
        var method = mainWindowType.GetMethod("SanitizeFileName",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null)
            throw new InvalidOperationException("SanitizeFileName method not found");
        return (string)method.Invoke(null, new object[] { input })!;
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    public void RejectsReservedDeviceNames(string reserved)
    {
        // [M10] Windows reserved names should be prefixed
        var result = InvokeSanitize(reserved);
        Assert.NotEqual(reserved, result);
        Assert.StartsWith("_", result);
    }

    [Theory]
    [InlineData("test.txt", "test.txt")]
    [InlineData("document.pdf", "document.pdf")]
    [InlineData("my file (1).docx", "my file (1).docx")]
    public void AllowsNormalFilenames(string input, string expected)
    {
        // [M10] Normal filenames should pass through
        var result = InvokeSanitize(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void StripsPathTraversal()
    {
        // [M10] Path traversal sequences should be removed
        var result = InvokeSanitize("../../etc/passwd");
        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain("/", result);
    }

    [Fact]
    public void StripsLeadingDots()
    {
        // Hidden files on Unix - leading dots stripped
        var result = InvokeSanitize("...hidden");
        Assert.DoesNotContain(".", result);
    }
}
