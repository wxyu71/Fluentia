using Fluentia.Services;

namespace Fluentia.Tests;

public class RegexRuleImportServiceTests
{
    [Fact]
    public void TryImport_ValidMarkdown_ReturnsRules()
    {
        var md = "```regex\n\\b(?:um|uh)\\b\n```";
        var success = RegexRuleImportService.TryImport(md, out var result, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Single(result.Rules);
        Assert.Contains("um|uh", result.Rules[0]);
    }

    [Fact]
    public void TryImport_MultipleRules()
    {
        var md = "```regex\n\\bum\\b\n\\buh\\b\n```";
        var success = RegexRuleImportService.TryImport(md, out var result, out _);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal(2, result.Rules.Count);
    }

    [Fact]
    public void TryImport_EmptyInput_ReturnsError()
    {
        var success = RegexRuleImportService.TryImport("", out _, out var error);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryImport_NullInput_ReturnsError()
    {
        var success = RegexRuleImportService.TryImport(null!, out _, out var error);

        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryImport_NoCodeBlock_ReturnsError()
    {
        var success = RegexRuleImportService.TryImport("no code block here", out _, out var error);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("code block", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryImport_SkipsCommentLines()
    {
        var md = "```regex\n# This is a comment\n\\bum\\b\n// Another comment\n\\buh\\b\n```";
        var success = RegexRuleImportService.TryImport(md, out var result, out _);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal(2, result.Rules.Count);
    }

    [Fact]
    public void TryImport_EmptyCodeBlock_ReturnsError()
    {
        var md = "```regex\n\n```";
        var success = RegexRuleImportService.TryImport(md, out _, out var error);

        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryImport_InvalidRegex_ReturnsError()
    {
        var md = "```regex\n[invalid\n```";
        var success = RegexRuleImportService.TryImport(md, out _, out var error);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("Invalid regex", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryImport_SlashSyntax_ParsesFlags()
    {
        var md = "```regex\n/\\bum\\b/i\n```";
        var success = RegexRuleImportService.TryImport(md, out var result, out _);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Single(result.Rules);
    }

    [Fact]
    public void TryImport_MultipleCodeBlocks()
    {
        var md = "```regex\n\\bum\\b\n```\n\n```regex\n\\buh\\b\n```";
        var success = RegexRuleImportService.TryImport(md, out var result, out _);

        Assert.True(success);
        Assert.NotNull(result);
        Assert.Equal(2, result.Rules.Count);
    }

    [Fact]
    public void TryImport_NormalizedMarkdown_ContainsCodeBlock()
    {
        var md = "```regex\n\\bum\\b\n\\buh\\b\n```";
        RegexRuleImportService.TryImport(md, out var result, out _);

        Assert.NotNull(result);
        Assert.Contains("```regex", result.NormalizedMarkdown);
        Assert.Contains("```", result.NormalizedMarkdown);
    }
}
