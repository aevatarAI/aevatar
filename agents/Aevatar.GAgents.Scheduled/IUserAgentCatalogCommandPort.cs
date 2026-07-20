using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Application-service surface for catalog mutations. Owns projection
/// priming, envelope construction, dispatch through
/// <see cref="Aevatar.Foundation.Abstractions.IActorDispatchPort"/> so callers
/// (LLM tools, Studio admin endpoints, etc.) stay thin parameter-mapping adapters.
/// Refactor (iter1/cluster-001):
///   Old pattern: catalog mutation plumbing also carried per-runner execution status.
///   New principle: command port mutations are membership-only; execution is projected from runner commits.
/// Refactor (iter4/cluster-009):
///   Old pattern: Command port outcomes encoded opportunistic readmodel observation.
///   New principle: Command outcomes are accepted-only; readmodel freshness is queried explicitly.
/// Refactor (iter5/cluster-012):
///   Old pattern: Accepted-only command ACKs were wrapped in result records with a single enum value.
///   New principle: Successful completion of the Task is the command-port ACK; user-facing accepted copy stays at tool boundaries.
/// </summary>
public interface IUserAgentCatalogCommandPort
{
    Task UpsertAsync(
        UserAgentCatalogUpsertCommand command,
        CancellationToken ct = default);

    Task TombstoneAsync(
        string agentId,
        CancellationToken ct = default,
        string bearerToken = "");

    Task RecordApiKeyRevocationAttemptAsync(
        UserAgentCatalogRecordApiKeyRevocationAttemptCommand command,
        CancellationToken ct = default);

    Task RequestCredentialRevocationAsync(
        ScheduledAgentCredentialRevocationIntent intent,
        CancellationToken ct = default,
        string bearerToken = "");

    Task RetryCredentialRevocationsAsync(
        OwnerScope ownerScope,
        string bearerToken,
        CancellationToken ct = default);

    Task ShareAsync(
        string agentId,
        OwnerScope ownerScope,
        bool allowTrigger,
        CancellationToken ct = default);

    Task UnshareAsync(
        string agentId,
        OwnerScope ownerScope,
        CancellationToken ct = default);
}
