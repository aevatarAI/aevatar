using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

// Refactor (iter1/cluster-001):
//   Old pattern: Catalog execution fields were changed by commands sent from each runner to one catalog actor.
//   New principle: Catalog documents merge catalog-owned membership state with runner-owned committed execution state.
public sealed class UserAgentCatalogProjector
    : ICurrentStateProjectionMaterializer<UserAgentCatalogMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<UserAgentCatalogDocument> _writeDispatcher;
    private readonly IProjectionDocumentReader<UserAgentCatalogDocument, string> _documentReader;
    private readonly IProjectionClock _clock;

    public UserAgentCatalogProjector(
        IProjectionWriteDispatcher<UserAgentCatalogDocument> writeDispatcher,
        IProjectionDocumentReader<UserAgentCatalogDocument, string> documentReader,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        UserAgentCatalogMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (CommittedStateEventEnvelope.TryUnpackState<UserAgentCatalogState>(
                envelope,
                out _,
                out var catalogStateEvent,
                out var catalogState) &&
            catalogStateEvent is not null &&
            catalogState is not null)
        {
            await ProjectCatalogMembershipAsync(catalogState, catalogStateEvent, envelope, ct);
            return;
        }

        if (CommittedStateEventEnvelope.TryUnpackState<SkillRunnerState>(
                envelope,
                out _,
                out var runnerStateEvent,
                out var runnerState) &&
            runnerStateEvent is not null &&
            runnerState is not null)
        {
            await ProjectRunnerExecutionAsync(context, runnerState, runnerStateEvent, envelope, ct);
        }
    }

    private async Task ProjectCatalogMembershipAsync(
        UserAgentCatalogState state,
        StateEvent stateEvent,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        foreach (var entry in state.Entries)
        {
            if (entry == null)
                continue;

            var key = entry.AgentId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (entry.Tombstoned)
            {
                await _writeDispatcher.DeleteAsync(key, ct);
                continue;
            }

            var existing = await _documentReader.GetAsync(key, ct);
            // Fix (pr678-review): per-source monotonic guard. The document merges two
            //   authoritative sources (catalog + runner), so it cannot carry one
            //   authoritative version. Skip a catalog commit already materialized here —
            //   without this, a stale/replayed catalog event would roll CatalogSourceVersion
            //   back and re-write older membership fields over newer ones.
            if (existing is not null && stateEvent.Version <= existing.CatalogSourceVersion)
                continue;
            var document = MaterializeCatalogEntry(entry, stateEvent, updatedAt, existing);
            if (!string.Equals(document.Id, key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Projected document id '{document.Id}' does not match entry key '{key}'.");
            }

            await _writeDispatcher.UpsertAsync(document, ct);
        }
    }

    private async Task ProjectRunnerExecutionAsync(
        UserAgentCatalogMaterializationContext context,
        SkillRunnerState state,
        StateEvent stateEvent,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!TryResolveRunnerActorId(context, envelope, out var agentId))
            return;

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var existing = await _documentReader.GetAsync(agentId, ct);
        // Fix (pr678-review): per-source monotonic guard — skip a runner commit already
        //   materialized into this document (see ProjectCatalogMembershipAsync).
        if (existing is not null && stateEvent.Version <= existing.RunnerSourceVersion)
            return;
        await _writeDispatcher.UpsertAsync(
            MaterializeRunnerExecution(agentId, state, stateEvent, updatedAt, existing),
            ct);
    }

    private static UserAgentCatalogDocument MaterializeCatalogEntry(
        UserAgentCatalogEntry entry,
        StateEvent stateEvent,
        DateTimeOffset updatedAt,
        UserAgentCatalogDocument? existing)
    {
        var document = existing?.Clone() ?? new UserAgentCatalogDocument();
        document.Id = entry.AgentId;
#pragma warning disable CS0612 // legacy fields kept for backward read compat (issue #466 migration)
        document.Platform = entry.Platform ?? string.Empty;
#pragma warning restore CS0612
        document.ConversationId = entry.ConversationId ?? string.Empty;
        document.NyxProviderSlug = entry.NyxProviderSlug ?? string.Empty;
#pragma warning disable CS0612
        document.OwnerNyxUserId = entry.OwnerNyxUserId ?? string.Empty;
#pragma warning restore CS0612
        document.AgentType = entry.AgentType ?? string.Empty;
        document.TemplateName = entry.TemplateName ?? string.Empty;
        document.ScopeId = entry.ScopeId ?? string.Empty;
        document.ApiKeyId = entry.ApiKeyId ?? string.Empty;
        document.ScheduleCron = entry.ScheduleCron ?? string.Empty;
        document.ScheduleTimezone = entry.ScheduleTimezone ?? string.Empty;
        document.Tombstoned = entry.Tombstoned;
        document.ActorId = UserAgentCatalogGAgent.WellKnownId;
        document.UpdatedAt = updatedAt;
        document.CreatedAt = entry.CreatedAt != null ? entry.CreatedAt.ToDateTimeOffset() : updatedAt;
        document.LarkReceiveId = entry.LarkReceiveId ?? string.Empty;
        document.LarkReceiveIdType = entry.LarkReceiveIdType ?? string.Empty;
        document.LarkReceiveIdFallback = entry.LarkReceiveIdFallback ?? string.Empty;
        document.LarkReceiveIdTypeFallback = entry.LarkReceiveIdTypeFallback ?? string.Empty;

        // Project owner_scope verbatim from the upserted entry. Per issue #466 the entry
        // is the authoritative source for ownership; the projector materializes it for
        // the caller-scoped readmodel filter rather than recomputing or inferring it.
#pragma warning disable CS0612
        var entryScope = entry.OwnerScope ?? OwnerScope.FromLegacyFields(entry.OwnerNyxUserId, entry.Platform);
#pragma warning restore CS0612
        if (entryScope is not null)
            document.OwnerScope = entryScope;

        document.CatalogSourceVersion = stateEvent.Version;
        document.CatalogLastEventId = stateEvent.EventId ?? string.Empty;
        document.StateVersion = ResolveMergedStateVersion(document);
        document.LastEventId = ResolveMergedLastEventId(document);
        return document;
    }

    private static UserAgentCatalogDocument MaterializeRunnerExecution(
        string agentId,
        SkillRunnerState state,
        StateEvent stateEvent,
        DateTimeOffset updatedAt,
        UserAgentCatalogDocument? existing)
    {
        var document = existing?.Clone() ?? new UserAgentCatalogDocument
        {
            Id = agentId,
            ActorId = UserAgentCatalogGAgent.WellKnownId,
            CreatedAt = updatedAt,
        };

        document.Id = agentId;
        document.ActorId = UserAgentCatalogGAgent.WellKnownId;
        document.AgentType = string.IsNullOrWhiteSpace(document.AgentType)
            ? SkillRunnerDefaults.AgentType
            : document.AgentType;
        document.TemplateName = CoalesceNonEmpty(document.TemplateName, state.TemplateName);
        document.ScopeId = CoalesceNonEmpty(document.ScopeId, state.ScopeId);
        document.ScheduleCron = CoalesceNonEmpty(document.ScheduleCron, state.ScheduleCron);
        document.ScheduleTimezone = CoalesceNonEmpty(document.ScheduleTimezone, state.ScheduleTimezone);
        document.Status = ResolveRunnerStatus(state);
        document.LastRunAtUtc = state.LastRunAt;
        document.NextRunAtUtc = state.NextRunAt;
        document.ErrorCount = state.ErrorCount;
        document.LastError = state.LastError ?? string.Empty;
        document.UpdatedAt = updatedAt;
        if (document.CreatedAt == default)
            document.CreatedAt = updatedAt;
        document.RunnerSourceVersion = stateEvent.Version;
        document.RunnerLastEventId = stateEvent.EventId ?? string.Empty;
        document.StateVersion = ResolveMergedStateVersion(document);
        document.LastEventId = ResolveMergedLastEventId(document);
        return document;
    }

    private static bool TryResolveRunnerActorId(
        UserAgentCatalogMaterializationContext context,
        EventEnvelope envelope,
        out string agentId)
    {
        agentId = string.Empty;
        var rootActorId = context.RootActorId?.Trim();
        if (!string.IsNullOrWhiteSpace(rootActorId) &&
            !string.Equals(rootActorId, UserAgentCatalogGAgent.WellKnownId, StringComparison.Ordinal))
        {
            agentId = rootActorId;
            return true;
        }

        var publisherActorId = envelope.Route?.PublisherActorId?.Trim();
        if (string.IsNullOrWhiteSpace(publisherActorId) ||
            string.Equals(publisherActorId, UserAgentCatalogGAgent.WellKnownId, StringComparison.Ordinal))
        {
            return false;
        }

        agentId = publisherActorId;
        return true;
    }

    private static string ResolveRunnerStatus(SkillRunnerState state)
    {
        if (!state.Enabled)
            return SkillRunnerDefaults.StatusDisabled;

        return state.ErrorCount > 0
            ? SkillRunnerDefaults.StatusError
            : SkillRunnerDefaults.StatusRunning;
    }

    // Fix (pr678-review): StateVersion merges two authoritative source versions. With the
    //   per-source monotonic guards above, every accepted event strictly advances exactly
    //   one source, so the sum is strictly monotonic per document and is a valid overwrite
    //   watermark. CatalogSourceVersion / RunnerSourceVersion remain the honest per-source
    //   versions; a fully vector-typed StateVersion would need a proto schema change.
    private static long ResolveMergedStateVersion(UserAgentCatalogDocument document) =>
        Math.Max(0, document.CatalogSourceVersion) + Math.Max(0, document.RunnerSourceVersion);

    private static string ResolveMergedLastEventId(UserAgentCatalogDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.CatalogLastEventId))
            return document.RunnerLastEventId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(document.RunnerLastEventId))
            return document.CatalogLastEventId ?? string.Empty;

        return $"{document.CatalogLastEventId}:{document.RunnerLastEventId}";
    }

    private static string CoalesceNonEmpty(string? existing, string? incoming) =>
        !string.IsNullOrWhiteSpace(existing)
            ? existing.Trim()
            : (incoming ?? string.Empty).Trim();
}
