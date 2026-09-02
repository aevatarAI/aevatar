using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.ExternalCapabilities;
using Aevatar.Workflow.Application.Observatory;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Application.RunForks;
using Aevatar.Workflow.Application.Runs;
using Aevatar.Workflow.Application.Schedules;
using Aevatar.Workflow.Application.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowApplication(
        this IServiceCollection services,
        Action<WorkflowDefinitionCatalogOptions>? configureRegistry = null,
        Action<WorkflowRunBehaviorOptions>? configureRunBehavior = null)
    {
        var options = new WorkflowDefinitionCatalogOptions();
        configureRegistry?.Invoke(options);
        var runBehaviorOptions = new WorkflowRunBehaviorOptions();
        configureRunBehavior?.Invoke(runBehaviorOptions);
        if (runBehaviorOptions.AcceptedObservationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configureRunBehavior),
                "Workflow accepted observation timeout must be positive.");
        }
        if (runBehaviorOptions.ChatHistoryReservationObservationTimeout <= TimeSpan.Zero ||
            runBehaviorOptions.ChatHistoryReservationObservationInterval <= TimeSpan.Zero ||
            runBehaviorOptions.ChatHistoryReservationObservationInterval >
            runBehaviorOptions.ChatHistoryReservationObservationTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configureRunBehavior),
                "Chat history reservation observation timing must be positive and the interval must not exceed the timeout.");
        }
        services.AddSingleton(runBehaviorOptions);
        services.TryAddTransient<ExternalWorkflowCapabilityReadinessService>();
        services.TryAddTransient<IExternalWorkflowCapabilityListPort>(provider =>
            provider.GetRequiredService<ExternalWorkflowCapabilityReadinessService>());
        services.TryAddTransient<IExternalWorkflowCapabilityReadinessPort>(provider =>
            provider.GetRequiredService<ExternalWorkflowCapabilityReadinessService>());
        services.TryAddTransient<IWorkflowExternalCapabilityAdmissionService,
            WorkflowExternalCapabilityAdmissionService>();
        services.TryAddTransient<IWorkflowArtifactCompatibilityPreflight,
            WorkflowArtifactCompatibilityPreflight>();
        services.TryAddSingleton<IWorkflowExplicitRequestPreviewService,
            WorkflowExplicitRequestPreviewService>();
        services.TryAddTransient<IWorkflowDraftRunCapabilityAdmissionService,
            WorkflowDraftRunCapabilityAdmissionService>();

        services.AddSingleton<IWorkflowDefinitionCatalog>(_ =>
        {
            var catalog = new WorkflowDefinitionCatalog();
            if (options.RegisterBuiltInDirectWorkflow)
                catalog.Register(
                    "direct",
                    WorkflowDefinitionCatalog.BuiltInDirectYaml,
                    Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive);
            if (options.RegisterBuiltInStudioWorkflow)
                catalog.Register(
                    "studio",
                    WorkflowDefinitionCatalog.BuiltInStudioYaml,
                    Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive);
            if (options.RegisterBuiltInAutoWorkflow)
                catalog.Register(
                    "auto",
                    WorkflowDefinitionCatalog.CreateBuiltInAutoYaml(),
                    Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive);
            if (options.RegisterBuiltInAutoReviewWorkflow)
                catalog.Register(
                    "auto_review",
                    WorkflowDefinitionCatalog.CreateBuiltInAutoReviewYaml(),
                    Aevatar.Workflow.Abstractions.ExternalCapabilityExecutionMode.Interactive);

            return catalog;
        });

        services.AddSingleton<WorkflowDirectFallbackPolicy>();
        services.AddSingleton<IWorkflowRunActorResolver>(sp =>
            new WorkflowRunActorResolver(
                sp.GetRequiredService<IWorkflowActorBindingReader>(),
                sp.GetRequiredService<IWorkflowRunProvisioningPort>(),
                sp.GetRequiredService<IWorkflowDefinitionParser>(),
                sp.GetRequiredService<IWorkflowDefinitionCatalog>(),
                sp.GetRequiredService<IWorkflowDraftRunCapabilityAdmissionService>(),
                sp.GetRequiredService<WorkflowRunBehaviorOptions>()));
        services.TryAddSingleton<ICommandContextPolicy, DefaultCommandContextPolicy>();
        services.AddSingleton<ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>, WorkflowRunCommandTargetResolver>();
        services.AddSingleton<ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>, WorkflowRunAcceptedCommandTargetResolver>();
        services.AddSingleton<ICommandEnvelopeFactory<WorkflowChatRunRequest>, WorkflowChatRequestEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetResolver<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunStartError>, WorkflowForkRunCommandTargetResolver>();
        services.TryAddSingleton<ICommandTargetDispatcher<WorkflowForkRunCommandTarget>>(sp =>
            new ActorCommandTargetDispatcher<WorkflowForkRunCommandTarget>(
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.IActorDispatchPort>()));
        services.TryAddSingleton<ICommandTargetEnvelopeFactory<WorkflowForkRunCommand, WorkflowForkRunCommandTarget>, WorkflowForkRunCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandReceiptFactory<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>, DefaultCommandDispatchPipeline<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>>();
        services.TryAddSingleton<ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>, WorkflowForkRunCommandDispatchService>();
        services.TryAddSingleton(sp => new Lazy<ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>>(
            () => sp.GetRequiredService<ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            WorkflowRunForkCoordinator>());
        services.AddSingleton<ICommandTargetDispatcher<WorkflowRunCommandTarget>, ActorCommandTargetDispatcher<WorkflowRunCommandTarget>>();
        services.AddSingleton<ICommandTargetDispatcher<WorkflowRunAcceptedCommandTarget>, ActorCommandTargetDispatcher<WorkflowRunAcceptedCommandTarget>>();
        services.AddSingleton<ICommandReceiptFactory<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowRunAcceptedReceiptFactory>();
        services.AddSingleton<ICommandReceiptFactory<WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt>, WorkflowRunAcceptedReceiptFactory>();
        services.AddSingleton<ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>, DefaultCommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>();
        services.AddSingleton<ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>, DefaultCommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>();
        services.TryAddSingleton<ICommandFallbackPolicy<WorkflowChatRunRequest>>(sp => sp.GetRequiredService<WorkflowDirectFallbackPolicy>());
        services.TryAddSingleton<ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope>, WorkflowRunFinalizeEmitter>();
        services.TryAddSingleton<ICommandCompletionPolicy<WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>, WorkflowRunCompletionPolicy>();
        services.TryAddSingleton<WorkflowRunDurableCompletionResolver>();
        services.TryAddSingleton<ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>>(sp =>
            sp.GetRequiredService<WorkflowRunDurableCompletionResolver>());
        // 06-20-observatory-run-state-feed (R2): gate that confirms an ad-hoc run's current-state doc has
        // materialized before its throwaway actors are reclaimed. Built from the head-version + durable
        // materialization-watermark read ports (supplied by Infrastructure); options bound by the host.
        services.TryAddSingleton<WorkflowRunReclaimOptions>();
        services.TryAddSingleton<WorkflowRunMaterializationReclaimGate>(sp =>
            new WorkflowRunMaterializationReclaimGate(
                sp.GetRequiredService<IWorkflowRunCommittedVersionPort>(),
                sp.GetRequiredService<IWorkflowRunMaterializationWatermarkPort>(),
                sp.GetRequiredService<WorkflowRunReclaimOptions>(),
                logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<WorkflowRunMaterializationReclaimGate>>()));
        services.TryAddSingleton<ICommandObservationScopeLeasePreparation<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>, WorkflowRunObservationScopeLeasePreparation>();
        services.TryAddSingleton<ICommandObservationLifecycle<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>, WorkflowRunObservationLifecycle>();
        services.TryAddSingleton<IEventFrameMapper<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>, IdentityEventFrameMapper<WorkflowRunEventEnvelope>>();
        services.TryAddSingleton<IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>, DefaultEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>>();
        services.AddSingleton(sp =>
            new DefaultCommandInteractionService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>(
                sp.GetRequiredService<ICommandDispatchPipeline<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>(),
                sp.GetRequiredService<IEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>>(),
                sp.GetRequiredService<ICommandCompletionPolicy<WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>>(),
                sp.GetRequiredService<ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope>>(),
                sp.GetRequiredService<ICommandDurableCompletionResolver<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<DefaultCommandInteractionService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>>>(),
                sp.GetRequiredService<ICommandObservationLifecycle<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>(),
                sp.GetRequiredService<ICommandReceiptFactory<WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt>>(),
                sp.GetRequiredService<ICommandObservationScopeLeasePreparation<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>(),
                acceptedObservationTimeout: sp.GetRequiredService<WorkflowRunBehaviorOptions>().AcceptedObservationTimeout));
        services.AddSingleton<IWorkflowChatRunInteractionPort>(sp =>
            new WorkflowChatRunInteractionService(
                sp.GetRequiredService<IWorkflowRunActorResolver>(),
                sp.GetRequiredService<IWorkflowExecutionProjectionPort>(),
                sp.GetRequiredService<IWorkflowRunProvisioningPort>(),
                sp.GetRequiredService<DefaultCommandInteractionService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>>(),
                sp.GetRequiredService<WorkflowDirectFallbackPolicy>(),
                sp.GetService<IWorkflowChatHistoryTerminalDeliveryPort>(),
                sp.GetService<IWorkflowChatHistoryCreateRecoveryReadPort>(),
                sp.GetRequiredService<WorkflowRunBehaviorOptions>(),
                sp.GetService<IWorkflowExecutionCurrentStateQueryPort>(),
                sp.GetService<ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(),
                sp.GetService<ICommandFinalizeEmitter<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus, WorkflowRunEventEnvelope>>()));
        services.TryAddSingleton<IWorkflowRunReportExportPort, NoopWorkflowRunReportExporter>();
        // Refactor (iter18/cluster-005):
        //   Old pattern: accepted-only dispatch used a detached live-sink monitor service
        //   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
        services.AddSingleton<DefaultCommandDispatchService<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>();
        services.AddSingleton<ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>(sp =>
            new FallbackCommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>(
                sp.GetRequiredService<DefaultCommandDispatchService<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>(),
                sp.GetRequiredService<ICommandFallbackPolicy<WorkflowChatRunRequest>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<FallbackCommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>>()));
        services.TryAddSingleton<ICommandTargetDispatcher<WorkflowRunControlCommandTarget>, ActorCommandTargetDispatcher<WorkflowRunControlCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt>, WorkflowRunControlAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandTargetResolver<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>, WorkflowResumeCommandTargetResolver>();
        services.TryAddSingleton<ICommandEnvelopeFactory<WorkflowResumeCommand>, WorkflowResumeCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchPipeline<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchService<WorkflowResumeCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandTargetResolver<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>, WorkflowSignalCommandTargetResolver>();
        services.TryAddSingleton<ICommandEnvelopeFactory<WorkflowSignalCommand>, WorkflowSignalCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchPipeline<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchService<WorkflowSignalCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandTargetResolver<WorkflowRetryCompensationCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>, WorkflowRetryCompensationCommandTargetResolver>();
        services.TryAddSingleton<ICommandEnvelopeFactory<WorkflowRetryCompensationCommand>, WorkflowRetryCompensationCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<WorkflowRetryCompensationCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchPipeline<WorkflowRetryCompensationCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchService<WorkflowRetryCompensationCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandTargetResolver<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>, WorkflowStopCommandTargetResolver>();
        services.TryAddSingleton<ICommandEnvelopeFactory<WorkflowStopCommand>, WorkflowStopCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchPipeline<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>, DefaultCommandDispatchService<WorkflowStopCommand, WorkflowRunControlCommandTarget, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>();
        services.TryAddSingleton<RegistryBackedWorkflowCatalogPort>();
        services.TryAddSingleton<IWorkflowCatalogPort>(sp =>
            sp.GetRequiredService<RegistryBackedWorkflowCatalogPort>());
        services.TryAddSingleton<IWorkflowCapabilitiesPort>(sp =>
            sp.GetRequiredService<RegistryBackedWorkflowCatalogPort>());
        services.AddSingleton<WorkflowExecutionQueryApplicationService>();
        services.AddSingleton<IWorkflowExecutionQueryApplicationService>(sp =>
            sp.GetRequiredService<WorkflowExecutionQueryApplicationService>());
        services.AddSingleton<IWorkflowExecutionScopeQueryApplicationService>(sp =>
            sp.GetRequiredService<WorkflowExecutionQueryApplicationService>());
        // 06-19-workflow-run-observatory (C2): scope-enforcement seam for the read-only run viewer.
        // 06-20-observatory-admin-cross-scope (G3): one instance backs both the scope-bound reads and the
        // separate cross-scope admin query contract.
        services.TryAddSingleton<WorkflowRunObservatoryQueryService>();
        services.TryAddSingleton<IWorkflowRunObservatoryQueryService>(sp =>
            sp.GetRequiredService<WorkflowRunObservatoryQueryService>());
        services.TryAddSingleton<IWorkflowRunAdminQueryService>(sp =>
            sp.GetRequiredService<WorkflowRunObservatoryQueryService>());
        services.TryAddSingleton<IWorkflowScheduleApplicationService, WorkflowScheduleApplicationService>();
        services.TryAddSingleton<IWorkflowScheduleCommandPort, WorkflowScheduleCommandPort>();
        return services;
    }
}
