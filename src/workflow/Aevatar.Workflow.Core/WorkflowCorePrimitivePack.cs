using Aevatar.Workflow.Core.PrimitiveExecutors;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowCorePrimitivePack : IWorkflowPrimitivePack
{
    private static readonly IReadOnlyList<WorkflowPrimitiveRegistration> ExecutorRegistrations =
    [
        WorkflowPrimitiveRegistration.Create<ConditionalPrimitiveExecutor>("conditional"),
        WorkflowPrimitiveRegistration.Create<SwitchPrimitiveExecutor>("switch"),
        WorkflowPrimitiveRegistration.Create<CheckpointPrimitiveExecutor>("checkpoint"),
        WorkflowPrimitiveRegistration.Create<AssignPrimitiveExecutor>("assign"),
        WorkflowPrimitiveRegistration.Create<VoteConsensusPrimitiveExecutor>("vote_consensus", "vote"),
        WorkflowPrimitiveRegistration.Create<ToolCallPrimitiveExecutor>("tool_call"),
        WorkflowPrimitiveRegistration.Create<ConnectorCallPrimitiveExecutor>("connector_call", "bridge_call"),
        WorkflowPrimitiveRegistration.Create<TransformPrimitiveExecutor>("transform"),
        WorkflowPrimitiveRegistration.Create<RetrieveFactsPrimitiveExecutor>("retrieve_facts"),
        WorkflowPrimitiveRegistration.Create<GuardPrimitiveExecutor>("guard", "assert"),
        WorkflowPrimitiveRegistration.Create<EmitPrimitiveExecutor>("emit", "publish"),
        WorkflowPrimitiveRegistration.Create<WorkflowYamlValidatePrimitiveExecutor>("workflow_yaml_validate"),
        WorkflowPrimitiveRegistration.Create<DynamicWorkflowPrimitiveExecutor>("dynamic_workflow"),
    ];

    public string Name => "workflow.core";

    public IReadOnlyList<WorkflowPrimitiveRegistration> Executors => ExecutorRegistrations;
}
