using Aevatar.Workflow.Core.Modules;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowCoreModulePack : IWorkflowModulePack
{
    private static readonly IReadOnlyList<WorkflowModuleRegistration> ModuleRegistrations =
    [
        WorkflowModuleRegistration.Create<ConditionalModule>("conditional"),
        WorkflowModuleRegistration.Create<SwitchModule>("switch"),
        WorkflowModuleRegistration.Create<CheckpointModule>("checkpoint"),
        WorkflowModuleRegistration.Create<AssignModule>("assign"),
        WorkflowModuleRegistration.Create<VoteConsensusModule>("vote_consensus", "vote"),
        WorkflowModuleRegistration.Create<ToolCallModule>("tool_call"),
        WorkflowModuleRegistration.Create<ConnectorCallModule>("connector_call", "bridge_call"),
        WorkflowModuleRegistration.Create<TransformModule>("transform"),
        WorkflowModuleRegistration.Create<RetrieveFactsModule>("retrieve_facts"),
        WorkflowModuleRegistration.Create<GuardModule>("guard", "assert"),
        WorkflowModuleRegistration.Create<EmitModule>("emit", "publish"),
        WorkflowModuleRegistration.Create<WorkflowYamlValidateModule>("workflow_yaml_validate"),
        WorkflowModuleRegistration.Create<DynamicWorkflowModule>("dynamic_workflow"),
    ];

    public string Name => "workflow.core";

    public IReadOnlyList<WorkflowModuleRegistration> Modules => ModuleRegistrations;
}
