using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Application.Services;
using Aevatar.GroupChat.Application.Participants;
using Aevatar.GroupChat.Application.Feeds;
using Aevatar.GroupChat.Application.Workers;
using Aevatar.GAgentService.Abstractions.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GroupChat.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGroupChatApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IGroupThreadCommandPort, GroupThreadCommandApplicationService>();
        services.TryAddSingleton<IGroupThreadQueryPort, GroupThreadQueryApplicationService>();
        services.TryAddSingleton<IAgentFeedCommandPort, AgentFeedCommandApplicationService>();
        services.TryAddSingleton<ISourceRegistryCommandPort, SourceRegistryCommandApplicationService>();
        services.TryAddSingleton<IAgentFeedInterestEvaluator, DirectHintAgentFeedInterestEvaluator>();
        services.TryAddSingleton<IParticipantReplyGenerationPort, NoOpParticipantReplyGenerationPort>();
        services.TryAddSingleton<IGroupParticipantReplyProjectionPort, NoOpGroupParticipantReplyProjectionPort>();
        services.TryAddSingleton<IParticipantRuntimeDispatchPort>(sp =>
        {
            var invocationPort = sp.GetService<IServiceInvocationPort>();
            return invocationPort == null
                ? new NoOpParticipantRuntimeDispatchPort()
                : new GAgentServiceParticipantRuntimeDispatchPort(invocationPort);
        });
        services.TryAddSingleton<IGroupMentionHintHandler, GroupMentionHintFeedRoutingHandler>();
        services.TryAddSingleton<IAgentFeedHintHandler, AgentFeedReplyLoopHandler>();
        services.TryAddSingleton<GroupMentionHintWorker>();
        services.TryAddSingleton<AgentFeedService>();
        services.TryAddSingleton<GroupParticipantReplyCompletedService>();
        return services;
    }
}
