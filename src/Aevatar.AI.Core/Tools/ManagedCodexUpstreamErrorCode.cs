namespace Aevatar.AI.Core.Tools;

internal static class ManagedCodexUpstreamErrorCode
{
    private const string Prefix = "managed_upstream_codex_";
    private const int MaxLength = 96;

    public static string? Resolve(string? value) =>
        value is not null &&
        value.Length > Prefix.Length &&
        value.Length <= MaxLength &&
        value.StartsWith(Prefix, StringComparison.Ordinal) &&
        value.All(character =>
            char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character) ||
            character == '_')
            ? value
            : null;
}
