namespace Aevatar.AI.Abstractions.LLMProviders;

public static class NyxIdServiceSlugPolicy
{
    public const int MaxLength = 80;

    public static bool IsCanonical(string? value)
    {
        if (value is null ||
            value.Length is < 1 or > MaxLength ||
            value[0] == '-' ||
            value[^1] == '-' ||
            value.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')
                return false;
        }

        return true;
    }
}
