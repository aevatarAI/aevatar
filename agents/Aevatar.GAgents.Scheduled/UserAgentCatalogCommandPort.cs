using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Production implementation of <see cref="IUserAgentCatalogCommandPort"/>.
/// Routes catalog upsert / tombstone through <see cref="IActorDispatchPort"/>
/// (no direct <c>HandleEventAsync</c> on the actor instance) and polls the
/// projection document store for the projected state version so callers can
/// return honest <see cref="CatalogCommandOutcome.Observed"/> when
/// materialization catches up within the wait budget, falling back to
/// <see cref="CatalogCommandOutcome.Accepted"/> otherwise.
///
/// Issue #466: this is an internal infrastructure port (not user-facing). It
/// reads the projection document directly by id; ownership semantics live on
/// the public <see cref="IUserAgentCatalogQueryPort"/> (caller-scoped) and are
/// applied at the LLM tool layer, not here.
/// </summary>
internal sealed class UserAgentCatalogCommandPort : IUserAgentCatalogCommandPort
{
    private const string PublisherActorId = "scheduled.user-agent-catalog";

    private readonly IProjectionDocumentReader<UserAgentCatalogDocument, string> _documentReader;
    private readonly UserAgentCatalogProjectionPort _projectionPort;
    private readonly IActorRuntime _actorRuntime;
    private readonly IActorDispatchPort _actorDispatchPort;
    private readonly int _projectionWaitAttempts;
    private readonly int _projectionWaitDelayMilliseconds;

    public UserAgentCatalogCommandPort(
        IProjectionDocumentReader<UserAgentCatalogDocument, string> documentReader,
        UserAgentCatalogProjectionPort projectionPort,
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort)
        : this(
            documentReader,
            projectionPort,
            actorRuntime,
            actorDispatchPort,
            ProjectionWaitDefaults.Attempts,
            ProjectionWaitDefaults.DelayMilliseconds)
    {
    }

    internal UserAgentCatalogCommandPort(
        IProjectionDocumentReader<UserAgentCatalogDocument, string> documentReader,
        UserAgentCatalogProjectionPort projectionPort,
        IActorRuntime actorRuntime,
        IActorDispatchPort actorDispatchPort,
        int projectionWaitAttempts,
        int projectionWaitDelayMilliseconds)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
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
        var versionBefore = (await _documentReader.GetAsync(command.AgentId, ct))?.StateVersion ?? -1;
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

            var after = await _documentReader.GetAsync(command.AgentId, ct);
            var versionAfter = after?.StateVersion ?? -1;
            if (versionAfter <= versionBefore)
                continue;

            if (after is null || after.Tombstoned)
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

        var existing = await _documentReader.GetAsync(agentId, ct);
        if (existing is null || existing.Tombstoned)
            return new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.NotFound);

        var versionBefore = existing.StateVersion;
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

            var after = await _documentReader.GetAsync(agentId, ct);
            if (after is null || after.Tombstoned)
                return new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Observed);

            if (after.StateVersion <= versionBefore)
                continue;
        }

        return new UserAgentCatalogTombstoneResult(CatalogCommandOutcome.Accepted);
    }

    private async Task EnsureCatalogActorAsync(CancellationToken ct)
    {
        _ = await _actorRuntime.GetAsync(UserAgentCatalogGAgent.WellKnownId)
            ?? await _actorRuntime.CreateAsync<UserAgentCatalogGAgent>(UserAgentCatalogGAgent.WellKnownId, ct);
    }

#pragma warning disable CS0612 // Platform field deprecated; comparison covers legacy data still on the wire
    private static bool Matches(UserAgentCatalogDocument doc, UserAgentCatalogUpsertCommand command) =>
        string.Equals(doc.Platform, command.Platform, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(doc.ConversationId, command.ConversationId, StringComparison.Ordinal) &&
        string.Equals(doc.NyxProviderSlug, command.NyxProviderSlug, StringComparison.Ordinal);
    // Note: NyxApiKey is no longer projected to UserAgentCatalogDocument (reserved
    // field 5); the credential lives in UserAgentCatalogNyxCredentialDocument and
    // is not part of the upsert observation contract here. Issue #466 §D.
#pragma warning restore CS0612
}
