using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

/// <summary>
/// Materializes <see cref="UserConfigGAgentState"/> committed events into
/// <see cref="UserConfigCurrentStateDocument"/> in the projection document store.
///
/// Follows the <see cref="ICurrentStateProjectionMaterializer{TContext}"/> pattern
/// from the scripting module.
/// </summary>
public sealed class UserConfigCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<UserConfigCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public UserConfigCurrentStateProjector(
        IProjectionWriteDispatcher<UserConfigCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<UserConfigGAgentState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);

        var document = new UserConfigCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            DefaultModel = state.DefaultModel,
            PreferredLlmRoute = state.PreferredLlmRoute,
            RuntimeMode = state.RuntimeMode,
            LocalRuntimeBaseUrl = state.LocalRuntimeBaseUrl,
            RemoteRuntimeBaseUrl = state.RemoteRuntimeBaseUrl,
            MaxToolRounds = state.MaxToolRounds,
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
