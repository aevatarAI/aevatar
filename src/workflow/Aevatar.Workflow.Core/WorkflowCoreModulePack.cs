using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowCoreModulePack : IWorkflowModulePack
{
    private static readonly IReadOnlyList<WorkflowModuleRegistration> ModuleRegistrations =
    [
        WorkflowModuleRegistration.Create<ConditionalModule>(WorkflowPrimitiveDefinitions.Get("conditional"), "conditional"),
        WorkflowModuleRegistration.Create<SwitchModule>(WorkflowPrimitiveDefinitions.Get("switch"), "switch"),
        WorkflowModuleRegistration.Create<WhileModule>(WorkflowPrimitiveDefinitions.Get("while"), "while", "loop"),
        WorkflowModuleRegistration.Create<WorkflowCallModule>(WorkflowPrimitiveDefinitions.Get("workflow_call"), "workflow_call", "sub_workflow"),
        WorkflowModuleRegistration.Create<CheckpointModule>(WorkflowPrimitiveDefinitions.Get("checkpoint"), "checkpoint"),
        WorkflowModuleRegistration.Create<AssignModule>(WorkflowPrimitiveDefinitions.Get("assign"), "assign"),
        WorkflowModuleRegistration.Create<ParallelFanOutModule>(WorkflowPrimitiveDefinitions.Get("parallel"), "parallel_fanout", "parallel", "fan_out"),
        WorkflowModuleRegistration.Create<VoteAgreementModule>(WorkflowPrimitiveDefinitions.Get("vote"), "vote", "vote_consensus"),
        WorkflowModuleRegistration.Create<ForEachModule>(WorkflowPrimitiveDefinitions.Get("foreach"), "foreach", "for_each"),
        WorkflowModuleRegistration.Create<RaceModule>(WorkflowPrimitiveDefinitions.Get("race"), "race", "select"),
        WorkflowModuleRegistration.Create<MapReduceModule>(WorkflowPrimitiveDefinitions.Get("map_reduce"), "map_reduce", "mapreduce"),
        WorkflowModuleRegistration.Create<LLMCallModule>(WorkflowPrimitiveDefinitions.Get("llm_call"), "llm_call"),
        WorkflowModuleRegistration.Create<ToolCallModule>(WorkflowPrimitiveDefinitions.Get("tool_call"), "tool_call"),
        WorkflowModuleRegistration.Create<ConnectorCallModule>(WorkflowPrimitiveDefinitions.Get("connector_call"), "connector_call", "bridge_call", "secure_connector_call", "secure_connector"),
        WorkflowModuleRegistration.Create<TransformModule>(WorkflowPrimitiveDefinitions.Get("transform"), "transform"),
        WorkflowModuleRegistration.Create<RetrieveFactsModule>(WorkflowPrimitiveDefinitions.Get("retrieve_facts"), "retrieve_facts"),
        WorkflowModuleRegistration.Create<WaitSignalModule>(WorkflowPrimitiveDefinitions.Get("wait_signal"), "wait_signal", "wait"),
        WorkflowModuleRegistration.Create<GuardModule>(WorkflowPrimitiveDefinitions.Get("guard"), "guard", "assert"),
        WorkflowModuleRegistration.Create<EvaluateModule>(WorkflowPrimitiveDefinitions.Get("evaluate"), "evaluate", "judge"),
        WorkflowModuleRegistration.Create<ReflectModule>(WorkflowPrimitiveDefinitions.Get("reflect"), "reflect"),
        WorkflowModuleRegistration.Create<DelayModule>(WorkflowPrimitiveDefinitions.Get("delay"), "delay", "sleep"),
        WorkflowModuleRegistration.Create<EmitModule>(WorkflowPrimitiveDefinitions.Get("emit"), "emit", "publish"),
        WorkflowModuleRegistration.Create<NotifyModule>(WorkflowPrimitiveDefinitions.Get("notify"), "notify"),
        WorkflowModuleRegistration.Create<ActorSendModule>(WorkflowPrimitiveDefinitions.Get("actor_send"), "actor_send"),
        WorkflowModuleRegistration.Create<CacheModule>(WorkflowPrimitiveDefinitions.Get("cache"), "cache"),
        WorkflowModuleRegistration.Create<HumanApprovalModule>(WorkflowPrimitiveDefinitions.Get("human_approval"), "human_approval"),
        WorkflowModuleRegistration.Create<HumanInputModule>(WorkflowPrimitiveDefinitions.Get("human_input"), "human_input"),
        WorkflowModuleRegistration.Create<SecureInputModule>(WorkflowPrimitiveDefinitions.Get("secure_input"), "secure_input", "secret_input"),
        WorkflowModuleRegistration.Create<WorkflowYamlValidateModule>(WorkflowPrimitiveDefinitions.Get("workflow_yaml_validate"), "workflow_yaml_validate"),
        WorkflowModuleRegistration.Create<DynamicWorkflowModule>(WorkflowPrimitiveDefinitions.Get("dynamic_workflow"), "dynamic_workflow"),
        WorkflowModuleRegistration.Create<LeaseModule>(WorkflowPrimitiveDefinitions.Get("lease"), "lease", "mutex"),
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
