namespace Aevatar.GAgents.Channel.Runtime;

public static class ChannelTextCommandParser
{
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var current = new List<char>(text!.Length);
        var quote = '\0';

        foreach (var ch in text)
        {
            if (quote == '\0')
            {
                if (ch is '"' or '\'')
                {
                    quote = ch;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    FlushToken(tokens, current);
                    continue;
                }

                current.Add(ch);
                continue;
            }

            if (ch == quote)
            {
                quote = '\0';
                continue;
            }

            current.Add(ch);
        }

        FlushToken(tokens, current);
        return tokens;
    }

    public static IReadOnlyDictionary<string, string> ParseNamedArguments(
        IReadOnlyList<string> tokens,
        int startIndex = 1)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var separatorIndex = token.IndexOf('=');
            if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
                continue;

            var key = token[..separatorIndex].Trim();
            var value = token[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
                continue;

            values[key] = value;
        }

        return values;
    }

    private static void FlushToken(List<string> tokens, List<char> current)
    {
        if (current.Count == 0)
            return;

        tokens.Add(new string(current.ToArray()));
        current.Clear();
    }
}
