using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.OpenClaw;
using Aevatar.Workflow.Application.Adapters;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.OpenClaw;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Orchestration;
using Aevatar.Workflow.Application.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowApplication(
        this IServiceCollection services,
        Action<WorkflowDefinitionRegistryOptions>? configureRegistry = null,
        Action<WorkflowRunBehaviorOptions>? configureRunBehavior = null)
    {
        var options = new WorkflowDefinitionRegistryOptions();
        configureRegistry?.Invoke(options);
        var runBehaviorOptions = new WorkflowRunBehaviorOptions();
        configureRunBehavior?.Invoke(runBehaviorOptions);
        services.AddSingleton(runBehaviorOptions);

        services.AddSingleton<IWorkflowDefinitionRegistry>(_ =>
        {
            var registry = new WorkflowDefinitionRegistry();
            if (options.RegisterBuiltInDirectWorkflow)
                registry.Register("direct", WorkflowDefinitionRegistry.BuiltInDirectYaml);
            if (options.RegisterBuiltInAutoWorkflow)
                registry.Register("auto", WorkflowDefinitionRegistry.BuiltInAutoYaml);
            if (options.RegisterBuiltInAutoReviewWorkflow)
                registry.Register("auto_review", WorkflowDefinitionRegistry.BuiltInAutoReviewYaml);

            return registry;
        });

        services.AddSingleton<WorkflowDirectFallbackPolicy>();
        services.AddSingleton<IWorkflowRunActorResolver>(sp =>
            new WorkflowRunActorResolver(
                sp.GetRequiredService<IWorkflowRunActorPort>(),
                sp.GetRequiredService<IWorkflowDefinitionRegistry>(),
                sp.GetRequiredService<WorkflowRunBehaviorOptions>()));
        services.TryAddSingleton<ICommandContextPolicy, WorkflowCommandContextPolicy>();
        services.AddSingleton<ICommandEnvelopeFactory<WorkflowChatRunRequest>, WorkflowChatRequestEnvelopeFactory>();
        services.AddSingleton<IWorkflowRunRequestExecutor, WorkflowRunRequestExecutor>();
        services.AddSingleton<IWorkflowRunContextFactory, WorkflowRunContextFactory>();
        services.TryAddSingleton<IWorkflowRunStateSnapshotEmitter, WorkflowRunStateSnapshotEmitter>();
        services.AddSingleton<IWorkflowRunExecutionEngine, WorkflowRunExecutionEngine>();
        services.TryAddSingleton<IWorkflowRunCompletionPolicy, WorkflowRunCompletionPolicy>();
        services.TryAddSingleton<IWorkflowRunResourceFinalizer, WorkflowRunResourceFinalizer>();
        services.AddSingleton<WorkflowRunOutputStreamer>();
        services.AddSingleton<IWorkflowRunOutputStreamer>(sp => sp.GetRequiredService<WorkflowRunOutputStreamer>());
        services.TryAddSingleton<IEventOutputStream<WorkflowRunEvent, WorkflowOutputFrame>>(sp => sp.GetRequiredService<WorkflowRunOutputStreamer>());
        services.TryAddSingleton<IEventFrameMapper<WorkflowRunEvent, WorkflowOutputFrame>>(sp => sp.GetRequiredService<WorkflowRunOutputStreamer>());
        services.TryAddSingleton<IWorkflowExecutionReportArtifactSink, NoopWorkflowExecutionReportArtifactSink>();
        services.TryAddSingleton<IWorkflowExecutionTopologyResolver, ActorRuntimeWorkflowExecutionTopologyResolver>();
        services.AddSingleton<IWorkflowRunCommandService, WorkflowChatRunApplicationService>();
        services.TryAddSingleton<IOpenClawBridgeOrchestrationService, OpenClawBridgeOrchestrationService>();
        services.AddSingleton<IWorkflowExecutionQueryApplicationService, WorkflowExecutionQueryApplicationService>();
        services.TryAddSingleton<WorkflowCommandExecutionServiceAdapter>();
        services.TryAddSingleton<ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>>(sp =>
            sp.GetRequiredService<WorkflowCommandExecutionServiceAdapter>());
        return services;
    }
}
