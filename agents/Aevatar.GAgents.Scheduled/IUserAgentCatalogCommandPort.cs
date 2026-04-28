namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Honest accepted / observed status returned by the catalog command port.
/// </summary>
public enum CatalogCommandOutcome
{
    /// <summary>Command was dispatched into the catalog actor inbox; projection has not yet caught up.</summary>
    Accepted = 0,

    /// <summary>Command was dispatched and the projection has materialized the resulting state version.</summary>
    Observed = 1,

    /// <summary>Tombstone path: requested agent id was not present at the time of the call.</summary>
    NotFound = 2,
}

public sealed record UserAgentCatalogUpsertResult(CatalogCommandOutcome Outcome);

public sealed record UserAgentCatalogTombstoneResult(CatalogCommandOutcome Outcome);

/// <summary>
/// Application-service surface for catalog mutations. Owns projection
/// priming, envelope construction, dispatch through
/// <see cref="Aevatar.Foundation.Abstractions.IActorDispatchPort"/>, and
/// projection-version polling so callers (LLM tools, Studio admin endpoints,
/// etc.) stay thin parameter-mapping adapters.
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
