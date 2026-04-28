using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Scheduled;

public sealed class UserAgentCatalogNyxCredentialProjector
    : PerEntryDocumentProjector<
        UserAgentCatalogState,
        UserAgentCatalogEntry,
        UserAgentCatalogNyxCredentialDocument,
        UserAgentCatalogMaterializationContext>
{
    public UserAgentCatalogNyxCredentialProjector(
        IProjectionWriteDispatcher<UserAgentCatalogNyxCredentialDocument> writeDispatcher,
        IProjectionClock clock)
        : base(writeDispatcher, clock)
    {
    }

    protected override IEnumerable<UserAgentCatalogEntry> ExtractEntries(UserAgentCatalogState state) => state.Entries;

    protected override string EntryKey(UserAgentCatalogEntry entry) => entry.AgentId ?? string.Empty;

    protected override ProjectionVerdict Evaluate(UserAgentCatalogEntry entry) =>
        entry.Tombstoned || string.IsNullOrWhiteSpace(entry.NyxApiKey)
            ? ProjectionVerdict.Tombstone
            : ProjectionVerdict.Project;

    protected override UserAgentCatalogNyxCredentialDocument Materialize(
        UserAgentCatalogEntry entry,
        UserAgentCatalogMaterializationContext context,
        StateEvent stateEvent,
        DateTimeOffset updatedAt) =>
        new()
        {
            Id = entry.AgentId,
            NyxApiKey = entry.NyxApiKey ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            ActorId = context.RootActorId,
            UpdatedAt = updatedAt,
        };
}
