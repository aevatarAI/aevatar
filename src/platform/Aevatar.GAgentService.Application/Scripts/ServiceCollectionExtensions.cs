using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using Aevatar.GAgentService.Abstractions.ScopeScripts;
using Aevatar.Presentation.AGUI;
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
        services.TryAddSingleton<ICommandTargetBinder<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunStartError>, ScriptServiceRunCommandTargetBinder>();
        services.TryAddSingleton<ICommandEnvelopeFactory<ScriptServiceRunCommand>, ScriptServiceRunEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<ScriptServiceRunCommandTarget>, ScriptServiceRunCommandDispatcher>();
        services.TryAddSingleton<ICommandReceiptFactory<ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt>, ScriptServiceRunAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError>, DefaultCommandDispatchPipeline<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError>>();
        services.TryAddSingleton<ICommandCompletionPolicy<AGUIEvent, ScriptServiceRunCompletionStatus>, ScriptServiceRunCompletionPolicy>();
        services.TryAddSingleton<ICommandFinalizeEmitter<ScriptServiceRunAcceptedReceipt, ScriptServiceRunCompletionStatus, AGUIEvent>, ScriptServiceRunFinalizeEmitter>();
        services.TryAddSingleton<ICommandDurableCompletionResolver<ScriptServiceRunAcceptedReceipt, ScriptServiceRunCompletionStatus>, ScriptServiceRunDurableCompletionResolver>();
        services.TryAddSingleton<IEventFrameMapper<AGUIEvent, AGUIEvent>, IdentityEventFrameMapper<AGUIEvent>>();
        services.TryAddSingleton<IEventOutputStream<AGUIEvent, AGUIEvent>, DefaultEventOutputStream<AGUIEvent, AGUIEvent>>();
        services.TryAddSingleton<DefaultCommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, AGUIEvent, ScriptServiceRunCompletionStatus>>();
        services.TryAddSingleton<ICommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, ScriptServiceRunCompletionStatus>>(sp =>
            new ScriptServiceRunRegistrationInteraction(
                sp.GetRequiredService<DefaultCommandInteractionService<ScriptServiceRunCommand, ScriptServiceRunCommandTarget, ScriptServiceRunAcceptedReceipt, ScriptServiceRunStartError, AGUIEvent, AGUIEvent, ScriptServiceRunCompletionStatus>>(),
                sp.GetRequiredService<Abstractions.Ports.IServiceRunRegistrationPort>()));
        return services;
    }
}
