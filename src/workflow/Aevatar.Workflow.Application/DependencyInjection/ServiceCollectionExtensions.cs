using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
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
                registry.Register("auto", WorkflowDefinitionRegistry.CreateBuiltInAutoYaml());
            if (options.RegisterBuiltInAutoReviewWorkflow)
                registry.Register("auto_review", WorkflowDefinitionRegistry.CreateBuiltInAutoReviewYaml());

            return registry;
        });

        services.AddSingleton<WorkflowDirectFallbackPolicy>();
        services.AddSingleton<IWorkflowRunActorResolver>(sp =>
            new WorkflowRunActorResolver(
                sp.GetRequiredService<IWorkflowActorBindingReader>(),
                sp.GetRequiredService<IWorkflowRunActorPort>(),
                sp.GetRequiredService<IWorkflowDefinitionRegistry>(),
                sp.GetRequiredService<WorkflowRunBehaviorOptions>()));
        services.TryAddSingleton<ICommandContextPolicy, DefaultCommandContextPolicy>();
        services.AddSingleton<ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>, WorkflowRunCommandTargetResolver>();
        services.AddSingleton<ICommandTargetBinder<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>, WorkflowRunCommandTargetBinder>();
        services.AddSingleton<ICommandEnvelopeFactory<WorkflowChatRunRequest>, WorkflowChatRequestEnvelopeFactory>();
        services.AddSingleton<ICommandTargetDispatcher<WorkflowRunCommandTarget>, ActorCommandTargetDispatcher<WorkflowRunCommandTarget>>();
        services.AddSingleton<ICommandReceiptFactory<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowRunAcceptedReceiptFactory>();
        services.AddSingleton<ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>, DefaultCommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>();
        services.TryAddSingleton<ICommandFallbackPolicy<WorkflowChatRunRequest>>(sp => sp.GetRequiredService<WorkflowDirectFallbackPolicy>());
        services.TryAddSingleton<ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope>, WorkflowRunFinalizeEmitter>();
        services.TryAddSingleton<ICommandCompletionPolicy<WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>, WorkflowRunCompletionPolicy>();
        services.TryAddSingleton<ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>, WorkflowRunDurableCompletionResolver>();
        services.TryAddSingleton<IEventFrameMapper<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>, IdentityEventFrameMapper<WorkflowRunEventEnvelope>>();
        services.TryAddSingleton<IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>, DefaultEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>>();
        services.AddSingleton<DefaultCommandInteractionService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>>();
        services.AddSingleton<ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>>(sp =>
            new FallbackCommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>(
                sp.GetRequiredService<DefaultCommandInteractionService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>>(),
                sp.GetRequiredService<ICommandFallbackPolicy<WorkflowChatRunRequest>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<FallbackCommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>>>()));
        services.TryAddSingleton<IWorkflowRunReportExportPort, NoopWorkflowRunReportExporter>();
        services.TryAddSingleton<IWorkflowExecutionTopologyResolver, ActorRuntimeWorkflowExecutionTopologyResolver>();
        services.AddSingleton<DefaultDetachedCommandDispatchService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>();
        services.AddSingleton<ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>(sp =>
            new FallbackCommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>(
                sp.GetRequiredService<DefaultDetachedCommandDispatchService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>(),
                sp.GetRequiredService<ICommandFallbackPolicy<WorkflowChatRunRequest>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<FallbackCommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>>()));
        services.TryAddSingleton<ICommandTargetDispatcher<WorkflowRunControlCommandTarget>, ActorCommandTargetDispatcher<WorkflowRunControlCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt>, WorkflowRunControlAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandTargetResolver<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>, WorkflowResumeCommandTargetResolver>();
        services.TryAddSingleton<ICommandTargetBinder<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>, NoOpCommandTargetBinder<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandEnvelopeFactory<WorkflowResumeCommand>, WorkflowResumeCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchPipeline<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchService<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandTargetResolver<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>, WorkflowSignalCommandTargetResolver>();
        services.TryAddSingleton<ICommandTargetBinder<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>, NoOpCommandTargetBinder<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandEnvelopeFactory<WorkflowSignalCommand>, WorkflowSignalCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchPipeline<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchService<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandTargetResolver<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>, WorkflowStopCommandTargetResolver>();
        services.TryAddSingleton<ICommandTargetBinder<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>, NoOpCommandTargetBinder<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandEnvelopeFactory<WorkflowStopCommand>, WorkflowStopCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchPipeline<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchService<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<RegistryBackedWorkflowCatalogPort>();
        services.TryAddSingleton<IWorkflowCatalogPort>(sp =>
            sp.GetRequiredService<RegistryBackedWorkflowCatalogPort>());
        services.TryAddSingleton<IWorkflowCapabilitiesPort>(sp =>
            sp.GetRequiredService<RegistryBackedWorkflowCatalogPort>());
        services.AddSingleton<IWorkflowExecutionQueryApplicationService, WorkflowExecutionQueryApplicationService>();
        return services;
    }
}
