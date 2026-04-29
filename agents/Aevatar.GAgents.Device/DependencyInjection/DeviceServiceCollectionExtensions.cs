using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Maintenance;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Device;

/// <summary>
/// DI registration entry point for the device registration package.
/// </summary>
public static class DeviceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Device Registration projection pipeline (materialization runtime,
    /// projector, query port, document metadata, startup service, and projection store).
    /// Pass <paramref name="configuration"/> so the document projection store matches the
    /// host environment (Elasticsearch in prod, InMemory for local dev / tests).
    /// </summary>
    public static IServiceCollection AddDeviceRegistration(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The helper logs a misconfiguration warning (Console.Error during SCE
        // composition; structured log when a real logger is wired in tests) when
        // configuration is present but Endpoints/Enabled are both empty, so
        // operators see the InMemory fallback at startup.
        var useElasticsearch = ElasticsearchProjectionConfiguration.IsEnabled(
            configuration,
            storeName: "DeviceRegistration");

        // ─── Retired-actor cleanup contribution ───
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRetiredActorSpec, DeviceRetiredActorSpec>());

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
        services.TryAddSingleton<DeviceRegistrationProjectionPort>();
        services.AddHostedService<DeviceRegistrationStartupService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITombstoneCompactionTarget, DeviceTombstoneCompactionTarget>());

        if (useElasticsearch)
        {
            services.AddElasticsearchDocumentProjectionStore<DeviceRegistrationDocument, string>(
                optionsFactory: _ => ElasticsearchProjectionConfiguration.BindOptions(configuration!),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<DeviceRegistrationDocument>>().Metadata,
                keySelector: static doc => doc.Id,
                keyFormatter: static key => key);
        }
        else
        {
            services.AddInMemoryDocumentProjectionStore<DeviceRegistrationDocument, string>(
                static doc => doc.Id, static key => key);
        }

        return services;
    }

}
