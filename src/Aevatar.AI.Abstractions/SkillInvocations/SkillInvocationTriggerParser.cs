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
        foreach (var line in EnumerateLines(text.TrimStart()))
        {
            var candidate = line.TrimStart();
            var candidateWithoutTrailingWhitespace = candidate.TrimEnd();
            if (candidate.Length == 0)
                continue;

            foreach (var token in options.GetTokens(effectivePlatform))
            {
                if (!candidate.StartsWith(token, StringComparison.Ordinal))
                    continue;

                if (!TryParseCandidate(candidate, candidateWithoutTrailingWhitespace, token, effectivePlatform, out trigger))
                    continue;

                return true;
            }
        }

        return false;
    }

    private static bool TryParseCandidate(
        string candidate,
        string candidateWithoutTrailingWhitespace,
        string token,
        string platform,
        out SkillInvocationTrigger trigger)
    {
        trigger = default!;
        var tail = candidate[token.Length..];
        if (token == SkillInvocationTriggerOptions.CanonicalTriggerToken &&
            tail.Length == 0 &&
            candidate.Length == candidateWithoutTrailingWhitespace.Length)
        {
            trigger = new SkillInvocationTrigger(
                Name: null,
                Arguments: string.Empty,
                IsDiscovery: true,
                OriginalText: candidate,
                TriggerToken: token,
                Platform: platform);
            return true;
        }

        if (tail.Length == 0 || char.IsWhiteSpace(tail[0]))
            return false;

        var nameEnd = 0;
        while (nameEnd < tail.Length && !char.IsWhiteSpace(tail[nameEnd]))
            nameEnd++;

        var rawName = tail[..nameEnd];
        if (!IsValidName(rawName))
            return false;

        var arguments = nameEnd >= tail.Length ? string.Empty : tail[nameEnd..].Trim();
        trigger = new SkillInvocationTrigger(
            rawName.ToLowerInvariant(),
            arguments,
            IsDiscovery: false,
            OriginalText: candidate,
            TriggerToken: token,
            Platform: platform);
        return true;
    }

    private static bool IsValidName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
                continue;

            return false;
        }

        return true;
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            yield return text[start..i].TrimEnd('\r');
            start = i + 1;
        }

        yield return text[start..].TrimEnd('\r');
    }
}
