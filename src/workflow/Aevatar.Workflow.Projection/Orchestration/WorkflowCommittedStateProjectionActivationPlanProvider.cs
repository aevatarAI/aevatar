using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Maps workflow committed state events to existing durable projection scopes.
/// </summary>
// Refactor (iter46/issue-871-workflow-file-catalog-query-port):
//   Old pattern: Workflow catalog/capabilities query port discovered files, parsed YAML, loaded connector config, and cached results in singleton process memory during query execution.
//   New principle: WorkflowGAgent per-definition authority; query ports only read freshness-bearing readmodels; file discovery/parsing happens at startup/import time, not in query path.
// Refactor (iter18/cluster-006):
//   Old pattern: command-path projection activation facade with new actor/lifecycle phase
//   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
public sealed class WorkflowCommittedStateProjectionActivationPlanProvider : IProjectionActivationPlanProvider
{
    // Refactor (iter18/cluster-006):
    //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
    //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Published.StateEvent?.EventData == null)
            yield break;

        if (context.ActorType == typeof(WorkflowGAgent) &&
            context.Published.StateEvent.EventData.Is(BindWorkflowDefinitionEvent.Descriptor))
        {
            yield return DurablePlan<WorkflowBindingRuntimeLease>(
                context.ActorId,
                WorkflowProjectionKinds.Binding);
            yield break;
        }

        if (context.ActorType != typeof(WorkflowRunGAgent))
            yield break;

        if (context.Published.StateEvent.EventData.Is(BindWorkflowRunDefinitionEvent.Descriptor))
        {
            yield return DurablePlan<WorkflowBindingRuntimeLease>(
                context.ActorId,
                WorkflowProjectionKinds.Binding);
        }

        yield return DurablePlan<WorkflowExecutionMaterializationRuntimeLease>(
            context.ActorId,
            WorkflowProjectionKinds.ExecutionMaterialization);
    }

    private static ProjectionActivationPlan DurablePlan<TLease>(
        string actorId,
        string projectionKind)
        where TLease : class, IProjectionRuntimeLease =>
        new()
        {
            LeaseType = typeof(TLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
}
