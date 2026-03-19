using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit.DependencyInjection;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using Orleans.Configuration;
using Orleans.Hosting;
using System.Net;
using System.Net.Sockets;

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
            ConfigureClustering(siloBuilder, hostOptions);

            siloBuilder.AddAevatarFoundationRuntimeOrleans(orleansOptions =>
            {
                orleansOptions.StreamBackend = runtimeOptions.OrleansStreamBackend;
                orleansOptions.StreamProviderName = runtimeOptions.OrleansStreamProviderName;
                orleansOptions.ActorEventNamespace = runtimeOptions.OrleansActorEventNamespace;
                orleansOptions.PersistenceBackend = runtimeOptions.OrleansPersistenceBackend;
                orleansOptions.GarnetConnectionString = runtimeOptions.OrleansGarnetConnectionString;
                orleansOptions.QueueCount = hostOptions.QueueCount;
                orleansOptions.QueueCacheSize = hostOptions.QueueCacheSize;
            });

            if (string.Equals(runtimeOptions.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendMassTransitAdapter, StringComparison.OrdinalIgnoreCase))
            {
                siloBuilder.ConfigureServices(services =>
                {
                    services.AddAevatarMassTransitStreamProvider(streamOptions =>
                    {
                        streamOptions.StreamNamespace = runtimeOptions.OrleansActorEventNamespace;
                    });
                });
                siloBuilder.AddAevatarFoundationRuntimeOrleansMassTransitAdapter();
            }

            if (string.Equals(runtimeOptions.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendKafkaStrictProvider, StringComparison.OrdinalIgnoreCase))
            {
                siloBuilder.ConfigureServices(services =>
                {
                    services.AddAevatarFoundationRuntimeOrleansKafkaStrictProviderTransport(options =>
                    {
                        options.BootstrapServers = runtimeOptions.MassTransitKafkaBootstrapServers;
                        options.TopicName = runtimeOptions.MassTransitKafkaTopicName;
                        options.ConsumerGroup = runtimeOptions.MassTransitKafkaConsumerGroup;
                        options.TopicPartitionCount = hostOptions.QueueCount;
                    });
                });
            }
        });

        return builder;
    }

    private static void ConfigureClustering(ISiloBuilder siloBuilder, OrleansHostOptions hostOptions)
    {
        if (string.Equals(hostOptions.ClusteringMode, OrleansHostOptions.ClusteringModeLocalhost, StringComparison.OrdinalIgnoreCase))
        {
            siloBuilder.UseLocalhostClustering(
                siloPort: hostOptions.SiloPort,
                gatewayPort: hostOptions.GatewayPort,
                primarySiloEndpoint: TryParseEndpoint(hostOptions.PrimarySiloEndpoint),
                serviceId: hostOptions.ServiceId,
                clusterId: hostOptions.ClusterId);
            return;
        }

        if (string.Equals(hostOptions.ClusteringMode, OrleansHostOptions.ClusteringModeDevelopment, StringComparison.OrdinalIgnoreCase))
        {
            var primarySiloEndpoint = TryParseEndpoint(hostOptions.PrimarySiloEndpoint);
            var advertisedIp = ResolveHostAddress(hostOptions.SiloHost);

            siloBuilder.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = hostOptions.ClusterId;
                options.ServiceId = hostOptions.ServiceId;
            });
            siloBuilder.ConfigureEndpoints(
                advertisedIP: advertisedIp,
                siloPort: hostOptions.SiloPort,
                gatewayPort: hostOptions.GatewayPort,
                listenOnAnyHostAddress: hostOptions.ListenOnAnyHostAddress);
            siloBuilder.UseDevelopmentClustering(options =>
            {
                options.PrimarySiloEndpoint = primarySiloEndpoint;
            });
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported Orleans clustering mode '{hostOptions.ClusteringMode}'.");
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
        var configuredPersistenceBackend = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansPersistenceBackend"];
        if (!string.IsNullOrWhiteSpace(configuredPersistenceBackend))
            options.OrleansPersistenceBackend = configuredPersistenceBackend;
        var configuredGarnetConnectionString = configuration[$"{AevatarActorRuntimeOptions.SectionName}:OrleansGarnetConnectionString"];
        if (!string.IsNullOrWhiteSpace(configuredGarnetConnectionString))
            options.OrleansGarnetConnectionString = configuredGarnetConnectionString;
        var configuredKafkaBootstrapServers = configuration[$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaBootstrapServers"];
        if (!string.IsNullOrWhiteSpace(configuredKafkaBootstrapServers))
            options.MassTransitKafkaBootstrapServers = configuredKafkaBootstrapServers;
        var configuredKafkaTopicName = configuration[$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaTopicName"];
        if (!string.IsNullOrWhiteSpace(configuredKafkaTopicName))
            options.MassTransitKafkaTopicName = configuredKafkaTopicName;
        var configuredKafkaConsumerGroup = configuration[$"{AevatarActorRuntimeOptions.SectionName}:MassTransitKafkaConsumerGroup"];
        if (!string.IsNullOrWhiteSpace(configuredKafkaConsumerGroup))
            options.MassTransitKafkaConsumerGroup = configuredKafkaConsumerGroup;

        return options;
    }

    private static OrleansHostOptions ResolveOrleansHostOptions(IConfiguration configuration)
    {
        var options = new OrleansHostOptions();

        var configuredClusteringMode = configuration["Orleans:ClusteringMode"];
        if (!string.IsNullOrWhiteSpace(configuredClusteringMode))
            options.ClusteringMode = configuredClusteringMode;

        var configuredClusterId = configuration["Orleans:ClusterId"];
        if (!string.IsNullOrWhiteSpace(configuredClusterId))
            options.ClusterId = configuredClusterId;

        var configuredServiceId = configuration["Orleans:ServiceId"];
        if (!string.IsNullOrWhiteSpace(configuredServiceId))
            options.ServiceId = configuredServiceId;

        var configuredSiloHost = configuration["Orleans:SiloHost"];
        if (!string.IsNullOrWhiteSpace(configuredSiloHost))
            options.SiloHost = configuredSiloHost;

        var configuredPrimarySiloEndpoint = configuration["Orleans:PrimarySiloEndpoint"];
        if (!string.IsNullOrWhiteSpace(configuredPrimarySiloEndpoint))
            options.PrimarySiloEndpoint = configuredPrimarySiloEndpoint;

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

        var configuredListenOnAnyHostAddress = configuration["Orleans:ListenOnAnyHostAddress"];
        if (bool.TryParse(configuredListenOnAnyHostAddress, out var listenOnAnyHostAddress))
            options.ListenOnAnyHostAddress = listenOnAnyHostAddress;

        return options;
    }

    private static IPEndPoint? TryParseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var separatorIndex = endpoint.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == endpoint.Length - 1)
            throw new InvalidOperationException(
                $"Invalid Orleans endpoint '{endpoint}'. Expected format is host:port.");

        var host = endpoint[..separatorIndex].Trim();
        var portLiteral = endpoint[(separatorIndex + 1)..].Trim();
        if (!int.TryParse(portLiteral, out var port) || port <= 0)
            throw new InvalidOperationException(
                $"Invalid Orleans endpoint port in '{endpoint}'.");

        return new IPEndPoint(ResolveHostAddress(host), port);
    }

    private static IPAddress ResolveHostAddress(string host)
    {
        if (IPAddress.TryParse(host, out var parsed))
            return parsed;

        var addresses = Dns.GetHostAddresses(host);
        var address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault();
        if (address == null)
            throw new InvalidOperationException($"Unable to resolve Orleans host '{host}'.");

        return address;
    }

    private sealed class OrleansHostOptions
    {
        public const string ClusteringModeLocalhost = "Localhost";
        public const string ClusteringModeDevelopment = "Development";

        public string ClusteringMode { get; set; } = ClusteringModeLocalhost;

        public string ClusterId { get; set; } = "aevatar-mainnet-cluster";

        public string ServiceId { get; set; } = "aevatar-mainnet-host-api";

        public string SiloHost { get; set; } = "127.0.0.1";

        public string? PrimarySiloEndpoint { get; set; }

        public int SiloPort { get; set; } = 11111;

        public int GatewayPort { get; set; } = 30000;

        public int QueueCount { get; set; } = 8;

        public int QueueCacheSize { get; set; } = 4096;

        public bool ListenOnAnyHostAddress { get; set; }
    }
}
