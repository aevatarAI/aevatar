namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Honest accepted status returned by the catalog command port.
/// </summary>
public enum CatalogCommandOutcome
{
    /// <summary>Command was dispatched into the catalog actor inbox; projection has not yet caught up.</summary>
    Accepted = 0,
}

public sealed record UserAgentCatalogUpsertResult(CatalogCommandOutcome Outcome);

public sealed record UserAgentCatalogTombstoneResult(CatalogCommandOutcome Outcome);

/// <summary>
/// Application-service surface for catalog mutations. Owns projection
/// priming, envelope construction, dispatch through
/// <see cref="Aevatar.Foundation.Abstractions.IActorDispatchPort"/>, and
/// accepted-only command ACKs so callers (LLM tools, Studio admin endpoints,
/// etc.) stay thin parameter-mapping adapters.
/// Refactor (iter1/cluster-001):
///   Old pattern: catalog mutation plumbing also carried per-runner execution status.
///   New principle: command port mutations are membership-only; execution is projected from runner commits.
/// Refactor (iter4/cluster-009):
///   Old pattern: Command port outcomes encoded opportunistic readmodel observation.
///   New principle: Command outcomes are accepted-only; readmodel freshness is queried explicitly.
/// </summary>
public interface IUserAgentCatalogCommandPort
{
    Task<UserAgentCatalogUpsertResult> UpsertAsync(
        UserAgentCatalogUpsertCommand command,
        CancellationToken ct = default);

    Task<UserAgentCatalogTombstoneResult> TombstoneAsync(
        string agentId,
        CancellationToken ct = default);
}
