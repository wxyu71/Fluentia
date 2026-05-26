using Fluentia.Services;
using Xunit;

namespace Fluentia.Tests;

/// <summary>
/// Tests for diff batching and suffix optimization in MainWindow.
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

    // === FlushBufferedDiff logic (with suffix optimization) ===
    // Replicates the logic from MainWindow.FlushBufferedDiff
    private static (int backspace, string insert) ComputeFlushDiff(string oldText, string nextText)
    {
        // Prefix optimization
        var prefix = 0;
        var limit = Math.Min(oldText.Length, nextText.Length);
        while (prefix < limit && oldText[prefix] == nextText[prefix])
        {
            prefix++;
        }

        // Suffix optimization
        var suffix = 0;
        var oldEnd = oldText.Length - 1;
        var newEnd = nextText.Length - 1;
        while (suffix < limit - prefix &&
               oldEnd - suffix >= prefix &&
               newEnd - suffix >= prefix &&
               oldText[oldEnd - suffix] == nextText[newEnd - suffix])
        {
            suffix++;
        }

        // Surrogate pair safety
        if (suffix > 0)
        {
            var suffixStart = oldText.Length - suffix;
            if (suffixStart > 0 && suffixStart < oldText.Length &&
                char.IsLowSurrogate(oldText[suffixStart]))
            {
                suffix--;
            }
        }

        var backspace = oldText.Length - prefix - suffix;
        var insertLength = nextText.Length - prefix - suffix;
        var insert = insertLength > 0 ? nextText.Substring(prefix, insertLength) : string.Empty;
        return (backspace, insert);
    }

    // === Suffix optimization tests ===

    [Fact]
    public void SuffixOptimization_BeginningEdit_ReducesBackspace()
    {
        // old: "AAAA", new: "BAAA" → prefix=0, suffix=3
        var (backspace, insert) = ComputeFlushDiff("AAAA", "BAAA");
        Assert.Equal(1, backspace);
        Assert.Equal("B", insert);
    }

    [Fact]
    public void SuffixOptimization_NoCommonSuffix_FullBackspace()
    {
        // old: "ABC", new: "XYZ" → prefix=0, suffix=0
        var (backspace, insert) = ComputeFlushDiff("ABC", "XYZ");
        Assert.Equal(3, backspace);
        Assert.Equal("XYZ", insert);
    }

    [Fact]
    public void SuffixOptimization_EmptyTarget_DeleteAll()
    {
        // old: "AAAA", new: "" → prefix=0, suffix=0
        var (backspace, insert) = ComputeFlushDiff("AAAA", "");
        Assert.Equal(4, backspace);
        Assert.Equal("", insert);
    }

    [Fact]
    public void SuffixOptimization_AppendAtEnd_NoSuffixNeeded()
    {
        // old: "hello", new: "hello world" → prefix=5, suffix=0
        var (backspace, insert) = ComputeFlushDiff("hello", "hello world");
        Assert.Equal(0, backspace);
        Assert.Equal(" world", insert);
    }

    [Fact]
    public void SuffixOptimization_MiddleEdit()
    {
        // old: "hello world", new: "hello earth" → prefix=6, suffix=0
        var (backspace, insert) = ComputeFlushDiff("hello world", "hello earth");
        Assert.Equal(5, backspace);
        Assert.Equal("earth", insert);
    }

    [Fact]
    public void SuffixOptimization_SingleCharChangeAtStart()
    {
        // old: "aaaa", new: "baaa" → prefix=0, suffix=3
        var (backspace, insert) = ComputeFlushDiff("aaaa", "baaa");
        Assert.Equal(1, backspace);
        Assert.Equal("b", insert);
    }

    [Fact]
    public void SuffixOptimization_IdenticalText()
    {
        var (backspace, insert) = ComputeFlushDiff("hello", "hello");
        Assert.Equal(0, backspace);
        Assert.Equal("", insert);
    }

    [Fact]
    public void SuffixOptimization_CompletelyDifferent()
    {
        var (backspace, insert) = ComputeFlushDiff("", "world");
        Assert.Equal(0, backspace);
        Assert.Equal("world", insert);
    }

    [Fact]
    public void SuffixOptimization_LongText_BeginningEdit()
    {
        // Simulate 200-char text, change first char
        var old = new string('A', 200);
        var newStr = "B" + new string('A', 199);

        var (backspace, insert) = ComputeFlushDiff(old, newStr);

        // prefix=0, suffix=199, backspace=1, insert="B"
        Assert.Equal(1, backspace);
        Assert.Equal("B", insert);
    }

    [Fact]
    public void SuffixOptimization_LongText_MultipleBeginningEdits()
    {
        // Simulate: "AAAA...A" (200) → "BAAA...A" → "BCAA...A"
        var old = new string('A', 200);
        var mid = "B" + new string('A', 199);
        var final = "BC" + new string('A', 198);

        var (bs1, ins1) = ComputeFlushDiff(old, mid);
        Assert.Equal(1, bs1);
        Assert.Equal("B", ins1);

        var (bs2, ins2) = ComputeFlushDiff(mid, final);
        Assert.Equal(1, bs2);
        Assert.Equal("C", ins2);
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
