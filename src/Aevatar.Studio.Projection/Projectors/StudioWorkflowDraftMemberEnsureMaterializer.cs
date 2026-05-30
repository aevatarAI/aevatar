using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Workspace;
using Google.Protobuf;

namespace Aevatar.Studio.Projection.Projectors;

/// <summary>
/// Ensures a workflow draft has a canonical StudioMember authority after the
/// workspace actor commits <see cref="StudioWorkflowDraftSaved"/>.
/// </summary>
// Refactor (iter1345/cluster-519-draft-member-authority):
//   Old pattern: draft save callers could stitch member creation together
//   outside the committed-state projection chain.
//   New principle: the committed StudioWorkflowDraftSaved fact is the single
//   trigger; this materializer fans out one typed EnsureStudioMember command
//   through the standard actor dispatch path.
internal sealed class StudioWorkflowDraftMemberEnsureMaterializer
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private readonly IStudioActorBootstrap _bootstrap;
    private readonly StudioProjectionActorCommandDispatch _commandDispatch;
    private readonly StudioWorkflowDraftMemberEnsureCommandFactory _commandFactory;

    public StudioWorkflowDraftMemberEnsureMaterializer(
        IStudioActorBootstrap bootstrap,
        StudioProjectionActorCommandDispatch commandDispatch,
        StudioWorkflowDraftMemberEnsureCommandFactory commandFactory)
    {
        _bootstrap = bootstrap ?? throw new ArgumentNullException(nameof(bootstrap));
        _commandDispatch = commandDispatch ?? throw new ArgumentNullException(nameof(commandDispatch));
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpack(envelope, out var published) ||
            published?.StateEvent?.EventData == null ||
            !published.StateEvent.EventData.Is(StudioWorkflowDraftSaved.Descriptor))
        {
            return;
        }

        var evt = published.StateEvent.EventData.Unpack<StudioWorkflowDraftSaved>();
        if (evt.Draft == null)
            return;

        var plan = _commandFactory.TryCreate(evt.ScopeId, evt.Draft.WorkflowId, evt.Draft.Name, evt.SavedAtUtc);
        if (plan == null)
            return;

        var actor = await _bootstrap.EnsureAsync<StudioMemberGAgent>(plan.ActorId, ct).ConfigureAwait(false);
        await _commandDispatch.DispatchAsync(
            actor,
            plan.Command,
            plan.PublisherId,
            commandId: plan.CommandId,
            deduplicationOperationId: plan.DeduplicationOperationId,
            ct: ct).ConfigureAwait(false);
    }
}
