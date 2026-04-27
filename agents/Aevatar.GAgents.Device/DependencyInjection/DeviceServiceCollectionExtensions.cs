using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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

        var useElasticsearch = ResolveElasticsearchEnabled(configuration);

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
                optionsFactory: _ => BuildElasticsearchOptions(configuration!),
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

    /// <summary>
    /// Detects whether Elasticsearch is the projection store. Mirrors the same logic as
    /// the channel runtime: explicit Enabled=true, or auto-detect from Endpoints presence.
    /// When configuration is null (unit tests), falls back to InMemory.
    /// </summary>
    private static bool ResolveElasticsearchEnabled(IConfiguration? configuration)
    {
        if (configuration == null) return false;

        var section = configuration.GetSection("Projection:Document:Providers:Elasticsearch");
        var explicitEnabled = section["Enabled"];
        if (!string.IsNullOrWhiteSpace(explicitEnabled))
            return string.Equals(explicitEnabled.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        var hasEndpoints = section.GetSection("Endpoints").GetChildren()
            .Any(x => !string.IsNullOrWhiteSpace(x.Value));

        if (!hasEndpoints)
        {
            Console.Error.WriteLine(
                "[WARN] DeviceRegistration: Elasticsearch not configured — using volatile InMemory projection store. " +
                "Registration data will be lost on restart. Set Projection:Document:Providers:Elasticsearch:Enabled=true for production.");
        }

        return hasEndpoints;
    }

    private static Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration.ElasticsearchProjectionDocumentStoreOptions
        BuildElasticsearchOptions(IConfiguration configuration)
    {
        var options = new Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration.ElasticsearchProjectionDocumentStoreOptions();
        configuration.GetSection("Projection:Document:Providers:Elasticsearch").Bind(options);
        return options;
    }
}
