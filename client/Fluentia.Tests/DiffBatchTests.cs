using Fluentia.Services;
using Xunit;

namespace Fluentia.Tests;

/// <summary>
/// Tests for diff batching and prefix-only diff logic in MainWindow.
/// These test the pure logic extracted from FlushBufferedDiff and ApplyDiffToBuffer.
/// </summary>
public class DiffBatchTests
{
    // === ApplyDiffToBuffer logic ===
    // Replicates the logic from MainWindow.ApplyDiffToBuffer
    private static string ApplyDiffToBuffer(string current, int backspace, string? insertText)
    {
        var safeBackspace = Math.Max(0, Math.Min(backspace, current.Length));
        var prefixLength = current.Length - safeBackspace;
        return current[..prefixLength] + (insertText ?? string.Empty);
    }

    // === FlushBufferedDiff logic (prefix-only) ===
    // Replicates the logic from MainWindow.FlushBufferedDiff
    private static (int backspace, string insert) ComputeFlushDiff(string oldText, string nextText)
    {
        var prefix = 0;
        var limit = Math.Min(oldText.Length, nextText.Length);
        while (prefix < limit && oldText[prefix] == nextText[prefix])
        {
            prefix++;
        }

        var backspace = oldText.Length - prefix;
        var insert = nextText[prefix..];
        return (backspace, insert);
    }

    // === Prefix-only diff tests ===

    [Fact]
    public void FlushDiff_IdenticalText()
    {
        var (backspace, insert) = ComputeFlushDiff("hello", "hello");
        Assert.Equal(0, backspace);
        Assert.Equal("", insert);
    }

    [Fact]
    public void FlushDiff_AppendAtEnd()
    {
        // old: "hello", new: "hello world" → prefix=5
        var (backspace, insert) = ComputeFlushDiff("hello", "hello world");
        Assert.Equal(0, backspace);
        Assert.Equal(" world", insert);
    }

    [Fact]
    public void FlushDiff_MiddleEdit()
    {
        // old: "hello world", new: "hello earth" → prefix=6
        var (backspace, insert) = ComputeFlushDiff("hello world", "hello earth");
        Assert.Equal(5, backspace);
        Assert.Equal("earth", insert);
    }

    [Fact]
    public void FlushDiff_BeginningEdit_FullBackspace()
    {
        // old: "AAAA", new: "BAAA" → prefix=0, backspace=4
        // Prefix-only correctly backspaces the entire old text and re-inserts.
        var (backspace, insert) = ComputeFlushDiff("AAAA", "BAAA");
        Assert.Equal(4, backspace);
        Assert.Equal("BAAA", insert);
    }

    [Fact]
    public void FlushDiff_RepeatedPattern_NoOffByOne()
    {
        // Regression test: "ABAB" → "BAB" must delete all 4 chars and insert "BAB".
        // Suffix optimization would incorrectly match "ABA" and produce "ABA".
        var (backspace, insert) = ComputeFlushDiff("ABAB", "BAB");
        Assert.Equal(4, backspace);
        Assert.Equal("BAB", insert);
    }

    [Fact]
    public void FlushDiff_CompletelyDifferent()
    {
        var (backspace, insert) = ComputeFlushDiff("ABC", "XYZ");
        Assert.Equal(3, backspace);
        Assert.Equal("XYZ", insert);
    }

    [Fact]
    public void FlushDiff_EmptyTarget_DeleteAll()
    {
        var (backspace, insert) = ComputeFlushDiff("AAAA", "");
        Assert.Equal(4, backspace);
        Assert.Equal("", insert);
    }

    [Fact]
    public void FlushDiff_EmptyOld_InsertAll()
    {
        var (backspace, insert) = ComputeFlushDiff("", "world");
        Assert.Equal(0, backspace);
        Assert.Equal("world", insert);
    }

    [Fact]
    public void FlushDiff_SingleCharChangeAtStart()
    {
        // old: "aaaa", new: "baaa" → prefix=0, backspace=4
        var (backspace, insert) = ComputeFlushDiff("aaaa", "baaa");
        Assert.Equal(4, backspace);
        Assert.Equal("baaa", insert);
    }

    [Fact]
    public void FlushDiff_SingleCharChangeAtEnd()
    {
        // old: "hello", new: "hellp" → prefix=4
        var (backspace, insert) = ComputeFlushDiff("hello", "hellp");
        Assert.Equal(1, backspace);
        Assert.Equal("p", insert);
    }

    [Fact]
    public void FlushDiff_LongText_BeginningEdit()
    {
        // 200-char text, change first char
        var old = new string('A', 200);
        var newStr = "B" + new string('A', 199);

        var (backspace, insert) = ComputeFlushDiff(old, newStr);

        Assert.Equal(200, backspace);
        Assert.Equal(newStr, insert);
    }

    // === ApplyDiffToBuffer tests ===

    [Fact]
    public void ApplyDiffToBuffer_SimpleAppend()
    {
        var result = ApplyDiffToBuffer("hello", 0, " world");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void ApplyDiffToBuffer_SimpleDelete()
    {
        var result = ApplyDiffToBuffer("hello world", 6, null);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ApplyDiffToBuffer_ReplaceEnd()
    {
        var result = ApplyDiffToBuffer("hello world", 5, "earth");
        Assert.Equal("hello earth", result);
    }

    [Fact]
    public void ApplyDiffToBuffer_DeleteMoreThanLength_Clamped()
    {
        var result = ApplyDiffToBuffer("hi", 100, "bye");
        Assert.Equal("bye", result);
    }

    [Fact]
    public void ApplyDiffToBuffer_EmptyInsert()
    {
        var result = ApplyDiffToBuffer("hello", 5, "");
        Assert.Equal("", result);
    }
}
