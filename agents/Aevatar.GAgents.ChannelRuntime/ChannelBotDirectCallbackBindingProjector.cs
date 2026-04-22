using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Materializes direct platform-callback secret bindings into a runtime-only
/// document. Lark registrations on the Nyx relay path leave this empty.
/// </summary>
public sealed class ChannelBotDirectCallbackBindingProjector
    : PerEntryDocumentProjector<
        ChannelBotRegistrationStoreState,
        ChannelBotRegistrationEntry,
        ChannelBotDirectCallbackBindingDocument,
        ChannelBotRegistrationMaterializationContext>
{
    public ChannelBotDirectCallbackBindingProjector(
        IProjectionWriteDispatcher<ChannelBotDirectCallbackBindingDocument> writeDispatcher,
        IProjectionClock clock)
        : base(writeDispatcher, clock)
    {
    }

    protected override IEnumerable<ChannelBotRegistrationEntry> ExtractEntries(
        ChannelBotRegistrationStoreState state) => state.Registrations;

    protected override string EntryKey(ChannelBotRegistrationEntry entry) => entry.Id ?? string.Empty;

    protected override ProjectionVerdict Evaluate(ChannelBotRegistrationEntry entry) =>
        entry.Tombstoned ||
        string.Equals(entry.Platform, "lark", StringComparison.OrdinalIgnoreCase) ||
        entry.ResolveDirectCallbackBinding() is null
            ? ProjectionVerdict.Tombstone
            : ProjectionVerdict.Project;

    protected override ChannelBotDirectCallbackBindingDocument Materialize(
        ChannelBotRegistrationEntry entry,
        ChannelBotRegistrationMaterializationContext context,
        StateEvent stateEvent,
        DateTimeOffset updatedAt)
    {
        var binding = entry.ResolveDirectCallbackBinding() ?? new ChannelBotDirectCallbackBinding();
        return new ChannelBotDirectCallbackBindingDocument
        {
            Id = entry.Id,
            NyxUserToken = binding.NyxUserToken ?? string.Empty,
            NyxRefreshToken = binding.NyxRefreshToken ?? string.Empty,
            VerificationToken = binding.VerificationToken ?? string.Empty,
            CredentialRef = binding.CredentialRef ?? string.Empty,
            EncryptKey = binding.EncryptKey ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            ActorId = context.RootActorId,
            UpdatedAt = updatedAt,
        };
    }
}
