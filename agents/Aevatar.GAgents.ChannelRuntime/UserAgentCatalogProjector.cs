using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class UserAgentCatalogProjector
    : PerEntryDocumentProjector<
        UserAgentCatalogState,
        UserAgentCatalogEntry,
        UserAgentCatalogDocument,
        UserAgentCatalogMaterializationContext>
{
    public UserAgentCatalogProjector(
        IProjectionWriteDispatcher<UserAgentCatalogDocument> writeDispatcher,
        IProjectionClock clock)
        : base(writeDispatcher, clock)
    {
    }

    protected override IEnumerable<UserAgentCatalogEntry> ExtractEntries(UserAgentCatalogState state) => state.Entries;

    protected override string EntryKey(UserAgentCatalogEntry entry) => entry.AgentId ?? string.Empty;

    protected override ProjectionVerdict Evaluate(UserAgentCatalogEntry entry) =>
        entry.Tombstoned ? ProjectionVerdict.Tombstone : ProjectionVerdict.Project;

    protected override UserAgentCatalogDocument Materialize(
        UserAgentCatalogEntry entry,
        UserAgentCatalogMaterializationContext context,
        StateEvent stateEvent,
        DateTimeOffset updatedAt) =>
        new()
        {
            Id = entry.AgentId,
            Platform = entry.Platform ?? string.Empty,
            ConversationId = entry.ConversationId ?? string.Empty,
            NyxProviderSlug = entry.NyxProviderSlug ?? string.Empty,
            OwnerNyxUserId = entry.OwnerNyxUserId ?? string.Empty,
            AgentType = entry.AgentType ?? string.Empty,
            TemplateName = entry.TemplateName ?? string.Empty,
            ScopeId = entry.ScopeId ?? string.Empty,
            ApiKeyId = entry.ApiKeyId ?? string.Empty,
            ScheduleCron = entry.ScheduleCron ?? string.Empty,
            ScheduleTimezone = entry.ScheduleTimezone ?? string.Empty,
            Status = entry.Status ?? string.Empty,
            LastRunAtUtc = entry.LastRunAt,
            NextRunAtUtc = entry.NextRunAt,
            ErrorCount = entry.ErrorCount,
            LastError = entry.LastError ?? string.Empty,
            Tombstoned = entry.Tombstoned,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            ActorId = context.RootActorId,
            UpdatedAt = updatedAt,
            CreatedAt = entry.CreatedAt != null ? entry.CreatedAt.ToDateTimeOffset() : updatedAt,
        };
}
