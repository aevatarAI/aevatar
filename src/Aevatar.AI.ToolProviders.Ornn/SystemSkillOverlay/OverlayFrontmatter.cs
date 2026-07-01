namespace Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;

public sealed record OverlayFrontmatter(
    string? Title,
    string? Scope,
    string? Priority,
    string? MaxBytes,
    string? AppliesTo,
    string? NonOverride)
{
    public static OverlayFrontmatter? Parse(string markdown) => OverlayFrontmatterParser.Parse(markdown);
}

public static class OverlayFrontmatterParser
{
    public static OverlayFrontmatter? Parse(string markdown)
    {
        return TryParse(markdown, out var frontmatter, out _)
            ? frontmatter
            : null;
    }

    public static bool TryParse(string markdown, out OverlayFrontmatter? frontmatter, out string body)
    {
        frontmatter = null;
        body = string.Empty;

        if (string.IsNullOrWhiteSpace(markdown))
            return false;

        var openingEnd = markdown.StartsWith("---\n", StringComparison.Ordinal)
            ? 4
            : markdown.StartsWith("---\r\n", StringComparison.Ordinal)
                ? 5
                : -1;
        if (openingEnd < 0)
            return false;

        var closingIndex = FindClosingDelimiter(markdown, openingEnd);
        if (closingIndex < 0)
            return false;

        var block = markdown[openingEnd..closingIndex];
        string? title = null;
        string? scope = null;
        string? priority = null;
        string? maxBytes = null;
        string? appliesTo = null;
        string? nonOverride = null;

        foreach (var rawLine in block.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
                return false;

            var key = line[..colonIndex].Trim().Replace('-', '_').ToLowerInvariant();
            var value = Unquote(line[(colonIndex + 1)..].Trim());

            switch (key)
            {
                case "title":
                    title = value;
                    break;
                case "scope":
                    scope = value;
                    break;
                case "priority":
                    priority = value;
                    break;
                case "max_bytes":
                    maxBytes = value;
                    break;
                case "applies_to":
                    appliesTo = value;
                    break;
                case "non_override":
                    nonOverride = value;
                    break;
            }
        }

        frontmatter = new OverlayFrontmatter(title, scope, priority, maxBytes, appliesTo, nonOverride);
        body = markdown[GetBodyStart(markdown, closingIndex)..].TrimStart('\r', '\n');
        return true;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static int FindClosingDelimiter(string markdown, int startIndex)
    {
        var searchIndex = startIndex;
        while (searchIndex < markdown.Length)
        {
            var delimiterIndex = markdown.IndexOf("\n---", searchIndex, StringComparison.Ordinal);
            if (delimiterIndex < 0)
                return -1;

            var afterDashes = delimiterIndex + 4;
            if (afterDashes == markdown.Length ||
                markdown[afterDashes] == '\n' ||
                markdown[afterDashes] == '\r')
            {
                return delimiterIndex;
            }

            searchIndex = afterDashes;
        }

        return -1;
    }

    private static int GetBodyStart(string markdown, int closingDelimiterIndex)
    {
        var bodyStart = closingDelimiterIndex + 4;
        if (bodyStart < markdown.Length && markdown[bodyStart] == '\r')
            bodyStart++;
        if (bodyStart < markdown.Length && markdown[bodyStart] == '\n')
            bodyStart++;

        return bodyStart;
    }
}
