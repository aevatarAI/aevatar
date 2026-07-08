namespace Aevatar.AI.ToolProviders.Ornn.SystemSkillOverlay;

/// <summary>
/// Extracts the overlay body from a published skill's <c>SKILL.md</c> by stripping a single leading
/// YAML frontmatter block (<c>---\n…\n---</c>). Every Ornn-published skill carries standard frontmatter
/// (name/description/version/metadata), which is metadata for publishing — not overlay content — so it
/// must not leak into the composed prompt. Content without frontmatter is returned as-is.
/// </summary>
public static class OverlayBodyExtractor
{
    public static string ExtractBody(string? skillMarkdown)
    {
        if (string.IsNullOrWhiteSpace(skillMarkdown))
            return string.Empty;

        var openingEnd = skillMarkdown.StartsWith("---\n", StringComparison.Ordinal)
            ? 4
            : skillMarkdown.StartsWith("---\r\n", StringComparison.Ordinal)
                ? 5
                : -1;
        if (openingEnd < 0)
            return skillMarkdown.Trim();

        var closingIndex = FindClosingDelimiter(skillMarkdown, openingEnd);
        if (closingIndex < 0)
            return skillMarkdown.Trim();

        return skillMarkdown[GetBodyStart(skillMarkdown, closingIndex)..].Trim();
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
