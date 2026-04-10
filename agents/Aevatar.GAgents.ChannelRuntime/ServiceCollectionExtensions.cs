using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.ChannelRuntime;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChannelRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton<ChannelBotRegistrationStore>();

        // Projection pipeline for device registrations
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.AddProjectionMaterializationRuntimeCore<
            DeviceRegistrationMaterializationContext,
            DeviceRegistrationMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<DeviceRegistrationMaterializationContext>>(
            static scopeKey => new DeviceRegistrationMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new DeviceRegistrationMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            DeviceRegistrationMaterializationContext,
            DeviceRegistrationProjector>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DeviceRegistrationDocument>,
            DeviceRegistrationDocumentMetadataProvider>();
        services.TryAddSingleton<IDeviceRegistrationQueryPort, DeviceRegistrationQueryPort>();

        // Register platform adapters (add more as platforms are onboarded)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlatformAdapter, LarkPlatformAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlatformAdapter, TelegramPlatformAdapter>());

        return services;
    }
}
