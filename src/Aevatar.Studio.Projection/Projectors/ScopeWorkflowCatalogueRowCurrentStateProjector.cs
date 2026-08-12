using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class ScopeWorkflowCatalogueRowCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ScopeWorkflowCatalogueRowDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ScopeWorkflowCatalogueRowCurrentStateProjector(
        IProjectionWriteDispatcher<ScopeWorkflowCatalogueRowDocument> writeDispatcher,
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

        if (!CommittedStateEventEnvelope.TryUnpackState<ScopeWorkflowCatalogueRowState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null ||
            string.IsNullOrWhiteSpace(state.ScopeId) ||
            string.IsNullOrWhiteSpace(state.WorkflowId))
        {
            return;
        }

        if (state.DraftSource == null && state.ServiceSource == null)
        {
            await _writeDispatcher.DeleteAsync(
                ScopeWorkflowCatalogueRowMaterializer.ToDeleteMarker(
                    context.RootActorId,
                    stateEvent.Version,
                    state),
                ct);
            return;
        }

        var document = ScopeWorkflowCatalogueRowMaterializer.ToRowDocument(
            context.RootActorId,
            stateEvent.Version,
            state);
        if (document.UpdatedAt == null)
            document.UpdatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(_clock.UtcNow);

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
