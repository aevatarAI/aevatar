using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Modules;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowCoreModulePack : IWorkflowModulePack
{
    private static readonly IReadOnlyList<WorkflowModuleRegistration> ModuleRegistrations =
    [
        WorkflowModuleRegistration.Create<ConditionalModule>("conditional"),
        WorkflowModuleRegistration.Create<SwitchModule>("switch"),
        WorkflowModuleRegistration.Create<WhileModule>("while", "loop"),
        WorkflowModuleRegistration.Create<WorkflowCallModule>("workflow_call", "sub_workflow"),
        WorkflowModuleRegistration.Create<CheckpointModule>("checkpoint"),
        WorkflowModuleRegistration.Create<AssignModule>("assign"),
        WorkflowModuleRegistration.Create<ParallelFanOutModule>("parallel_fanout", "parallel", "fan_out"),
        WorkflowModuleRegistration.Create<VoteConsensusModule>("vote_consensus", "vote"),
        WorkflowModuleRegistration.Create<ForEachModule>("foreach", "for_each"),
        WorkflowModuleRegistration.Create<RaceModule>("race", "select"),
        WorkflowModuleRegistration.Create<MapReduceModule>("map_reduce", "mapreduce"),
        WorkflowModuleRegistration.Create<LLMCallModule>("llm_call"),
        WorkflowModuleRegistration.Create<ToolCallModule>("tool_call"),
        WorkflowModuleRegistration.Create<ConnectorCallModule>("connector_call", "bridge_call"),
        WorkflowModuleRegistration.Create<TransformModule>("transform"),
        WorkflowModuleRegistration.Create<RetrieveFactsModule>("retrieve_facts"),
        WorkflowModuleRegistration.Create<WaitSignalModule>("wait_signal", "wait"),
        WorkflowModuleRegistration.Create<GuardModule>("guard", "assert"),
        WorkflowModuleRegistration.Create<EvaluateModule>("evaluate", "judge"),
        WorkflowModuleRegistration.Create<ReflectModule>("reflect"),
        WorkflowModuleRegistration.Create<DelayModule>("delay", "sleep"),
        WorkflowModuleRegistration.Create<EmitModule>("emit", "publish"),
        WorkflowModuleRegistration.Create<ActorSendModule>("actor_send"),
        WorkflowModuleRegistration.Create<CacheModule>("cache"),
        WorkflowModuleRegistration.Create<HumanApprovalModule>("human_approval"),
        WorkflowModuleRegistration.Create<HumanInputModule>("human_input"),
        WorkflowModuleRegistration.Create<WorkflowYamlValidateModule>("workflow_yaml_validate"),
        WorkflowModuleRegistration.Create<DynamicWorkflowModule>("dynamic_workflow"),
    ];

    private static readonly IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanderRegistrations =
    [
        new WorkflowStepTypeModuleDependencyExpander(),
        new WorkflowImplicitModuleDependencyExpander(),
    ];

    private static readonly IReadOnlyList<IWorkflowModuleConfigurator> ConfiguratorRegistrations = [];

    public string Name => "workflow.core";

    public IReadOnlyList<WorkflowModuleRegistration> Modules => ModuleRegistrations;

    public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders => DependencyExpanderRegistrations;

    public IReadOnlyList<IWorkflowModuleConfigurator> Configurators => ConfiguratorRegistrations;
}
