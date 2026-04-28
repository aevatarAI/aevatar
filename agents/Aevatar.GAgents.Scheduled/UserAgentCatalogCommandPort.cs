using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Production implementation of <see cref="IUserAgentCatalogCommandPort"/>.
/// Routes catalog upsert / tombstone through <see cref="IActorDispatchPort"/>
/// (no direct <c>HandleEventAsync</c> on the actor instance) and polls the
/// runtime query port for the projected state version so callers can return
/// honest <see cref="CatalogCommandOutcome.Observed"/> when materialization
/// catches up within the wait budget, falling back to
/// <see cref="CatalogCommandOutcome.Accepted"/> otherwise.
/// </summary>
internal sealed class UserAgentCatalogCommandPort : IUserAgentCatalogCommandPort
{
    private const string PublisherActorId = "scheduled.user-agent-catalog";

    private readonly IUserAgentCatalogRuntimeQueryPort _runtimeQueryPort;
    private readonly UserAgentCatalogProjectionPort _projectionPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly int _projectionWaitAttempts;
    private readonly int _projectionWaitDelayMilliseconds;

    public UserAgentCatalogCommandPort(
        IUserAgentCatalogRuntimeQueryPort runtimeQueryPort,
        UserAgentCatalogProjectionPort projectionPort,
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort)
        : this(
            runtimeQueryPort,
            projectionPort,
            actorRuntime,
            actorDispatchPort,
            ProjectionWaitDefaults.Attempts,
            ProjectionWaitDefaults.DelayMilliseconds)
    {
    }

    internal UserAgentCatalogCommandPort(
        IUserAgentCatalogRuntimeQueryPort runtimeQueryPort,
        UserAgentCatalogProjectionPort projectionPort,
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        int projectionWaitAttempts,
        int projectionWaitDelayMilliseconds)
    {
        _runtimeQueryPort = runtimeQueryPort ?? throw new ArgumentNullException(nameof(runtimeQueryPort));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
        _actorDispatchPort = actorDispatchPort ?? throw new ArgumentNullException(nameof(actorDispatchPort));
        _projectionWaitAttempts = projectionWaitAttempts;
        _projectionWaitDelayMilliseconds = projectionWaitDelayMilliseconds;
    }

    public async Task<UserAgentCatalogUpsertResult> UpsertAsync(
        UserAgentCatalogUpsertCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.AgentId))
            throw new ArgumentException("AgentId is required for upsert.", nameof(command));

        await _projectionPort.EnsureProjectionForActorAsync(UserAgentCatalogGAgent.WellKnownId, ct);
        var versionBefore = await _runtimeQueryPort.GetStateVersionAsync(command.AgentId, ct) ?? -1;
        await EnsureCatalogActorAsync(ct);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, UserAgentCatalogGAgent.WellKnownId),
        };
        await _actorDispatchPort.DispatchAsync(UserAgentCatalogGAgent.WellKnownId, envelope, ct);

        for (var attempt = 0; attempt < _projectionWaitAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_projectionWaitDelayMilliseconds, ct);

            var versionAfter = await _runtimeQueryPort.GetStateVersionAsync(command.AgentId, ct) ?? -1;
            if (versionAfter <= versionBefore)
                continue;

            var after = await _runtimeQueryPort.GetAsync(command.AgentId, ct);
            if (after is null)
                continue;

            if (Matches(after, command))
                return new UserAgentCatalogUpsertResult(CatalogCommandOutcome.Observed);
        }

        return new UserAgentCatalogUpsertResult(CatalogCommandOutcome.Accepted);
    }

    public async Task<UserAgentCatalogTombstoneResult> TombstoneAsync(
        string agentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId is required.", nameof(agentId));

        var existing = await _runtimeQueryPort.GetAsync(agentId, ct);
        if (existing is null)
            return new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.NotFound);

        var versionBefore = await _runtimeQueryPort.GetStateVersionAsync(agentId, ct) ?? -1;
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

        for (var attempt = 0; attempt < _projectionWaitAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_projectionWaitDelayMilliseconds, ct);

            var versionAfter = await _runtimeQueryPort.GetStateVersionAsync(agentId, ct);
            if (versionAfter is null)
                return new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Observed);

            if (versionAfter.Value <= versionBefore)
                continue;

            if (await _runtimeQueryPort.GetAsync(agentId, ct) is null)
                return new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Observed);
        }

        return new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Accepted);
    }

    private async Task EnsureCatalogActorAsync(CancellationToken ct)
    {
        _ = await _actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId, ct);
    }

    private static bool Matches(UserAgentCatalogEntry entry, UserAgentCatalogUpsertCommand command) =>
        string.Equals(entry.Platform, command.Platform, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.ConversationId, command.ConversationId, StringComparison.Ordinal) &&
        string.Equals(entry.NyxProviderSlug, command.NyxProviderSlug, StringComparison.Ordinal) &&
        string.Equals(entry.NyxApiKey, command.NyxApiKey, StringComparison.Ordinal);
}
