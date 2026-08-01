using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Hosting.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider.DependencyInjection;
using Microsoft.Extensions.Configuration;
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

        builder.Configuration.AddJsonFile(
            Path.Combine(AppContext.BaseDirectory, "appsettings.Distributed.json"),
            optional: true,
            reloadOnChange: false);

        // Re-add environment variables so they take precedence over Distributed.json.
        // CreateBuilder and AddAevatarConfig register env var sources before this method,
        // but Distributed.json (loaded above) would shadow them without this re-add.
        // Both prefixed (AEVATAR_ActorRuntime__*, AEVATAR_Orleans__*) and bare
        // (Projection__*, ASPNETCORE_ENVIRONMENT) are used by CI/cluster scripts.
        builder.Configuration.AddEnvironmentVariables("AEVATAR_");
        builder.Configuration.AddEnvironmentVariables();

        var runtimeOptions = ResolveRuntimeOptions(builder.Configuration);
        builder.Services.AddAevatarRuntimeSecretStores(runtimeOptions);
        if (!string.Equals(runtimeOptions.Provider, AevatarActorRuntimeOptions.ProviderOrleans, StringComparison.OrdinalIgnoreCase))
            return builder;

        var hostOptions = ResolveOrleansHostOptions(builder.Configuration);

        builder.Host.UseOrleans(siloBuilder =>
        {
            ConfigureClustering(siloBuilder, hostOptions, runtimeOptions.OrleansGarnetConnectionString);

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

            if (string.Equals(runtimeOptions.OrleansStreamBackend, AevatarActorRuntimeOptions.OrleansStreamBackendKafkaProvider, StringComparison.OrdinalIgnoreCase))
            {
                siloBuilder.ConfigureServices(services =>
                {
                    services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(options =>
                    {
                        options.BootstrapServers = runtimeOptions.KafkaBootstrapServers;
                        options.TopicName = runtimeOptions.KafkaTopicName;
                        options.ConsumerGroup = runtimeOptions.KafkaConsumerGroup;
                        options.TopicPartitionCount = hostOptions.QueueCount;
                        options.ReceiverBufferCapacity = runtimeOptions.KafkaReceiverBufferCapacity;
                        options.ReceiverBufferHighWatermark = runtimeOptions.KafkaReceiverBufferHighWatermark;
                        options.ReceiverBufferLowWatermark = runtimeOptions.KafkaReceiverBufferLowWatermark;
                    });
                });
            }
        });

        return builder;
    }

    private static void ConfigureClustering(
        ISiloBuilder siloBuilder,
        OrleansHostOptions hostOptions,
        string garnetConnectionString)
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
            var advertisedIp = ResolveHostAddress(
                string.IsNullOrWhiteSpace(hostOptions.SiloHost) ? "127.0.0.1" : hostOptions.SiloHost);

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

        if (string.Equals(hostOptions.ClusteringMode, OrleansHostOptions.ClusteringModeGarnet, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(garnetConnectionString))
                throw new InvalidOperationException(
                    "Orleans Garnet clustering requires 'ActorRuntime:OrleansGarnetConnectionString'.");

            siloBuilder.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = hostOptions.ClusterId;
                options.ServiceId = hostOptions.ServiceId;
            });
            ConfigureGarnetClusterEndpoints(siloBuilder, hostOptions);
            // Membership must live in the same Garnet store (and ServiceId) as the
            // reminder table and grain state: silos that overlap during a rolling
            // deploy then join one cluster and partition the reminder ring, instead
            // of running as two single-silo clusters that each fire every reminder
            // and ping-pong grain-state etags until the old pod dies.
            siloBuilder.UseRedisClustering(redisOptions => redisOptions.ConfigurationOptions =
                StackExchange.Redis.ConfigurationOptions.Parse(garnetConnectionString));
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported Orleans clustering mode '{hostOptions.ClusteringMode}'.");
    }

    private static void ConfigureGarnetClusterEndpoints(ISiloBuilder siloBuilder, OrleansHostOptions hostOptions)
    {
        if (string.IsNullOrWhiteSpace(hostOptions.SiloHost))
        {
            // No advertised host configured: let Orleans pick the first
            // non-loopback interface address. Inside Kubernetes that is the
            // pod IP, which peer silos can reach during a rolling deploy.
            siloBuilder.ConfigureEndpoints(
                siloPort: hostOptions.SiloPort,
                gatewayPort: hostOptions.GatewayPort,
                listenOnAnyHostAddress: hostOptions.ListenOnAnyHostAddress);
            return;
        }

        siloBuilder.ConfigureEndpoints(
            advertisedIP: ResolveHostAddress(hostOptions.SiloHost),
            siloPort: hostOptions.SiloPort,
            gatewayPort: hostOptions.GatewayPort,
            listenOnAnyHostAddress: hostOptions.ListenOnAnyHostAddress);
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
        var configuredSecretStoreBackend = configuration[$"{AevatarActorRuntimeOptions.SectionName}:SecretStoreBackend"];
        if (string.Equals(options.Provider, AevatarActorRuntimeOptions.ProviderInMemory, StringComparison.OrdinalIgnoreCase))
            options.SecretStoreBackend = AevatarActorRuntimeOptions.ProviderInMemory;
        else if (!string.IsNullOrWhiteSpace(configuredSecretStoreBackend))
            options.SecretStoreBackend = configuredSecretStoreBackend;
        var configuredSecretStoreConnectionString = configuration[$"{AevatarActorRuntimeOptions.SectionName}:SecretStoreConnectionString"];
        if (!string.IsNullOrWhiteSpace(configuredSecretStoreConnectionString))
            options.SecretStoreConnectionString = configuredSecretStoreConnectionString;
        var configuredSecretStoreDatabase = configuration[$"{AevatarActorRuntimeOptions.SectionName}:SecretStoreDatabase"];
        if (int.TryParse(configuredSecretStoreDatabase, out var secretStoreDatabase))
            options.SecretStoreDatabase = secretStoreDatabase;
        var configuredSecretStoreKeyringPath = configuration[$"{AevatarActorRuntimeOptions.SectionName}:SecretStoreKeyringPath"];
        if (!string.IsNullOrWhiteSpace(configuredSecretStoreKeyringPath))
            options.SecretStoreKeyringPath = configuredSecretStoreKeyringPath;
        var configuredSecretStoreVaultPrefix = configuration[$"{AevatarActorRuntimeOptions.SectionName}:SecretStoreVaultPrefix"];
        if (!string.IsNullOrWhiteSpace(configuredSecretStoreVaultPrefix))
            options.SecretStoreVaultPrefix = configuredSecretStoreVaultPrefix;
        var configuredSecretStoreRuntimePrefix = configuration[$"{AevatarActorRuntimeOptions.SectionName}:SecretStoreRuntimePrefix"];
        if (!string.IsNullOrWhiteSpace(configuredSecretStoreRuntimePrefix))
            options.SecretStoreRuntimePrefix = configuredSecretStoreRuntimePrefix;
        var configuredKafkaBootstrapServers = configuration[$"{AevatarActorRuntimeOptions.SectionName}:KafkaBootstrapServers"];
        if (!string.IsNullOrWhiteSpace(configuredKafkaBootstrapServers))
            options.KafkaBootstrapServers = configuredKafkaBootstrapServers;
        var configuredKafkaTopicName = configuration[$"{AevatarActorRuntimeOptions.SectionName}:KafkaTopicName"];
        if (!string.IsNullOrWhiteSpace(configuredKafkaTopicName))
            options.KafkaTopicName = configuredKafkaTopicName;
        var configuredKafkaConsumerGroup = configuration[$"{AevatarActorRuntimeOptions.SectionName}:KafkaConsumerGroup"];
        if (!string.IsNullOrWhiteSpace(configuredKafkaConsumerGroup))
            options.KafkaConsumerGroup = configuredKafkaConsumerGroup;
        options.KafkaReceiverBufferCapacity = ResolveIntSetting(
            configuration,
            nameof(AevatarActorRuntimeOptions.KafkaReceiverBufferCapacity),
            options.KafkaReceiverBufferCapacity);
        options.KafkaReceiverBufferHighWatermark = ResolveIntSetting(
            configuration,
            nameof(AevatarActorRuntimeOptions.KafkaReceiverBufferHighWatermark),
            options.KafkaReceiverBufferHighWatermark);
        options.KafkaReceiverBufferLowWatermark = ResolveIntSetting(
            configuration,
            nameof(AevatarActorRuntimeOptions.KafkaReceiverBufferLowWatermark),
            options.KafkaReceiverBufferLowWatermark);

        if (string.IsNullOrWhiteSpace(options.SecretStoreBackend))
        {
            options.SecretStoreBackend = string.Equals(
                options.Provider,
                AevatarActorRuntimeOptions.ProviderInMemory,
                StringComparison.OrdinalIgnoreCase)
                ? AevatarActorRuntimeOptions.ProviderInMemory
                : options.OrleansPersistenceBackend;
        }

        if (string.IsNullOrWhiteSpace(options.SecretStoreConnectionString) &&
            string.Equals(options.SecretStoreBackend, AevatarActorRuntimeOptions.OrleansPersistenceBackendGarnet, StringComparison.OrdinalIgnoreCase))
        {
            options.SecretStoreConnectionString = options.OrleansGarnetConnectionString;
        }

        return options;
    }

    private static int ResolveIntSetting(
        IConfiguration configuration,
        string settingName,
        int defaultValue)
    {
        var configuredValue = configuration[$"{AevatarActorRuntimeOptions.SectionName}:{settingName}"];
        if (string.IsNullOrWhiteSpace(configuredValue))
            return defaultValue;

        if (int.TryParse(configuredValue, out var value))
            return value;

        throw new FormatException(
            $"Invalid ActorRuntime:{settingName} value '{configuredValue}'.");
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
        public const string ClusteringModeGarnet = "Garnet";

        public string ClusteringMode { get; set; } = ClusteringModeLocalhost;

        public string ClusterId { get; set; } = "aevatar-mainnet-cluster";

        public string ServiceId { get; set; } = "aevatar-mainnet-host-api";

        public string SiloHost { get; set; } = string.Empty;

        public string? PrimarySiloEndpoint { get; set; }

        public int SiloPort { get; set; } = 11111;

        public int GatewayPort { get; set; } = 30000;

        public int QueueCount { get; set; } = 8;

        public int QueueCacheSize { get; set; } = 4096;

        public bool ListenOnAnyHostAddress { get; set; }
    }
}
