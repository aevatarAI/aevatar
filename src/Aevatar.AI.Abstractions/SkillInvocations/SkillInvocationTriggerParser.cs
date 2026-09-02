namespace Aevatar.AI.Abstractions.SkillInvocations;

public static class SkillInvocationTriggerParser
{
    public static bool TryParse(
        string? text,
        string? platform,
        out SkillInvocationTrigger trigger,
        SkillInvocationTriggerOptions? options = null)
    {
        trigger = default!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        options ??= new SkillInvocationTriggerOptions();
        var effectivePlatform = string.IsNullOrWhiteSpace(platform) ? "default" : platform.Trim();
        var tokens = options.GetTokens(effectivePlatform);
        var source = text.TrimStart();
        var lines = SplitLines(source);

        for (var i = 0; i < lines.Count; i++)
        {
            if (!TryMatchLine(source, lines[i], tokens, out var match))
                continue;

            if (match.IsDiscovery)
            {
                trigger = new SkillInvocationTrigger(
                    Name: null,
                    Arguments: string.Empty,
                    IsDiscovery: true,
                    OriginalText: source[match.TokenStart..match.CommandEnd],
                    TriggerToken: match.Token,
                    Platform: effectivePlatform);
                return true;
            }

            // Arguments span from the end of the command name to the start of the next line that
            // is itself a valid trigger — or to the end of the message when there is none. This
            // keeps line-oriented multi-trigger input (one "::cmd" per line) intact while also
            // preserving a pasted multi-line message (e.g. "/draft <body across several lines>")
            // as a single argument, instead of dropping everything after the first newline.
            var argumentsEnd = source.Length;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (TryMatchLine(source, lines[j], tokens, out _))
                {
                    argumentsEnd = lines[j].Start;
                    break;
                }
            }

            var arguments = source[match.CommandEnd..argumentsEnd].Trim();
            trigger = new SkillInvocationTrigger(
                match.Name!.ToLowerInvariant(),
                arguments,
                IsDiscovery: false,
                OriginalText: source[match.TokenStart..argumentsEnd].Trim(),
                TriggerToken: match.Token,
                Platform: effectivePlatform);
            return true;
        }

        return false;
    }

    private static bool TryMatchLine(
        string source,
        LineRange line,
        IReadOnlyList<string> tokens,
        out TriggerMatch match)
    {
        match = default;

        var start = line.Start;
        while (start < line.End && char.IsWhiteSpace(source[start]))
            start++;
        if (start >= line.End)
            return false;

        // End of the candidate with trailing whitespace removed (within this line).
        var trimmedEnd = line.End;
        while (trimmedEnd > start && char.IsWhiteSpace(source[trimmedEnd - 1]))
            trimmedEnd--;

        foreach (var token in tokens)
        {
            if (!StartsWith(source, start, line.End, token))
                continue;

            var tailStart = start + token.Length;

            // Bare canonical token with no trailing content => discovery request.
            if (token == SkillInvocationTriggerOptions.CanonicalTriggerToken &&
                tailStart >= line.End &&
                line.End == trimmedEnd)
            {
                match = new TriggerMatch(token, start, tailStart, Name: null, IsDiscovery: true);
                return true;
            }

            // A legal trigger needs a non-whitespace command name right after the token.
            if (tailStart >= line.End || char.IsWhiteSpace(source[tailStart]))
                continue;

            var nameEnd = tailStart;
            while (nameEnd < line.End && !char.IsWhiteSpace(source[nameEnd]))
                nameEnd++;

            if (!IsValidName(source.AsSpan(tailStart, nameEnd - tailStart)))
                continue;

            match = new TriggerMatch(token, start, nameEnd, source[tailStart..nameEnd], IsDiscovery: false);
            return true;
        }

        return false;
    }

    private static bool StartsWith(string source, int start, int end, string token)
        => end - start >= token.Length &&
           source.AsSpan(start, token.Length).SequenceEqual(token);

    private static bool IsValidName(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return false;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
                continue;

            return false;
        }

        return true;
    }

    private static List<LineRange> SplitLines(string source)
    {
        var lines = new List<LineRange>();
        var start = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != '\n')
                continue;

            var end = i;
            if (end > start && source[end - 1] == '\r')
                end--;

            lines.Add(new LineRange(start, end));
            start = i + 1;
        }

        var lastEnd = source.Length;
        if (lastEnd > start && source[lastEnd - 1] == '\r')
            lastEnd--;

        lines.Add(new LineRange(start, lastEnd));
        return lines;
    }

    // End is exclusive and excludes the line's newline / trailing carriage return.
    private readonly record struct LineRange(int Start, int End);

    private readonly record struct TriggerMatch(
        string Token,
        int TokenStart,
        int CommandEnd,
        string? Name,
        bool IsDiscovery);
}
