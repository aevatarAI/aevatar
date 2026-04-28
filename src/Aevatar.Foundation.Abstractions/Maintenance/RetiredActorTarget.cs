namespace Aevatar.Foundation.Abstractions.Maintenance;

/// <summary>
/// One actor that should be cleaned when its persisted runtime type matches a retired type token.
/// </summary>
/// <param name="ActorId">Stable actor id (e.g. <c>channel-bot-registration-store</c>).</param>
/// <param name="RetiredTypeTokens">CLR type names whose presence in the persisted runtime type marks the actor as retired.</param>
/// <param name="SourceStreamId">Optional parent stream that produced this actor as a relay (set for projection scope actors).</param>
/// <param name="CleanupReadModels">When true, the owning spec's read-model cleaner is invoked for this actor.</param>
/// <param name="ResetWhenRuntimeTypeUnavailable">When true, the event stream is reset even when the runtime type cannot be resolved (recovery path for partially-cleaned actors).</param>
public sealed record RetiredActorTarget(
    string ActorId,
    IReadOnlyList<string> RetiredTypeTokens,
    string? SourceStreamId = null,
    bool CleanupReadModels = false,
    bool ResetWhenRuntimeTypeUnavailable = true)
{
    /// <summary>
    /// True when <paramref name="runtimeTypeName"/> contains any retired token as a
    /// whole CLR-type-name segment (boundary-aware so substrings such as
    /// <c>...GAgentProxy</c> do not accidentally match <c>...GAgent</c>).
    /// </summary>
    public bool MatchesRuntimeType(string? runtimeTypeName)
    {
        if (string.IsNullOrWhiteSpace(runtimeTypeName))
            return false;

        foreach (var token in RetiredTypeTokens)
        {
            if (ContainsTypeNameToken(runtimeTypeName, token))
                return true;
        }

        return false;
    }

    private static bool ContainsTypeNameToken(string runtimeTypeName, string token)
    {
        var startIndex = 0;
        while (startIndex < runtimeTypeName.Length)
        {
            var index = runtimeTypeName.IndexOf(token, startIndex, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var beforeOk = index == 0 || IsTypeNameBoundary(runtimeTypeName[index - 1]);
            var afterIndex = index + token.Length;
            var afterOk = afterIndex == runtimeTypeName.Length || IsTypeNameBoundary(runtimeTypeName[afterIndex]);
            if (beforeOk && afterOk)
                return true;

            startIndex = index + token.Length;
        }

        return false;
    }

    private static bool IsTypeNameBoundary(char value) =>
        value is '[' or ']' or ',' or ' ';
}
