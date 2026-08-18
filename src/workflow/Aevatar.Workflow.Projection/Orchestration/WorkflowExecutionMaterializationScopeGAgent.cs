using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Projection.Orchestration;

[GAgent(
    WorkflowExecutionMaterializationScopeGAgent.AgentKind,
    StateSchemaVersion = WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion)]
public sealed class WorkflowExecutionMaterializationScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<WorkflowExecutionMaterializationContext>
{
    public const string AgentKind =
        "projection.materialization-scope.workflow-execution-materialization-context";
    public const int SupportedStateSchemaVersion = 1;

    protected override bool EnablesDurableObservationRecovery =>
        WorkflowProjectionIncrementalGraphSchemaAdoption.IsGranted(
            Services.GetService<IRuntimeActorStateSchemaContextReader>());
}
