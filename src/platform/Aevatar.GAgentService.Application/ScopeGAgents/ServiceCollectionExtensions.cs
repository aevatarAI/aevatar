using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Presentation.AGUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Application.ScopeGAgents;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddScopeGAgentDraftRunInteraction(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCqrsCore();
        services.TryAddSingleton<ICommandTargetResolver<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunStartError>, GAgentDraftRunCommandTargetResolver>();
        services.TryAddSingleton<ICommandTargetBinder<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunStartError>, GAgentDraftRunCommandTargetBinder>();
        services.TryAddSingleton<ICommandEnvelopeFactory<GAgentDraftRunCommand>, GAgentDraftRunCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<GAgentDraftRunCommandTarget>, ActorCommandTargetDispatcher<GAgentDraftRunCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt>, GAgentDraftRunAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError>, DefaultCommandDispatchPipeline<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError>>();
        services.TryAddSingleton<ICommandCompletionPolicy<AGUIEvent, GAgentDraftRunCompletionStatus>, GAgentDraftRunCompletionPolicy>();
        services.TryAddSingleton<ICommandFinalizeEmitter<GAgentDraftRunAcceptedReceipt, GAgentDraftRunCompletionStatus, AGUIEvent>, GAgentDraftRunFinalizeEmitter>();
        services.TryAddSingleton<ICommandDurableCompletionResolver<GAgentDraftRunAcceptedReceipt, GAgentDraftRunCompletionStatus>, GAgentDraftRunDurableCompletionResolver>();
        services.TryAddSingleton<IEventFrameMapper<AGUIEvent, AGUIEvent>, IdentityEventFrameMapper<AGUIEvent>>();
        services.TryAddSingleton<IEventOutputStream<AGUIEvent, AGUIEvent>, DefaultEventOutputStream<AGUIEvent, AGUIEvent>>();
        services.TryAddSingleton<ICommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, GAgentDraftRunCompletionStatus>>(sp =>
            new DefaultCommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, AGUIEvent, GAgentDraftRunCompletionStatus>(
                sp.GetRequiredService<ICommandDispatchPipeline<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError>>(),
                sp.GetRequiredService<IEventOutputStream<AGUIEvent, AGUIEvent>>(),
                sp.GetRequiredService<ICommandCompletionPolicy<AGUIEvent, GAgentDraftRunCompletionStatus>>(),
                sp.GetRequiredService<ICommandFinalizeEmitter<GAgentDraftRunAcceptedReceipt, GAgentDraftRunCompletionStatus, AGUIEvent>>(),
                sp.GetRequiredService<ICommandDurableCompletionResolver<GAgentDraftRunAcceptedReceipt, GAgentDraftRunCompletionStatus>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<DefaultCommandInteractionService<GAgentDraftRunCommand, GAgentDraftRunCommandTarget, GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, AGUIEvent, AGUIEvent, GAgentDraftRunCompletionStatus>>>()));
        services.TryAddSingleton<ICommandTargetResolver<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalStartError>, GAgentApprovalCommandTargetResolver>();
        services.TryAddSingleton<ICommandTargetBinder<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalStartError>, GAgentApprovalCommandTargetBinder>();
        services.TryAddSingleton<ICommandEnvelopeFactory<GAgentApprovalCommand>, GAgentApprovalCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<GAgentApprovalCommandTarget>, ActorCommandTargetDispatcher<GAgentApprovalCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<GAgentApprovalCommandTarget, GAgentApprovalAcceptedReceipt>, GAgentApprovalAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalAcceptedReceipt, GAgentApprovalStartError>, DefaultCommandDispatchPipeline<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalAcceptedReceipt, GAgentApprovalStartError>>();
        services.TryAddSingleton<ICommandCompletionPolicy<AGUIEvent, GAgentApprovalCompletionStatus>, GAgentApprovalCompletionPolicy>();
        services.TryAddSingleton<ICommandFinalizeEmitter<GAgentApprovalAcceptedReceipt, GAgentApprovalCompletionStatus, AGUIEvent>, GAgentApprovalFinalizeEmitter>();
        services.TryAddSingleton<ICommandDurableCompletionResolver<GAgentApprovalAcceptedReceipt, GAgentApprovalCompletionStatus>, GAgentApprovalDurableCompletionResolver>();
        services.TryAddSingleton<ICommandInteractionService<GAgentApprovalCommand, GAgentApprovalAcceptedReceipt, GAgentApprovalStartError, AGUIEvent, GAgentApprovalCompletionStatus>>(sp =>
            new DefaultCommandInteractionService<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalAcceptedReceipt, GAgentApprovalStartError, AGUIEvent, AGUIEvent, GAgentApprovalCompletionStatus>(
                sp.GetRequiredService<ICommandDispatchPipeline<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalAcceptedReceipt, GAgentApprovalStartError>>(),
                sp.GetRequiredService<IEventOutputStream<AGUIEvent, AGUIEvent>>(),
                sp.GetRequiredService<ICommandCompletionPolicy<AGUIEvent, GAgentApprovalCompletionStatus>>(),
                sp.GetRequiredService<ICommandFinalizeEmitter<GAgentApprovalAcceptedReceipt, GAgentApprovalCompletionStatus, AGUIEvent>>(),
                sp.GetRequiredService<ICommandDurableCompletionResolver<GAgentApprovalAcceptedReceipt, GAgentApprovalCompletionStatus>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<DefaultCommandInteractionService<GAgentApprovalCommand, GAgentApprovalCommandTarget, GAgentApprovalAcceptedReceipt, GAgentApprovalStartError, AGUIEvent, AGUIEvent, GAgentApprovalCompletionStatus>>>()));
        return services;
    }
}
