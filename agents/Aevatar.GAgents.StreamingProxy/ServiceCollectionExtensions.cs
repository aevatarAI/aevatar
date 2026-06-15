using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgents.StreamingProxy;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStreamingProxy(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(StreamingProxyGAgent).Assembly));
        services.AddCqrsCore();
        services.TryAddSingleton<StreamingProxyNyxParticipantCoordinator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StreamingProxyChatLifecycleContinuationRunner>());
        services.TryAddSingleton<IStreamingProxyRoomCommandService, StreamingProxyRoomCommandService>();
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            StreamingProxyCommittedStateProjectionActivationPlanProvider>());

        services.AddEventSinkProjectionRuntimeCore<
            StreamingProxyRoomSessionProjectionContext,
            StreamingProxyRoomSessionRuntimeLease,
            StreamingProxyRoomSessionEnvelope,
            ProjectionSessionScopeGAgent<StreamingProxyRoomSessionProjectionContext>>(
            static scopeKey => new StreamingProxyRoomSessionProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new StreamingProxyRoomSessionRuntimeLease(context));
        services.TryAddSingleton<IProjectionSessionEventCodec<StreamingProxyRoomSessionEnvelope>, StreamingProxyRoomSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope>, ProjectionSessionEventHub<StreamingProxyRoomSessionEnvelope>>();
        services.TryAddSingleton<IStreamingProxyRoomSessionProjectionPort, StreamingProxyRoomSessionProjectionPort>();
        services.TryAddSingleton<IStreamingProxyRoomSubscriptionObservationPort, StreamingProxyRoomSubscriptionObservationPort>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<StreamingProxyRoomSessionProjectionContext>,
            StreamingProxyRoomSessionEventProjector>());

        services.AddProjectionMaterializationRuntimeCore<
            StreamingProxyCurrentStateProjectionContext,
            StreamingProxyCurrentStateRuntimeLease,
            ProjectionMaterializationScopeGAgent<StreamingProxyCurrentStateProjectionContext>>(
            static scopeKey => new StreamingProxyCurrentStateProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new StreamingProxyCurrentStateRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            StreamingProxyCurrentStateProjectionContext,
            StreamingProxyChatSessionTerminalProjector>();
        services.AddCurrentStateProjectionMaterializer<
            StreamingProxyCurrentStateProjectionContext,
            StreamingProxyRoomParticipantsProjector>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StreamingProxyChatSessionTerminalSnapshot>,
            StreamingProxyChatSessionTerminalSnapshotMetadataProvider>();
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<StreamingProxyRoomParticipantsSnapshot>,
            StreamingProxyRoomParticipantsSnapshotMetadataProvider>();
        services.TryAddSingleton<IStreamingProxyChatSessionTerminalQueryPort, StreamingProxyChatSessionTerminalQueryPort>();
        services.TryAddSingleton<IStreamingProxyRoomParticipantsQueryPort, StreamingProxyRoomParticipantsQueryPort>();
        services.TryAddSingleton<StreamingProxyChatDurableCompletionResolver>();
        AddStreamingProxyRoomInteraction(services);

        return services;
    }

    private static void AddStreamingProxyRoomInteraction(IServiceCollection services)
    {
        // Refactor (iter21/cluster-002-request-path-projection-session-priming):
        //   Old pattern: request handlers synchronously ensure projection/session leases and wait on live sinks.
        //   New principle: commands use accepted receipts; observation is owned by binders or attach-only sessions.
        services.TryAddSingleton<ICommandTargetResolver<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatStartError>, StreamingProxyRoomChatCommandTargetResolver>();
        services.TryAddSingleton<ICommandObservationLifecycle<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>, StreamingProxyRoomObservationLifecycle>();
        services.TryAddSingleton<ICommandEnvelopeFactory<StreamingProxyRoomChatCommand>, StreamingProxyRoomChatCommandEnvelopeFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<StreamingProxyRoomChatCommandTarget>, ActorCommandTargetDispatcher<StreamingProxyRoomChatCommandTarget>>();
        services.TryAddSingleton<ICommandReceiptFactory<StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt>, StreamingProxyRoomChatAcceptedReceiptFactory>();
        services.TryAddSingleton<ICommandDispatchPipeline<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>, DefaultCommandDispatchPipeline<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>>();
        services.TryAddSingleton<ICommandCompletionPolicy<StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>, StreamingProxyRoomChatCompletionPolicy>();
        services.TryAddSingleton<ICommandFinalizeEmitter<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus, StreamingProxyRoomSessionEnvelope>, StreamingProxyRoomChatFinalizeEmitter>();
        services.TryAddSingleton<ICommandDurableCompletionResolver<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus>, StreamingProxyRoomChatDurableCompletionResolverAdapter>();
        services.TryAddSingleton<IEventOutputStream<StreamingProxyRoomSessionEnvelope, StreamingProxyRoomSessionEnvelope>, StreamingProxyRoomChatOutputStream>();
        services.TryAddSingleton<ICommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>(sp =>
            new DefaultCommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>(
                sp.GetRequiredService<ICommandDispatchPipeline<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>>(),
                sp.GetRequiredService<IEventOutputStream<StreamingProxyRoomSessionEnvelope, StreamingProxyRoomSessionEnvelope>>(),
                sp.GetRequiredService<ICommandCompletionPolicy<StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>(),
                sp.GetRequiredService<ICommandFinalizeEmitter<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus, StreamingProxyRoomSessionEnvelope>>(),
                sp.GetRequiredService<ICommandDurableCompletionResolver<StreamingProxyRoomChatAcceptedReceipt, StreamingProxyProjectionCompletionStatus>>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<DefaultCommandInteractionService<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError, StreamingProxyRoomSessionEnvelope, StreamingProxyRoomSessionEnvelope, StreamingProxyProjectionCompletionStatus>>>(),
                sp.GetRequiredService<ICommandObservationLifecycle<StreamingProxyRoomChatCommand, StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt, StreamingProxyRoomChatStartError>>(),
                sp.GetRequiredService<ICommandReceiptFactory<StreamingProxyRoomChatCommandTarget, StreamingProxyRoomChatAcceptedReceipt>>()));
    }

}
