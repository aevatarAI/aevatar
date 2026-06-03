using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.AGUI.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Application.Scripts;

public static class ServiceCollectionExtensions
{
    // Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
    //   Old pattern: Scope service script stream inline orchestration in endpoints
    //   New principle: use existing ICommandInteractionService skeleton with ScriptServiceRunCommand and Application-owned service-run registration decorator
    public static IServiceCollection AddScriptServiceRunInteraction(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCqrsCore();
        services.TryAddSingleton<ICommandTargetResolver<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunStartError>, ScriptServiceRunCommandTargetResolver>();
        services.TryAddSingleton<ICommandObservationLifecycle<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError>, ScriptServiceRunCommandTargetBinder>();
        services.TryAddSingleton<ICommandEnvelopeFactory<ScriptServiceRunCommand>, ScriptServiceRunEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<ScriptServiceRunCommandTarget>, ScriptServiceRunCommandDispatcher>();
        services.TryAddSingleton<ICommandReceiptFactory<ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt>, ScriptServiceRunAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError>, DefaultCommandDispatchPipeline<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError>>();
        services.TryAddSingleton<ICommandCompletionPolicy<AGUIEvent, ScriptServiceRunCompletionStatus>, ScriptServiceRunCompletionPolicy>();
        services.TryAddSingleton<ICommandFinalizeEmitter<ScriptServiceRunAcceptedReceipt, ScriptServiceRunCompletionStatus, AGUIEvent>, ScriptServiceRunFinalizeEmitter>();
        services.TryAddSingleton<ICommandDurableCompletionResolver<ScriptServiceRunAcceptedReceipt, ScriptServiceRunCompletionStatus>, ScriptServiceRunDurableCompletionResolver>();
        services.TryAddSingleton<IEventFrameMapper<AGUIEvent, AGUIEvent>, IdentityEventFrameMapper<AGUIEvent>>();
        services.TryAddSingleton<IEventOutputStream<AGUIEvent, AGUIEvent>, DefaultEventOutputStream<AGUIEvent, AGUIEvent>>();
        services.TryAddSingleton(sp =>
            new DefaultCommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, AGUIEvent, ScriptServiceRunCompletionStatus>(
                sp.GetRequiredService<ICommandDispatchPipeline<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError>>(),
                sp.GetRequiredService<IEventOutputStream<AGUIEvent, AGUIEvent>>(),
                sp.GetRequiredService<ICommandCompletionPolicy<AGUIEvent, ScriptServiceRunCompletionStatus>>(),
                sp.GetRequiredService<ICommandFinalizeEmitter<ScriptServiceRunAcceptedReceipt, ScriptServiceRunCompletionStatus, AGUIEvent>>(),
                sp.GetRequiredService<ICommandDurableCompletionResolver<ScriptServiceRunAcceptedReceipt, ScriptServiceRunCompletionStatus>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<DefaultCommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, AGUIEvent, ScriptServiceRunCompletionStatus>>>(),
                sp.GetRequiredService<ICommandObservationLifecycle<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError>>(),
                sp.GetRequiredService<ICommandReceiptFactory<ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt>>()));
        services.TryAddSingleton<ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>>(sp =>
            new ScriptServiceRunRegistrationInteraction(
                sp.GetRequiredService<DefaultCommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, AGUIEvent, ScriptServiceRunCompletionStatus>>(),
                sp.GetRequiredService<Abstractions.Ports.IServiceRunRegistrationPort>()));
        services.TryAddSingleton<IRealtimeSession<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>>(sp =>
            sp.GetRequiredService<ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>>());
        return services;
    }
}
