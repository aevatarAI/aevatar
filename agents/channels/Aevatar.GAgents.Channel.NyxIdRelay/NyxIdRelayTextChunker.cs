namespace Aevatar.GAgents.Channel.NyxIdRelay;

internal static class NyxIdRelayTextChunker
{
    public const int LarkMaxTextLength = 30_000;

    private const int MarkerOverhead = 80;
    private const string ContinuesSuffixFormat = "\n\n[part {0}/{1} continues]";
    private const string ContinuedPrefixFormat = "[part {0}/{1} continued]\n\n";

    public static IReadOnlyList<string> SplitForLark(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [text ?? string.Empty];

        if (text.Length <= LarkMaxTextLength)
            return [text];

        var contentBudget = Math.Max(1_000, LarkMaxTextLength - MarkerOverhead);
        var rawChunks = SplitRaw(text, contentBudget);
        if (rawChunks.Count == 1)
            return rawChunks;

        var rendered = new List<string>(rawChunks.Count);
        for (var i = 0; i < rawChunks.Count; i++)
        {
            var partNumber = i + 1;
            var prefix = i == 0 ? string.Empty : string.Format(ContinuedPrefixFormat, partNumber, rawChunks.Count);
            var suffix = i == rawChunks.Count - 1 ? string.Empty : string.Format(ContinuesSuffixFormat, partNumber, rawChunks.Count);
            rendered.Add(prefix + rawChunks[i] + suffix);
        }

        return rendered;
    }

    private static List<string> SplitRaw(string text, int contentBudget)
    {
        var chunks = new List<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var remaining = text.Length - offset;
            if (remaining <= contentBudget)
            {
                chunks.Add(text[offset..]);
                break;
            }

            var searchAnchor = offset + contentBudget - 1;
            var boundary = text.LastIndexOf("\n\n", searchAnchor, contentBudget, StringComparison.Ordinal);
            if (boundary <= offset)
            {
                chunks.Add(text[offset..(offset + contentBudget)]);
                offset += contentBudget;
            }
            else
            {
                chunks.Add(text[offset..boundary]);
                offset = boundary + 2;
            }
        }

        return chunks;
    }
}
