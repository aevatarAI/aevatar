using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Device;

/// <summary>
/// Materializes <see cref="DeviceRegistrationState"/> into per-entry
/// <see cref="DeviceRegistrationDocument"/> documents for the query-side read model.
/// Tombstoned entries are retained in state and projected as <see cref="ProjectionVerdict.Tombstone"/>
/// so the dispatcher removes the stale document (Channel RFC §7.1.1).
/// </summary>
public sealed class DeviceRegistrationProjector
    : PerEntryDocumentProjector<
        DeviceRegistrationState,
        DeviceRegistrationEntry,
        DeviceRegistrationDocument,
        DeviceRegistrationMaterializationContext>
{
    public DeviceRegistrationProjector(
        IProjectionWriteDispatcher<DeviceRegistrationDocument> writeDispatcher,
        IProjectionClock clock)
        : base(writeDispatcher, clock)
    {
    }

    protected override IEnumerable<DeviceRegistrationEntry> ExtractEntries(DeviceRegistrationState state) =>
        state.Registrations;

    protected override string EntryKey(DeviceRegistrationEntry entry) => entry.Id ?? string.Empty;

    protected override ProjectionVerdict Evaluate(DeviceRegistrationEntry entry) =>
        entry.Tombstoned ? ProjectionVerdict.Tombstone : ProjectionVerdict.Project;

    protected override DeviceRegistrationDocument Materialize(
        DeviceRegistrationEntry entry,
        DeviceRegistrationMaterializationContext context,
        StateEvent stateEvent,
        DateTimeOffset updatedAt) =>
        new()
        {
            Id = entry.Id,
            ScopeId = entry.ScopeId ?? string.Empty,
            HmacKey = entry.HmacKey ?? string.Empty,
            NyxConversationId = entry.NyxConversationId ?? string.Empty,
            Description = entry.Description ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            ActorId = context.RootActorId,
            UpdatedAt = updatedAt,
        };
}
