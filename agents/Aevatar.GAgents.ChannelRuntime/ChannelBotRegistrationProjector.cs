using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Materializes <see cref="ChannelBotRegistrationStoreState"/> into per-entry
/// <see cref="ChannelBotRegistrationDocument"/> documents for the query-side read model.
/// Tombstoned entries are retained in state and projected as <see cref="ProjectionVerdict.Tombstone"/>
/// so the dispatcher removes the stale document (Channel RFC §7.1.1).
/// </summary>
public sealed class ChannelBotRegistrationProjector
    : PerEntryDocumentProjector<
        ChannelBotRegistrationStoreState,
        ChannelBotRegistrationEntry,
        ChannelBotRegistrationDocument,
        ChannelBotRegistrationMaterializationContext>
{
    public ChannelBotRegistrationProjector(
        IProjectionWriteDispatcher<ChannelBotRegistrationDocument> writeDispatcher,
        IProjectionClock clock)
        : base(writeDispatcher, clock)
    {
    }

    protected override IEnumerable<ChannelBotRegistrationEntry> ExtractEntries(
        ChannelBotRegistrationStoreState state) => state.Registrations;

    protected override string EntryKey(ChannelBotRegistrationEntry entry) => entry.Id ?? string.Empty;

    protected override ProjectionVerdict Evaluate(ChannelBotRegistrationEntry entry) =>
        entry.Tombstoned ? ProjectionVerdict.Tombstone : ProjectionVerdict.Project;

    protected override ChannelBotRegistrationDocument Materialize(
        ChannelBotRegistrationEntry entry,
        ChannelBotRegistrationMaterializationContext context,
        StateEvent stateEvent,
        DateTimeOffset updatedAt) =>
        new()
        {
            Id = entry.Id,
            Platform = entry.Platform ?? string.Empty,
            NyxProviderSlug = entry.NyxProviderSlug ?? string.Empty,
            ScopeId = entry.ScopeId ?? string.Empty,
            VerificationToken = entry.VerificationToken ?? string.Empty,
            WebhookUrl = entry.WebhookUrl ?? string.Empty,
            NyxUserToken = entry.NyxUserToken ?? string.Empty,
            NyxRefreshToken = entry.NyxRefreshToken ?? string.Empty,
            EncryptKey = entry.EncryptKey ?? string.Empty,
            CredentialRef = entry.CredentialRef ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            ActorId = context.RootActorId,
            UpdatedAt = updatedAt,
        };
}
