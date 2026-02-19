using Aevatar.Workflow.Core.Connectors;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Core;

/// <summary>
/// DI helpers for Cognitive workflow features.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Cognitive defaults:
    /// - <see cref="WorkflowModuleFactory"/>
    /// - <see cref="IConnectorRegistry"/> (in-memory)
    /// </summary>
    public static IServiceCollection AddAevatarWorkflow(this IServiceCollection services)
    {
        RegisterDefaultWorkflowModules(services);
        services.TryAddSingleton<IEventModuleFactory, WorkflowModuleFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowModuleDependencyExpander, WorkflowLoopModuleDependencyExpander>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowModuleDependencyExpander, WorkflowStepTypeModuleDependencyExpander>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowModuleDependencyExpander, WorkflowImplicitModuleDependencyExpander>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowModuleConfigurator, WorkflowLoopModuleConfigurator>());
        services.TryAddSingleton<IConnectorRegistry, InMemoryConnectorRegistry>();
        return services;
    }

    public static IServiceCollection AddWorkflowModule<TModule>(
        this IServiceCollection services,
        params string[] names)
        where TModule : class, IEventModule
    {
        services.AddSingleton<IWorkflowModuleDescriptor>(sp =>
            new WorkflowModuleDescriptor<TModule>(
                () => ActivatorUtilities.CreateInstance<TModule>(sp),
                names));
        return services;
    }

    private static void RegisterDefaultWorkflowModules(IServiceCollection services)
    {
        services.AddWorkflowModule<WorkflowLoopModule>("workflow_loop");
        services.AddWorkflowModule<ConditionalModule>("conditional");
        services.AddWorkflowModule<SwitchModule>("switch");
        services.AddWorkflowModule<WhileModule>("while", "loop");
        services.AddWorkflowModule<WorkflowCallModule>("workflow_call", "sub_workflow");
        services.AddWorkflowModule<CheckpointModule>("checkpoint");
        services.AddWorkflowModule<AssignModule>("assign");

        services.AddWorkflowModule<ParallelFanOutModule>("parallel_fanout", "parallel", "fan_out");
        services.AddWorkflowModule<VoteConsensusModule>("vote_consensus", "vote");
        services.AddWorkflowModule<ForEachModule>("foreach", "for_each");
        services.AddWorkflowModule<RaceModule>("race", "select");
        services.AddWorkflowModule<MapReduceModule>("map_reduce", "mapreduce");

        services.AddWorkflowModule<LLMCallModule>("llm_call");
        services.AddWorkflowModule<ToolCallModule>("tool_call");
        services.AddWorkflowModule<ConnectorCallModule>("connector_call", "bridge_call");

        services.AddWorkflowModule<TransformModule>("transform");
        services.AddWorkflowModule<RetrieveFactsModule>("retrieve_facts");

        services.AddWorkflowModule<WaitSignalModule>("wait_signal", "wait");
        services.AddWorkflowModule<GuardModule>("guard", "assert");
        services.AddWorkflowModule<EvaluateModule>("evaluate", "judge");
        services.AddWorkflowModule<ReflectModule>("reflect");
        services.AddWorkflowModule<DelayModule>("delay", "sleep");
        services.AddWorkflowModule<EmitModule>("emit", "publish");
        services.AddWorkflowModule<CacheModule>("cache");
        services.AddWorkflowModule<HumanApprovalModule>("human_approval");
        services.AddWorkflowModule<HumanInputModule>("human_input");
    }
}
