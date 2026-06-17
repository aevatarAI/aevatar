using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Workspace;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

// Refactor (iter16/cluster-meta-studio-actor-substrate):
//   Old: workspace current state was assembled from a local file-backed store outside the unified projection path.
//   New principle: this projector consumes committed workspace actor state and materializes the single query replica.
public sealed class StudioWorkspaceCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private static readonly JsonFormatter StateRootFormatter = new(
        JsonFormatter.Settings.Default
            .WithPreserveProtoFieldNames(true)
            .WithFormatDefaultValues(true));

    private readonly IProjectionWriteDispatcher<StudioWorkspaceCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public StudioWorkspaceCurrentStateProjector(
        IProjectionWriteDispatcher<StudioWorkspaceCurrentStateDocument> writeDispatcher,
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

        if (!CommittedStateEventEnvelope.TryUnpackState<StudioWorkspaceState>(
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
        var document = new StudioWorkspaceCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            StateRootJson = StateRootFormatter.Format(state),
        };
        document.DraftSummaries.AddRange(state.Drafts.Values.Select(ToDraftSummary));

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private static StudioWorkspaceDraftSummary ToDraftSummary(StudioWorkflowDraft draft) => new()
    {
        WorkflowId = draft.WorkflowId,
        Name = draft.Name,
        FileName = draft.FileName,
        DirectoryId = draft.DirectoryId,
        Version = draft.Version,
    };
}
