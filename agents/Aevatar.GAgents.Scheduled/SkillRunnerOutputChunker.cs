namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Splits a SkillRunner output into Lark-deliverable chunks when the report exceeds the
/// platform body cap. Issue #423 §C: prior to this helper, outputs &gt; 30 KB were truncated
/// at the cap with a marker, which silently dropped the tail of richer reports. The chunker
/// preserves the full content by splitting at paragraph (<c>\n\n</c>) boundaries when one
/// exists within the per-chunk budget, falling back to a character-boundary split for
/// pathological no-paragraph inputs.
/// </summary>
/// <remarks>
/// <para>
/// The cap budget reserves headroom for continuation markers ("part k/N") so the rendered
/// chunks (raw + markers) still fit under <see cref="SkillRunnerStreamingReplySink.MaxLarkTextLength"/>
/// — without the reservation a chunk filling the budget would re-overflow once the marker
/// was prepended/appended.
/// </para>
/// <para>
/// The chunker is a pure function: no I/O, no allocations beyond the result list. The actual
/// dispatch loop lives in <c>SkillRunnerGAgent.ExecuteSkillAsync</c>, which sends chunk[0]
/// through the streaming-edit sink (so the in-flight message lands as part 1) and chunks
/// 1..N as fresh one-shot POSTs through <c>SendOutputAsync</c>.
/// </para>
/// </remarks>
internal static class SkillRunnerOutputChunker
{
    private const string ContinuesSuffixFormat = "\n\n[part {0}/{1} • continues ↓]";
    private const string ContinuedPrefixFormat = "[part {0}/{1} • continued ↑]\n\n";

    /// <summary>
    /// Reserved per-chunk overhead for continuation markers. The longest rendered marker
    /// (e.g. <c>"\n\n[part 99/99 • continues ↓]"</c>) is well under this; 60 chars is a
    /// safe ceiling that does not need to track marker template width.
    /// </summary>
    private const int MarkerOverhead = 60;

    /// <summary>
    /// Splits <paramref name="output"/> into one or more chunks suitable for Lark text
    /// delivery. When <paramref name="output"/> already fits, returns a single-element list
    /// containing the input verbatim — callers can therefore use the same dispatch loop for
    /// both single-message and chunked-message paths.
    /// </summary>
    public static IReadOnlyList<string> Split(string output)
    {
        if (string.IsNullOrEmpty(output))
            return [output ?? string.Empty];

        if (output.Length <= SkillRunnerStreamingReplySink.MaxLarkTextLength)
            return [output];

        var contentBudget = SkillRunnerStreamingReplySink.MaxLarkTextLength - MarkerOverhead;
        if (contentBudget < 100)
            // Defensive: keep the algorithm well-formed if MaxLarkTextLength ever shrinks.
            // Content must always have positive room or the loop cannot make progress.
            contentBudget = SkillRunnerStreamingReplySink.MaxLarkTextLength;

        var rawChunks = SplitRaw(output, contentBudget);
        var total = rawChunks.Count;
        if (total == 1)
            return rawChunks;

        var rendered = new List<string>(total);
        for (var i = 0; i < total; i++)
        {
            var partNum = i + 1;
            var prefix = i > 0 ? string.Format(ContinuedPrefixFormat, partNum, total) : string.Empty;
            var suffix = i < total - 1 ? string.Format(ContinuesSuffixFormat, partNum, total) : string.Empty;
            rendered.Add(prefix + rawChunks[i] + suffix);
        }

        return rendered;
    }

    private static List<string> SplitRaw(string output, int contentBudget)
    {
        var chunks = new List<string>();
        var offset = 0;
        while (offset < output.Length)
        {
            var remaining = output.Length - offset;
            if (remaining <= contentBudget)
            {
                chunks.Add(output[offset..]);
                break;
            }

            // C# `LastIndexOf(value, startIndex, count)` searches the inclusive range
            // [startIndex - count + 1, startIndex] backwards. We want the latest "\n\n" in
            // [offset, offset + contentBudget - 1] so partial matches at the end of the
            // window (e.g. at offset + contentBudget - 1, where only the first '\n' falls
            // inside the budget) are still considered. The `count` argument intentionally
            // overshoots by 1 at the trailing edge so a "\n\n" anchored at
            // (offset + contentBudget - 1) with its second '\n' just outside the budget is
            // still picked up — the chunk content ends BEFORE the boundary so the second
            // '\n' is consumed by `offset = boundary + 2` regardless.
            var searchAnchor = offset + contentBudget - 1;
            var boundary = output.LastIndexOf("\n\n", searchAnchor, contentBudget, StringComparison.Ordinal);
            if (boundary <= offset)
            {
                // No paragraph boundary in budget (or boundary is at the very start, which
                // would produce an empty chunk). Hard-split at the budget — pathological
                // inputs (no \n\n at all, or a runaway single paragraph) still get
                // delivered chunked, just at character-boundary precision.
                chunks.Add(output[offset..(offset + contentBudget)]);
                offset += contentBudget;
            }
            else
            {
                chunks.Add(output[offset..boundary]);
                offset = boundary + 2; // skip the "\n\n"
            }
        }
        return chunks;
    }
}
