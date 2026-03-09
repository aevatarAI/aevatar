using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
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
                sp.GetRequiredService<IWorkflowActorBindingReader>(),
                sp.GetRequiredService<IWorkflowRunActorPort>(),
                sp.GetRequiredService<IWorkflowDefinitionRegistry>(),
                sp.GetRequiredService<WorkflowRunBehaviorOptions>()));
        services.TryAddSingleton<ICommandContextPolicy, WorkflowCommandContextPolicy>();
        services.AddSingleton<ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>, WorkflowRunCommandTargetResolver>();
        services.AddSingleton<ICommandTargetBinder<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>, WorkflowRunCommandTargetBinder>();
        services.AddSingleton<ICommandEnvelopeFactory<WorkflowChatRunRequest>, WorkflowChatRequestEnvelopeFactory>();
        services.AddSingleton<ICommandTargetDispatcher<WorkflowRunCommandTarget>, ActorCommandTargetDispatcher<WorkflowRunCommandTarget>>();
        services.AddSingleton<ICommandReceiptFactory<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowRunAcceptedReceiptFactory>();
        services.AddSingleton<ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>, DefaultCommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>();
        services.TryAddSingleton<IWorkflowRunStateSnapshotEmitter, WorkflowRunStateSnapshotEmitter>();
        services.TryAddSingleton<IWorkflowRunCompletionPolicy, WorkflowRunCompletionPolicy>();
        services.AddSingleton<WorkflowRunOutputStreamer>();
        services.AddSingleton<IWorkflowRunOutputStreamer>(sp => sp.GetRequiredService<WorkflowRunOutputStreamer>());
        services.TryAddSingleton<IEventOutputStream<WorkflowRunEvent, WorkflowOutputFrame>>(sp => sp.GetRequiredService<WorkflowRunOutputStreamer>());
        services.TryAddSingleton<IEventFrameMapper<WorkflowRunEvent, WorkflowOutputFrame>>(sp => sp.GetRequiredService<WorkflowRunOutputStreamer>());
        services.TryAddSingleton<IWorkflowExecutionReportArtifactSink, NoopWorkflowExecutionReportArtifactSink>();
        services.TryAddSingleton<IWorkflowExecutionTopologyResolver, ActorRuntimeWorkflowExecutionTopologyResolver>();
        services.AddSingleton<IWorkflowRunInteractionService, WorkflowRunInteractionService>();
        services.AddSingleton<ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>, WorkflowRunDetachedDispatchService>();
        services.AddSingleton<IWorkflowExecutionQueryApplicationService, WorkflowExecutionQueryApplicationService>();
        return services;
    }
}
