using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.Foundation.Core.EventSourcing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.VoicePresence.Projection;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: voice capability reads were registered without the production materializer that writes them.
//   New principle: voice presence registers its current-state materializer with the shared Projection Pipeline.
public static class VoicePresenceProjectionServiceCollectionExtensions
{
    public static IServiceCollection AddVoicePresenceCapabilityProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
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
        return services;
    }
}
