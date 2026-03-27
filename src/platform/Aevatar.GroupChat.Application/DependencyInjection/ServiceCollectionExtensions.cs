using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Application.Services;
using Aevatar.GroupChat.Application.Participants;
using Aevatar.GroupChat.Application.Feeds;
using Aevatar.GroupChat.Application.Workers;
using Aevatar.GAgentService.Abstractions.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GroupChat.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGroupChatApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IGroupThreadCommandPort, GroupThreadCommandApplicationService>();
        services.TryAddSingleton<IGroupThreadQueryPort, GroupThreadQueryApplicationService>();
        services.TryAddSingleton<IAgentFeedCommandPort, AgentFeedCommandApplicationService>();
        services.TryAddSingleton<IParticipantReplyRunCommandPort, ParticipantReplyRunCommandApplicationService>();
        services.TryAddSingleton<ISourceRegistryCommandPort, SourceRegistryCommandApplicationService>();
        services.TryAddSingleton<IAgentFeedInterestEvaluator, DirectHintAgentFeedInterestEvaluator>();
        services.TryAddSingleton<IParticipantReplyGenerationPort, NoOpParticipantReplyGenerationPort>();
        services.TryAddSingleton<IGroupParticipantReplyProjectionPort, NoOpGroupParticipantReplyProjectionPort>();
        services.TryAddSingleton<GAgentServiceParticipantRuntimeDispatchPort>();
        services.TryAddSingleton<WorkflowParticipantRuntimeDispatchPort>();
        services.TryAddSingleton<IParticipantRuntimeDispatchPort>(sp =>
        {
            var dispatchers = new List<IParticipantRuntimeDispatcher>();
            if (sp.GetService<IServiceInvocationPort>() != null)
                dispatchers.Add(sp.GetRequiredService<GAgentServiceParticipantRuntimeDispatchPort>());
            if (sp.GetService<ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>>() != null)
                dispatchers.Add(sp.GetRequiredService<WorkflowParticipantRuntimeDispatchPort>());

            return dispatchers.Count == 0
                ? new NoOpParticipantRuntimeDispatchPort()
                : new ParticipantRuntimeDispatchRouter(dispatchers);
        });
        services.TryAddSingleton<IGroupMentionHintHandler, GroupMentionHintFeedRoutingHandler>();
        services.TryAddSingleton<IAgentFeedHintHandler, AgentFeedReplyLoopHandler>();
        services.TryAddSingleton<GroupMentionHintWorker>();
        services.TryAddSingleton<AgentFeedService>();
        services.TryAddSingleton<GroupParticipantReplyCompletedService>();
        return services;
    }
}
