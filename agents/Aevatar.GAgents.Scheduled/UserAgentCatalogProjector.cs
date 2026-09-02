using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

// Catalog projection only materializes UserAgentCatalog authority membership.
public sealed class UserAgentCatalogProjector
    : ICurrentStateProjectionMaterializer<UserAgentCatalogMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<UserAgentCatalogDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public UserAgentCatalogProjector(
        IProjectionWriteDispatcher<UserAgentCatalogDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
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

            var document = MaterializeCatalogEntry(entry, stateEvent, updatedAt);
            if (!string.Equals(document.Id, key, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Projected document id '{document.Id}' does not match entry key '{key}'.");
            }

            await _writeDispatcher.UpsertAsync(document, ct);
        }
    }

    private static UserAgentCatalogDocument MaterializeCatalogEntry(
        UserAgentCatalogEntry entry,
        StateEvent stateEvent,
        DateTimeOffset updatedAt)
    {
        var document = new UserAgentCatalogDocument();
        document.Id = entry.AgentId;
        document.ConversationId = entry.ConversationId ?? string.Empty;
        document.NyxProviderSlug = entry.NyxProviderSlug ?? string.Empty;
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
        document.OutputFormat = entry.OutputFormat;
        document.SharingGrant = entry.SharingGrant?.Clone();
        document.TargetPlatform = entry.TargetPlatform ?? string.Empty;
#pragma warning disable CS0612 // deprecated entry fields are read only as a channel_address compatibility bridge
        document.ChannelAddress = UserAgentCatalogChannelAddress.ToProto(
            entry.ChannelAddress,
            entry.TargetPlatform,
            entry.NyxProviderSlug,
            entry.ConversationId,
            entry.LarkReceiveId,
            entry.LarkReceiveIdType,
            entry.LarkReceiveIdFallback,
            entry.LarkReceiveIdTypeFallback);
#pragma warning restore CS0612

        // Project owner_scope verbatim from the upserted entry. Per issue #466 the entry
        // is the authoritative source for ownership; the projector materializes it for
        // the caller-scoped readmodel filter rather than recomputing or inferring it.
#pragma warning disable CS0612
        // Refactor (iter92/cluster-092):
        //   Old: write path simultaneously emitted deprecated `Platform`/`OwnerNyxUserId`.
        //   New: write path emits only `OwnerScope`; legacy fields are retained only in
        //   the no-`OwnerScope` fallback branch for backwards compatibility.
        var entryScope = entry.OwnerScope ?? OwnerScope.FromLegacyFields(entry.OwnerNyxUserId, entry.Platform);
        if (entry.OwnerScope is null)
        {
            document.Platform = entry.Platform ?? string.Empty;
            document.OwnerNyxUserId = entry.OwnerNyxUserId ?? string.Empty;
        }
        else
        {
            document.Platform = string.Empty;
            document.OwnerNyxUserId = string.Empty;
        }
#pragma warning restore CS0612
        if (entryScope is not null)
            document.OwnerScope = entryScope;

        if (entry.SharingGrant is not null &&
            UserAgentCatalogSharingAudience.TryBuildKey(entryScope, out var audienceKey))
        {
            document.VisibleSharingAudienceKey = audienceKey;
            if (entry.SharingGrant.AllowTrigger)
                document.TriggerSharingAudienceKey = audienceKey;
        }

        document.StateVersion = stateEvent.Version;
        document.LastEventId = stateEvent.EventId ?? string.Empty;
        return document;
    }
}
