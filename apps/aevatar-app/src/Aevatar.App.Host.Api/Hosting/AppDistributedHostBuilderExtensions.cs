using Aevatar.Foundation.Runtime.Hosting;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using System.Net;
using System.Net.Sockets;

namespace Aevatar.App.Host.Api.Hosting;

public static class AppDistributedHostBuilderExtensions
{
    public static WebApplicationBuilder AddAppDistributedOrleansHost(
        this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var runtimeOptions = ResolveRuntimeOptions(builder.Configuration);
        if (!string.Equals(
                runtimeOptions.Provider,
                AevatarActorRuntimeOptions.ProviderOrleans,
                StringComparison.OrdinalIgnoreCase))
            return builder;

        var hostOptions = ResolveOrleansHostOptions(builder.Configuration);

        builder.Host.UseOrleans(siloBuilder =>
        {
            ConfigureClustering(siloBuilder, hostOptions);

            siloBuilder.AddAevatarFoundationRuntimeOrleans(o =>
            {
                o.StreamBackend = runtimeOptions.OrleansStreamBackend;
                o.StreamProviderName = runtimeOptions.OrleansStreamProviderName;
                o.ActorEventNamespace = runtimeOptions.OrleansActorEventNamespace;
                o.PersistenceBackend = runtimeOptions.OrleansPersistenceBackend;
                o.GarnetConnectionString = runtimeOptions.OrleansGarnetConnectionString;
                o.GarnetEventStoreKeyPrefix = runtimeOptions.OrleansGarnetEventStoreKeyPrefix;
                o.QueueCount = hostOptions.QueueCount;
                o.QueueCacheSize = hostOptions.QueueCacheSize;
            });

            if (string.Equals(
                    runtimeOptions.OrleansStreamBackend,
                    AevatarActorRuntimeOptions.OrleansStreamBackendMassTransitAdapter,
                    StringComparison.OrdinalIgnoreCase))
                siloBuilder.AddAevatarFoundationRuntimeOrleansMassTransitAdapter();
        });

        return builder;
    }

    private static void ConfigureClustering(
        ISiloBuilder siloBuilder, OrleansHostOptions hostOptions)
    {
        if (string.Equals(
                hostOptions.ClusteringMode,
                OrleansHostOptions.ClusteringModeLocalhost,
                StringComparison.OrdinalIgnoreCase))
        {
            siloBuilder.UseLocalhostClustering(
                siloPort: hostOptions.SiloPort,
                gatewayPort: hostOptions.GatewayPort,
                primarySiloEndpoint: TryParseEndpoint(hostOptions.PrimarySiloEndpoint),
                serviceId: hostOptions.ServiceId,
                clusterId: hostOptions.ClusterId);
            return;
        }

        if (string.Equals(
                hostOptions.ClusteringMode,
                OrleansHostOptions.ClusteringModeDevelopment,
                StringComparison.OrdinalIgnoreCase))
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

    private static AevatarActorRuntimeOptions ResolveRuntimeOptions(
        IConfiguration configuration)
    {
        var options = new AevatarActorRuntimeOptions();
        var section = AevatarActorRuntimeOptions.SectionName;

        Bind(configuration[$"{section}:Provider"], v => options.Provider = v);
        Bind(configuration[$"{section}:OrleansStreamBackend"], v => options.OrleansStreamBackend = v);
        Bind(configuration[$"{section}:OrleansStreamProviderName"], v => options.OrleansStreamProviderName = v);
        Bind(configuration[$"{section}:OrleansActorEventNamespace"], v => options.OrleansActorEventNamespace = v);
        Bind(configuration[$"{section}:OrleansPersistenceBackend"], v => options.OrleansPersistenceBackend = v);
        Bind(configuration[$"{section}:OrleansGarnetConnectionString"], v => options.OrleansGarnetConnectionString = v);
        Bind(configuration[$"{section}:OrleansGarnetEventStoreKeyPrefix"], v => options.OrleansGarnetEventStoreKeyPrefix = v);
        return options;
    }

    private static OrleansHostOptions ResolveOrleansHostOptions(
        IConfiguration configuration)
    {
        var options = new OrleansHostOptions();

        Bind(configuration["Orleans:ClusteringMode"], v => options.ClusteringMode = v);
        Bind(configuration["Orleans:ClusterId"], v => options.ClusterId = v);
        Bind(configuration["Orleans:ServiceId"], v => options.ServiceId = v);
        Bind(configuration["Orleans:SiloHost"], v => options.SiloHost = v);
        Bind(configuration["Orleans:PrimarySiloEndpoint"], v => options.PrimarySiloEndpoint = v);

        if (int.TryParse(configuration["Orleans:SiloPort"], out var siloPort) && siloPort > 0)
            options.SiloPort = siloPort;
        if (int.TryParse(configuration["Orleans:GatewayPort"], out var gatewayPort) && gatewayPort > 0)
            options.GatewayPort = gatewayPort;
        if (int.TryParse(configuration["Orleans:QueueCount"], out var queueCount) && queueCount > 0)
            options.QueueCount = queueCount;
        if (int.TryParse(configuration["Orleans:QueueCacheSize"], out var queueCacheSize) && queueCacheSize > 0)
            options.QueueCacheSize = queueCacheSize;
        if (bool.TryParse(configuration["Orleans:ListenOnAnyHostAddress"], out var listenOnAny))
            options.ListenOnAnyHostAddress = listenOnAny;

        return options;
    }

    private static void Bind(string? value, Action<string> setter)
    {
        if (!string.IsNullOrWhiteSpace(value)) setter(value);
    }

    private static IPEndPoint? TryParseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0 || idx == endpoint.Length - 1)
            throw new InvalidOperationException(
                $"Invalid Orleans endpoint '{endpoint}'. Expected host:port.");

        var host = endpoint[..idx].Trim();
        if (!int.TryParse(endpoint[(idx + 1)..].Trim(), out var port) || port <= 0)
            throw new InvalidOperationException(
                $"Invalid Orleans endpoint port in '{endpoint}'.");

        return new IPEndPoint(ResolveHostAddress(host), port);
    }

    private static IPAddress ResolveHostAddress(string host)
    {
        if (IPAddress.TryParse(host, out var parsed)) return parsed;

        var addresses = Dns.GetHostAddresses(host);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new InvalidOperationException(
                   $"Unable to resolve Orleans host '{host}'.");
    }

    private sealed class OrleansHostOptions
    {
        public const string ClusteringModeLocalhost = "Localhost";
        public const string ClusteringModeDevelopment = "Development";

        public string ClusteringMode { get; set; } = ClusteringModeLocalhost;
        public string ClusterId { get; set; } = "app-cluster";
        public string ServiceId { get; set; } = "app-host-api";
        public string SiloHost { get; set; } = "127.0.0.1";
        public string? PrimarySiloEndpoint { get; set; }
        public int SiloPort { get; set; } = 11111;
        public int GatewayPort { get; set; } = 30000;
        public int QueueCount { get; set; } = 8;
        public int QueueCacheSize { get; set; } = 4096;
        public bool ListenOnAnyHostAddress { get; set; }
    }
}
