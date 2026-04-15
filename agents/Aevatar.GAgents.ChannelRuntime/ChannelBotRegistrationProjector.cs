using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Materializes <see cref="ChannelBotRegistrationStoreState"/> into per-entry
/// <see cref="ChannelBotRegistrationDocument"/> documents for query-side read model.
///
/// Known limitation: <see cref="IProjectionWriteDispatcher{T}"/> only supports
/// <c>UpsertAsync</c>. When a bot is unregistered, the state no longer contains
/// that entry, but the orphaned document is not deleted. A future
/// <c>IProjectionWriteDispatcher.DeleteAsync</c> is needed to close this gap.
/// </summary>
public sealed class ChannelBotRegistrationProjector
    : ICurrentStateProjectionMaterializer<ChannelBotRegistrationMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ChannelBotRegistrationDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ChannelBotRegistrationProjector(
        IProjectionWriteDispatcher<ChannelBotRegistrationDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ChannelBotRegistrationMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<ChannelBotRegistrationStoreState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);

        foreach (var entry in state.Registrations)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                continue;

            var document = new ChannelBotRegistrationDocument
            {
                Id = entry.Id,
                Platform = entry.Platform ?? string.Empty,
                NyxProviderSlug = entry.NyxProviderSlug ?? string.Empty,
                ScopeId = entry.ScopeId ?? string.Empty,
                VerificationToken = entry.VerificationToken ?? string.Empty,
                WebhookUrl = entry.WebhookUrl ?? string.Empty,
                NyxUserToken = entry.NyxUserToken ?? string.Empty,
                EncryptKey = entry.EncryptKey ?? string.Empty,
                StateVersion = stateEvent.Version,
                LastEventId = stateEvent.EventId ?? string.Empty,
                ActorId = context.RootActorId,
                UpdatedAt = updatedAt,
            };

            await _writeDispatcher.UpsertAsync(document, ct);
        }
    }
}
