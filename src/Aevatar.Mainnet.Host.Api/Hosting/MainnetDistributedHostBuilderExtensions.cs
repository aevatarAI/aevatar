using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit.DependencyInjection;
using Orleans.Hosting;

namespace Aevatar.Mainnet.Host.Api.Hosting;

public static class MainnetDistributedHostBuilderExtensions
{
    public static WebApplicationBuilder AddMainnetDistributedOrleansHost(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var runtimeOptions = ResolveRuntimeOptions(builder.Configuration);
        if (!string.Equals(runtimeOptions.Provider, AevatarActorRuntimeOptions.ProviderOrleans, StringComparison.OrdinalIgnoreCase))
            return builder;

        var hostOptions = ResolveOrleansHostOptions(builder.Configuration);

        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseLocalhostClustering(
                siloPort: hostOptions.SiloPort,
                gatewayPort: hostOptions.GatewayPort,
                serviceId: hostOptions.ServiceId,
                clusterId: hostOptions.ClusterId);

            siloBuilder.AddAevatarFoundationRuntimeOrleans(orleansOptions =>
            {
                orleansOptions.StreamBackend = runtimeOptions.OrleansStreamBackend;
                orleansOptions.StreamProviderName = runtimeOptions.OrleansStreamProviderName;
                orleansOptions.ActorEventNamespace = runtimeOptions.OrleansActorEventNamespace;
                orleansOptions.QueueCount = hostOptions.QueueCount;
                orleansOptions.QueueCacheSize = hostOptions.QueueCacheSize;
            });

            if (string.Equals(runtimeOptions.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendMassTransitAdapter, StringComparison.OrdinalIgnoreCase))
                siloBuilder.AddAevatarFoundationRuntimeOrleansMassTransitAdapter();
        });

        return builder;
    }

    private static AevatarActorRuntimeOptions ResolveRuntimeOptions(IConfiguration configuration)
    {
        var options = new AevatarActorRuntimeOptions();

        var configuredProvider = configuration[$"{AevatarActorRuntimeOptions.SectionName}:Provider"];
        if (!string.IsNullOrWhiteSpace(configuredProvider))
            options.Provider = configuredProvider;

        var configuredStreamBackend = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamBackend"];
        if (!string.IsNullOrWhiteSpace(configuredStreamBackend))
            options.OrleansStreamBackend = configuredStreamBackend;

        var configuredStreamProviderName = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansStreamProviderName"];
        if (!string.IsNullOrWhiteSpace(configuredStreamProviderName))
            options.OrleansStreamProviderName = configuredStreamProviderName;

        var configuredActorEventNamespace = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansActorEventNamespace"];
        if (!string.IsNullOrWhiteSpace(configuredActorEventNamespace))
            options.OrleansActorEventNamespace = configuredActorEventNamespace;

        return options;
    }

    private static OrleansHostOptions ResolveOrleansHostOptions(IConfiguration configuration)
    {
        var options = new OrleansHostOptions();

        var configuredClusterId = configuration["Orleans:ClusterId"];
        if (!string.IsNullOrWhiteSpace(configuredClusterId))
            options.ClusterId = configuredClusterId;

        var configuredServiceId = configuration["Orleans:ServiceId"];
        if (!string.IsNullOrWhiteSpace(configuredServiceId))
            options.ServiceId = configuredServiceId;

        var configuredSiloPort = configuration["Orleans:SiloPort"];
        if (int.TryParse(configuredSiloPort, out var siloPort) && siloPort > 0)
            options.SiloPort = siloPort;

        var configuredGatewayPort = configuration["Orleans:GatewayPort"];
        if (int.TryParse(configuredGatewayPort, out var gatewayPort) && gatewayPort > 0)
            options.GatewayPort = gatewayPort;

        var configuredQueueCount = configuration["Orleans:QueueCount"];
        if (int.TryParse(configuredQueueCount, out var queueCount) && queueCount > 0)
            options.QueueCount = queueCount;

        var configuredQueueCacheSize = configuration["Orleans:QueueCacheSize"];
        if (int.TryParse(configuredQueueCacheSize, out var queueCacheSize) && queueCacheSize > 0)
            options.QueueCacheSize = queueCacheSize;

        return options;
    }

    private sealed class OrleansHostOptions
    {
        public string ClusterId { get; set; } = "aevatar-mainnet-cluster";

        public string ServiceId { get; set; } = "aevatar-mainnet-host-api";

        public int SiloPort { get; set; } = 11111;

        public int GatewayPort { get; set; } = 30000;

        public int QueueCount { get; set; } = 8;

        public int QueueCacheSize { get; set; } = 4096;
    }
}
