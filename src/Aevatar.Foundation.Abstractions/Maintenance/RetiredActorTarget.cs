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
    bool ResetWhenRuntimeTypeUnavailable = true);
