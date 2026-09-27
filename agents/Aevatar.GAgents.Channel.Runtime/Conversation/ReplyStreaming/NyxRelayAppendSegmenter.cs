using System.Globalization;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>Pure boundary selection; the actor owns all append state.</summary>
internal static class NyxRelayAppendSegmenter
{
    private const int FirstSegmentTarget = 800;
    private const int FirstSegmentMinimumNaturalBoundary = 400;
    private const int SubsequentSegmentTarget = 1600;

    public static string Select(
        string suffix,
        int acceptedCount,
        int maxLength,
        bool terminal,
        bool progressDue,
        Func<string, string> prepare)
    {
        if (suffix.Length == 0)
            return string.Empty;
        if (maxLength <= 0)
            throw new InvalidOperationException("append_invalid_text_limit");

        var starts = StringInfo.ParseCombiningCharacters(suffix);
        var boundaries = starts.Skip(1).Append(suffix.Length).ToArray();
        var firstVisibleStart = -1;
        var lastVisibleStart = -1;
        for (var index = 0; index < starts.Length; index++)
        {
            if (suffix.AsSpan(starts[index], boundaries[index] - starts[index]).IsWhiteSpace())
                continue;
            if (firstVisibleStart < 0)
                firstVisibleStart = starts[index];
            lastVisibleStart = starts[index];
        }
        if (lastVisibleStart < 0)
            return string.Empty;
        // The final grapheme of a growing stream can still gain a combining mark or ZWJ tail.
        // Keep its preceding visible grapheme when the remaining tail is whitespace, so
        // terminal can send that tail without replaying or falsely crediting accepted text.
        var stableEnd = terminal ? suffix.Length : Math.Min(lastVisibleStart, boundaries.Length > 1 ? boundaries[^2] : 0);
        // A whitespace-only stable prefix must wait with the withheld visible grapheme.
        if (stableEnd <= firstVisibleStart)
            return string.Empty;
        var target = terminal || progressDue
            ? maxLength
            : acceptedCount == 0 ? FirstSegmentTarget : SubsequentSegmentTarget;
        // Soft targets control pacing. Exhaustion must still search the true hard cap.
        foreach (var selectionLimit in new[] { Math.Min(target, maxLength), maxLength }.Distinct())
        {
            var end = boundaries.LastOrDefault(value => value <= Math.Min(stableEnd, selectionLimit));
            if (end == 0)
                continue;

            var natural = FindNaturalBoundary(suffix, boundaries, end, firstVisibleStart);
            if (!terminal && !progressDue && stableEnd < target)
            {
                if (acceptedCount != 0 || natural < FirstSegmentMinimumNaturalBoundary)
                    return string.Empty;
                end = natural;
            }
            else if (end < stableEnd && natural > 0 &&
                     (terminal || progressDue || acceptedCount != 0 ||
                      natural >= FirstSegmentMinimumNaturalBoundary))
            {
                end = natural;
            }

            foreach (var candidateEnd in boundaries.Where(value => value <= end).Reverse())
            {
                // A terminal answer longer than one segment must also leave a sendable suffix.
                if (candidateEnd < suffix.Length && candidateEnd > lastVisibleStart)
                    continue;
                var candidate = suffix[..candidateEnd];
                if (!HasValidSurrogates(candidate))
                    throw new InvalidOperationException("append_invalid_unicode");
                var prepared = prepare(candidate);
                if (!string.IsNullOrWhiteSpace(prepared) && prepared.Length <= maxLength)
                    return candidate;
            }
        }
        throw new InvalidOperationException("append_grapheme_exceeds_limit");
    }

    private static int FindNaturalBoundary(string text, int[] boundaries, int maxEnd, int firstVisibleStart)
    {
        foreach (var priority in new[] { 0, 1, 2, 3 })
        {
            foreach (var end in boundaries.Where(value => value <= maxEnd && value > firstVisibleStart).Reverse())
            {
                if ((priority == 0 && end >= 2 && text[end - 1] == '\n' && text[end - 2] == '\n') ||
                    (priority == 1 && text[end - 1] == '\n') ||
                    (priority == 2 && ".!?。！？；;".Contains(text[end - 1])) ||
                    (priority == 3 && char.IsWhiteSpace(text[end - 1])))
                    return end;
            }
        }
        return 0;
    }

    private static bool HasValidSurrogates(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index == text.Length || !char.IsLowSurrogate(text[index]))
                    return false;
            }
            else if (char.IsLowSurrogate(text[index]))
                return false;
        }
        return true;
    }
}
