using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Core;

namespace Aevatar.Studio.Projection.Orchestration;

/// <summary>
/// Activates the Studio-owned workflow board materializer from WorkflowRunGAgent committed facts.
/// </summary>
public sealed class StudioWorkflowBoardProjectionActivationPlanProvider : IProjectionActivationPlanProvider
{
    public const string ProjectionKind = "studio.workflow-board";

    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ActorType != typeof(WorkflowRunGAgent) ||
            context.Published.StateEvent?.EventData == null)
        {
            yield break;
        }

        yield return new ProjectionActivationPlan
        {
            LeaseType = typeof(StudioWorkflowBoardMaterializationRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = context.ActorId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
    }
}
