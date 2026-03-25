using System.Text;

namespace Aevatar.Studio.Application.Studio;

public static class StudioDocumentIdNormalizer
{
    public static string Normalize(string? rawValue, string fallbackPrefix)
    {
        var trimmed = string.IsNullOrWhiteSpace(rawValue)
            ? string.Empty
            : rawValue.Trim().ToLowerInvariant();
        var builder = new StringBuilder(trimmed.Length);
        var lastWasDash = false;
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasDash = false;
                continue;
            }

            if (ch is '-' or '_' or ' ' or '.')
            {
                if (lastWasDash)
                    continue;

                builder.Append('-');
                lastWasDash = true;
            }
        }

        var normalized = builder
            .ToString()
            .Trim('-');
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        return $"{fallbackPrefix}-{Guid.NewGuid():N}"[..16];
    }
}
