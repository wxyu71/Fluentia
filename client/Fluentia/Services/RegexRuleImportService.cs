using System.Text.RegularExpressions;

namespace Fluentia.Services;

public sealed record RegexImportResult(IReadOnlyList<string> Rules, string NormalizedMarkdown);

public static class RegexRuleImportService
{
    private static readonly Regex CodeBlockPattern = new("```(?:regex|regexp)?\\s*\\n([\\s\\S]*?)```", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryImport(string markdown, out RegexImportResult? result, out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(markdown))
        {
            error = "Regex configuration is empty.";
            return false;
        }

        var matches = CodeBlockPattern.Matches(markdown);
        if (matches.Count == 0)
        {
            error = "No regex code block found. Paste the AI output with a fenced code block.";
            return false;
        }

        var rules = new List<string>();
        var normalizedBlocks = new List<string>();

        foreach (Match match in matches)
        {
            var block = match.Groups[1].Value;
            var normalizedLines = new List<string>();

            foreach (var rawLine in block.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (rawLine.StartsWith('#') || rawLine.StartsWith("//"))
                {
                    continue;
                }

                if (!TryValidateRule(rawLine, out error))
                {
                    return false;
                }

                rules.Add(rawLine);
                normalizedLines.Add(rawLine);
            }

            if (normalizedLines.Count > 0)
            {
                normalizedBlocks.Add(string.Join(Environment.NewLine, new[] { "```regex" }.Concat(normalizedLines).Concat(new[] { "```" })));
            }
        }

        if (rules.Count == 0)
        {
            error = "No regex rule found inside the Markdown code block.";
            return false;
        }

        result = new RegexImportResult(rules, string.Join(Environment.NewLine + Environment.NewLine, normalizedBlocks));
        return true;
    }

    private static bool TryValidateRule(string line, out string? error)
    {
        error = null;
        var trimmed = line.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "Regex rule cannot be empty.";
            return false;
        }

        string source;
        string flags;

        if (trimmed.StartsWith('/') && trimmed.LastIndexOf('/') > 0)
        {
            var lastSlash = trimmed.LastIndexOf('/');
            source = trimmed[1..lastSlash];
            flags = trimmed[(lastSlash + 1)..];
        }
        else
        {
            source = trimmed;
            flags = string.Empty;
        }

        try
        {
            _ = new Regex(source, ParseOptions(flags));
            return true;
        }
        catch (ArgumentException ex)
        {
            error = $"Invalid regex '{trimmed}': {ex.Message}";
            return false;
        }
    }

    private static RegexOptions ParseOptions(string flags)
    {
        var options = RegexOptions.CultureInvariant;
        foreach (var flag in flags)
        {
            options |= flag switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                's' => RegexOptions.Singleline,
                'g' => RegexOptions.None,
                'u' => RegexOptions.None,
                _ => throw new ArgumentException($"Unsupported regex flag: {flag}"),
            };
        }

        return options;
    }
}
