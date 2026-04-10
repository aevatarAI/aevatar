using Aevatar.AI.Abstractions.ToolProviders;
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
        // Memory cache for webhook dedup (volatile — Phase 2 migrates to durable)
        services.AddMemoryCache();

        // Projection pipeline shared infrastructure
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        // ─── Device Registration projection pipeline ───
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

        // ─── Channel Bot Registration projection pipeline ───
        services.AddProjectionMaterializationRuntimeCore<
            ChannelBotRegistrationMaterializationContext,
            ChannelBotRegistrationMaterializationRuntimeLease,
            ProjectionMaterializationScopeGAgent<ChannelBotRegistrationMaterializationContext>>(
            static scopeKey => new ChannelBotRegistrationMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            static context => new ChannelBotRegistrationMaterializationRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            ChannelBotRegistrationMaterializationContext,
            ChannelBotRegistrationProjector>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ChannelBotRegistrationDocument>,
            ChannelBotRegistrationDocumentMetadataProvider>();
        services.TryAddSingleton<IChannelBotRegistrationQueryPort, ChannelBotRegistrationQueryPort>();

        // Register platform adapters (add more as platforms are onboarded)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlatformAdapter, LarkPlatformAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlatformAdapter, TelegramPlatformAdapter>());

        // Register channel_registrations tool so NyxIdChatGAgent can manage registrations
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, ChannelRegistrationToolSource>());

        return services;
    }
}
