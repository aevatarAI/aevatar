using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Production implementation of <see cref="IUserAgentCatalogCommandPort"/>.
/// Routes catalog upsert / tombstone through <see cref="IActorDispatchPort"/>.
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
///
/// Refactor (iter23/cluster-002):
///   Old pattern: Command ports synchronously activate projection scopes before dispatch and sometimes turn projection lease failure into command admission failure.
///   New principle: Command ports dispatch accepted commands; projection activation is owned by committed-state hooks, explicit observation binders, startup activators, or background materializers.
///
/// Refactor (iter149/issue1132): Old pattern: catalog mutations used handled-dispatch as a stronger synchronous ACK.  New principle: catalog command port uses accepted-only dispatch; catalog read model observes committed state later.
/// </summary>
internal sealed class UserAgentCatalogCommandPort : IUserAgentCatalogCommandPort
{
    private const string PublisherActorId = "scheduled.user-agent-catalog";

    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;

    public UserAgentCatalogCommandPort(
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
    }

    // Refactor (iter23/cluster-002):
    //   Old pattern: Command ports synchronously activate projection scopes before dispatch and sometimes turn projection lease failure into command admission failure.
    //   New principle: Command ports dispatch accepted commands; projection activation is owned by committed-state hooks, explicit observation binders, startup activators, or background materializers.
    public async Task UpsertAsync(
        UserAgentCatalogUpsertCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.AgentId))
            throw new ArgumentException("AgentId is required for upsert.", nameof(command));

        await EnsureCatalogActorAsync(ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, UserAgentCatalogGAgent.WellKnownId),
        };
        await _actorDispatchPort.DispatchAsync(UserAgentCatalogGAgent.WellKnownId, envelope, ct);
    }

    // Refactor (iter23/cluster-002):
    //   Old pattern: Command ports synchronously activate projection scopes before dispatch and sometimes turn projection lease failure into command admission failure.
    //   New principle: Command ports dispatch accepted commands; projection activation is owned by committed-state hooks, explicit observation binders, startup activators, or background materializers.
    public async Task TombstoneAsync(
        string agentId,
        CancellationToken ct = default,
        string bearerToken = "")
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId is required.", nameof(agentId));

        await EnsureCatalogActorAsync(ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new UserAgentCatalogTombstoneCommand
            {
                AgentId = agentId,
                BearerToken = bearerToken?.Trim() ?? string.Empty,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, UserAgentCatalogGAgent.WellKnownId),
        };
        await _actorDispatchPort.DispatchAsync(UserAgentCatalogGAgent.WellKnownId, envelope, ct);
    }

    public async Task RecordApiKeyRevocationAttemptAsync(
        UserAgentCatalogRecordApiKeyRevocationAttemptCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.AgentId))
            throw new ArgumentException("AgentId is required for API key revocation attempt.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.ApiKeyId))
            throw new ArgumentException("ApiKeyId is required for API key revocation attempt.", nameof(command));

        await EnsureCatalogActorAsync(ct);

        var envelope = BuildEnvelope(command);
        await _actorDispatchPort.DispatchAsync(UserAgentCatalogGAgent.WellKnownId, envelope, ct);
    }

    public async Task RequestCredentialRevocationAsync(
        ScheduledAgentCredentialRevocationIntent intent,
        CancellationToken ct = default,
        string bearerToken = "")
    {
        ArgumentNullException.ThrowIfNull(intent);
        await EnsureCatalogActorAsync(ct);
        await _actorDispatchPort.DispatchAsync(
            UserAgentCatalogGAgent.WellKnownId,
            BuildEnvelope(new UserAgentCatalogRequestCredentialRevocationCommand
            {
                Intent = intent.Clone(),
                BearerToken = bearerToken?.Trim() ?? string.Empty,
            }),
            ct);
    }

    public async Task RetryCredentialRevocationsAsync(
        OwnerScope ownerScope,
        string bearerToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownerScope);
        await EnsureCatalogActorAsync(ct);
        await _actorDispatchPort.DispatchAsync(
            UserAgentCatalogGAgent.WellKnownId,
            BuildEnvelope(new UserAgentCatalogRetryCredentialRevocationsCommand
            {
                OwnerScope = ownerScope.Clone(),
                BearerToken = bearerToken?.Trim() ?? string.Empty,
            }),
            ct);
    }

    public async Task ShareAsync(
        string agentId,
        OwnerScope ownerScope,
        bool allowTrigger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId is required.", nameof(agentId));
        ArgumentNullException.ThrowIfNull(ownerScope);

        await EnsureCatalogActorAsync(ct);

        var envelope = BuildEnvelope(new UserAgentCatalogShareCommand
        {
            AgentId = agentId,
            OwnerScope = ownerScope.Clone(),
            AllowTrigger = allowTrigger,
        });
        await _actorDispatchPort.DispatchAsync(UserAgentCatalogGAgent.WellKnownId, envelope, ct);
    }

    public async Task UnshareAsync(
        string agentId,
        OwnerScope ownerScope,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId is required.", nameof(agentId));
        ArgumentNullException.ThrowIfNull(ownerScope);

        await EnsureCatalogActorAsync(ct);

        var envelope = BuildEnvelope(new UserAgentCatalogUnshareCommand
        {
            AgentId = agentId,
            OwnerScope = ownerScope.Clone(),
        });
        await _actorDispatchPort.DispatchAsync(UserAgentCatalogGAgent.WellKnownId, envelope, ct);
    }

    private async Task EnsureCatalogActorAsync(CancellationToken ct)
    {
        _ = await _actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId, ct);
    }

    private static EventEnvelope BuildEnvelope(IMessage command) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, UserAgentCatalogGAgent.WellKnownId),
        };
}
