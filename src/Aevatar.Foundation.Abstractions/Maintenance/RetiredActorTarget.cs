namespace Aevatar.Foundation.Abstractions.Maintenance;

/// <summary>
/// One actor that should be cleaned when its runtime kind matches a retired kind token.
/// </summary>
/// <param name="ActorId">Stable actor id (e.g. <c>channel-bot-registration-store</c>).</param>
/// <param name="RetiredKindTokens">Primary kind tokens whose presence marks the actor as retired.</param>
/// <param name="SourceStreamId">Optional parent stream that produced this actor as a relay (set for projection scope actors).</param>
/// <param name="CleanupReadModels">When true, the owning spec's read-model cleaner is invoked for this actor.</param>
/// <param name="ResetWhenRuntimeKindUnavailable">When true, the event stream is reset even when the runtime kind cannot be resolved (recovery path for partially-cleaned actors).</param>
public sealed record RetiredActorTarget(
    string ActorId,
    IReadOnlyList<string> RetiredKindTokens,
    string? SourceStreamId = null,
    bool CleanupReadModels = false,
    bool ResetWhenRuntimeKindUnavailable = true)
{
    public string ActorId { get; init; } = RequireNonEmpty(ActorId, nameof(ActorId));

    public IReadOnlyList<string> RetiredKindTokens { get; init; } = ValidateTokens(RetiredKindTokens);

    /// <summary>
    /// True when <paramref name="runtimeKind"/> equals any retired kind token.
    /// </summary>
    public bool MatchesRuntimeKind(string? runtimeKind)
    {
        if (string.IsNullOrWhiteSpace(runtimeKind))
            return false;

        foreach (var token in RetiredKindTokens)
        {
            if (string.Equals(runtimeKind, token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string RequireNonEmpty(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static IReadOnlyList<string> ValidateTokens(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0)
        {
            throw new ArgumentException(
                "RetiredKindTokens must contain at least one primary agent kind.",
                nameof(tokens));
        }

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token) || !token.Contains('.', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Retired kind token '{token}' must be a module-qualified agent kind (containing '.').",
                    nameof(tokens));
            }
        }

        return tokens;
    }
}
