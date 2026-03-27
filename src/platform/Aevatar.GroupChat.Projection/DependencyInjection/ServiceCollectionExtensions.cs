using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Projection.Configuration;
using Aevatar.GroupChat.Projection.Contexts;
using Aevatar.GroupChat.Projection.Hinting;
using Aevatar.GroupChat.Projection.Metadata;
using Aevatar.GroupChat.Projection.Orchestration;
using Aevatar.GroupChat.Projection.Projectors;
using Aevatar.GroupChat.Projection.Queries;
using Aevatar.GroupChat.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GroupChat.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGroupChatProjection(
        this IServiceCollection services,
        Action<GroupChatProjectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new GroupChatProjectionOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.TryAddSingleton<IProjectionRuntimeOptions>(sp => sp.GetRequiredService<GroupChatProjectionOptions>());
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<IGroupMentionHintPublisher, StreamGroupMentionHintPublisher>();
        services.TryAddSingleton<NoOpGroupMentionHintPublisher>();
        services.TryAddSingleton<IAgentFeedHintPublisher, StreamAgentFeedHintPublisher>();
        services.TryAddSingleton<IGroupParticipantReplyCompletedPublisher, StreamGroupParticipantReplyCompletedPublisher>();
        services.TryAddSingleton<NoOpGroupParticipantReplyCompletedPublisher>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<GroupTimelineReadModel>, GroupTimelineReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<AgentFeedReadModel>, AgentFeedReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<SourceCatalogReadModel>, SourceCatalogReadModelMetadataProvider>();
        services.AddProjectionMaterializationRuntimeCore<
            GroupTimelineProjectionContext,
            GroupTimelineProjectionRuntimeLease,
            ProjectionMaterializationScopeGAgent<GroupTimelineProjectionContext>>(
            static scopeKey => new GroupTimelineProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new GroupTimelineProjectionRuntimeLease(context));
        services.AddProjectionMaterializationRuntimeCore<
            AgentFeedProjectionContext,
            AgentFeedProjectionRuntimeLease,
            ProjectionMaterializationScopeGAgent<AgentFeedProjectionContext>>(
            static scopeKey => new AgentFeedProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new AgentFeedProjectionRuntimeLease(context));
        services.AddProjectionMaterializationRuntimeCore<
            SourceCatalogProjectionContext,
            SourceCatalogProjectionRuntimeLease,
            ProjectionMaterializationScopeGAgent<SourceCatalogProjectionContext>>(
            static scopeKey => new SourceCatalogProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new SourceCatalogProjectionRuntimeLease(context));
        services.AddEventSinkProjectionRuntimeCore<
            GroupParticipantReplyProjectionContext,
            GroupParticipantReplyProjectionRuntimeLease,
            GroupParticipantReplyCompletedEvent,
            ProjectionSessionScopeGAgent<GroupParticipantReplyProjectionContext>>(
            static scopeKey => new GroupParticipantReplyProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new GroupParticipantReplyProjectionRuntimeLease(context));
        services.TryAddSingleton<GroupTimelineProjectionPort>();
        services.TryAddSingleton<GroupTimelineQueryPort>();
        services.TryAddSingleton<AgentFeedProjectionPort>();
        services.TryAddSingleton<AgentFeedQueryPort>();
        services.TryAddSingleton<SourceCatalogProjectionPort>();
        services.TryAddSingleton<SourceCatalogQueryPort>();
        services.TryAddSingleton<GroupParticipantReplyProjectionPort>();
        services.TryAddSingleton<IGroupTimelineProjectionPort>(sp => sp.GetRequiredService<GroupTimelineProjectionPort>());
        services.TryAddSingleton<IGroupTimelineQueryPort>(sp => sp.GetRequiredService<GroupTimelineQueryPort>());
        services.TryAddSingleton<IAgentFeedProjectionPort>(sp => sp.GetRequiredService<AgentFeedProjectionPort>());
        services.TryAddSingleton<IAgentFeedQueryPort>(sp => sp.GetRequiredService<AgentFeedQueryPort>());
        services.TryAddSingleton<ISourceCatalogProjectionPort>(sp => sp.GetRequiredService<SourceCatalogProjectionPort>());
        services.TryAddSingleton<ISourceRegistryQueryPort>(sp => sp.GetRequiredService<SourceCatalogQueryPort>());
        services.Replace(ServiceDescriptor.Singleton<IGroupParticipantReplyProjectionPort>(sp => sp.GetRequiredService<GroupParticipantReplyProjectionPort>()));
        services.AddProjectionArtifactMaterializer<
            GroupTimelineProjectionContext,
            GroupMentionHintProjector>();
        services.AddProjectionArtifactMaterializer<
            AgentFeedProjectionContext,
            AgentFeedAcceptedHintProjector>();
        services.AddCurrentStateProjectionMaterializer<
            GroupTimelineProjectionContext,
            GroupTimelineCurrentStateProjector>();
        services.AddCurrentStateProjectionMaterializer<
            AgentFeedProjectionContext,
            AgentFeedCurrentStateProjector>();
        services.AddCurrentStateProjectionMaterializer<
            SourceCatalogProjectionContext,
            SourceCatalogCurrentStateProjector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<GroupParticipantReplyProjectionContext>,
            GroupParticipantReplySessionProjector>());
        return services;
    }
}
