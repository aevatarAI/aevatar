using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Production implementation of <see cref="IUserAgentCatalogCommandPort"/>.
/// Routes catalog upsert / tombstone through <see cref="IActorDispatchPort"/>
/// (no direct <c>HandleEventAsync</c> on the actor instance).
///
/// Issue #466: this is an internal infrastructure port (not user-facing). It
/// dispatches by id; ownership semantics live on the public
/// <see cref="IUserAgentCatalogQueryPort"/> (caller-scoped) and are applied at
/// the LLM tool layer, not here.
///
/// Refactor (iter1/cluster-001):
///   Old pattern: catalog command plumbing was also available for execution updates.
///   New principle: command port dispatches only catalog-owned membership mutations.
///
/// Refactor (iter4/cluster-009):
///   Old pattern: Command dispatch polled projection documents to report observed state.
///   New principle: Command ACKs are accepted-only; observation belongs to explicit query/projection paths.
/// </summary>
internal sealed class UserAgentCatalogCommandPort : IUserAgentCatalogCommandPort
{
    private const string PublisherActorId = "scheduled.user-agent-catalog";

    private readonly UserAgentCatalogProjectionPort _projectionPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;

    public UserAgentCatalogCommandPort(
        UserAgentCatalogProjectionPort projectionPort,
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
    }

    public async Task<UserAgentCatalogUpsertResult> UpsertAsync(
        UserAgentCatalogUpsertCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.AgentId))
            throw new ArgumentException("AgentId is required for upsert.", nameof(command));

        await _projectionPort.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, ct);
        await EnsureCatalogActorAsync(ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, UserAgentCatalogGAgent.WellKnownId),
        };
        await _actorDispatchPort.DispatchAsync(UserAgentCatalogGAgent.WellKnownId, envelope, ct);

        return new UserAgentCatalogUpsertResult(CatalogCommandOutcome.Accepted);
    }

    public async Task<UserAgentCatalogTombstoneResult> TombstoneAsync(
        string agentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId is required.", nameof(agentId));

        await _projectionPort.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, ct);
        await EnsureCatalogActorAsync(ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new UserAgentCatalogTombstoneCommand { AgentId = agentId }),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, UserAgentCatalogGAgent.WellKnownId),
        };
        await _actorDispatchPort.DispatchAsync(UserAgentCatalogGAgent.WellKnownId, envelope, ct);

        return new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Accepted);
    }

    private async Task EnsureCatalogActorAsync(CancellationToken ct)
    {
        _ = await _actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId, ct);
    }
}
