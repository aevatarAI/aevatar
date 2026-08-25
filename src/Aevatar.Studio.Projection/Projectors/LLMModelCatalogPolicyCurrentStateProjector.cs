using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class LLMModelCatalogPolicyCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<LLMModelCatalogPolicyCurrentStateDocument>
        _writeDispatcher;
    private readonly IProjectionClock _clock;

    public LLMModelCatalogPolicyCurrentStateProjector(
        IProjectionWriteDispatcher<LLMModelCatalogPolicyCurrentStateDocument> writeDispatcher,
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

        if (!CommittedStateEventEnvelope.TryUnpackState<LLMModelCatalogPolicyGAgentState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var document = new LLMModelCatalogPolicyCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(
                CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)),
            OwnerType = state.OwnerType,
            ScopeId = state.ScopeId,
            Mode = state.Mode,
            LastMutationId = state.LastMutationId,
        };
        document.Sources.AddRange(state.Sources.Select(static source => source.Clone()));
        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
