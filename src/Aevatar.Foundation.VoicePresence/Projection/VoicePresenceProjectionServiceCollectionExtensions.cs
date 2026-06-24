using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.VoicePresence.Projection;

public static class VoicePresenceProjectionServiceCollectionExtensions
{
    public static IServiceCollection AddVoicePresenceCapabilityProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.AddEventSinkProjectionRuntimeCore<
            VoiceRealtimeProjectionContext,
            VoiceRealtimeRuntimeLease,
            VoiceRealtimeFrame,
            ProjectionSessionScopeGAgent<VoiceRealtimeProjectionContext>>(
            scopeKey => new VoiceRealtimeProjectionContext
            {
                SessionId = scopeKey.SessionId,
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new VoiceRealtimeRuntimeLease(context));
        services.TryAddSingleton<IProjectionSessionEventCodec<VoiceRealtimeFrame>, VoiceRealtimeSessionEventCodec>();
        services.TryAddSingleton<IProjectionSessionEventHub<VoiceRealtimeFrame>, ProjectionSessionEventHub<VoiceRealtimeFrame>>();
        services.TryAddSingleton<VoiceRealtimeProjectionPort>();
        services.TryAddSingleton<IVoiceRealtimeProjectionPort>(sp =>
            sp.GetRequiredService<VoiceRealtimeProjectionPort>());
        services.TryAddSingleton<
            IProjectionDocumentMetadataProvider<VoicePresenceCapabilityReadModel>,
            VoicePresenceCapabilityReadModelMetadataProvider>();
        services.AddProjectionMaterializationRuntimeCore<
            VoicePresenceCapabilityMaterializationContext,
            VoicePresenceCapabilityMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<VoicePresenceCapabilityMaterializationContext>>(
            scopeKey => new VoicePresenceCapabilityMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new VoicePresenceCapabilityMaterializationRuntimeLease(context));
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            VoicePresenceCommittedStateProjectionActivationPlanProvider>());
        services.AddCurrentStateProjectionMaterializer<
            VoicePresenceCapabilityMaterializationContext,
            VoicePresenceCapabilityReadModelProjector>();
        services.TryAddSingleton<
            IVoicePresenceCapabilityProjectionRecoveryPort,
            VoicePresenceCapabilityProjectionRecoveryPort>();
        return services;
    }
}
