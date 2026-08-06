using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.Authentication.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>DI registration for NyxID tool provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NyxID tool system. When BaseUrl is configured, all NyxID management
    /// tools are automatically available to any AIGAgentBase-derived agent.
    /// Also registers <see cref="NyxIdRemoteToolApprovalPort"/> so agents can
    /// submit remote approval and resume from actor-owned status continuations.
    /// </summary>
    public static IServiceCollection AddNyxIdTools(
        this IServiceCollection services,
        Action<NyxIdToolOptions> configure)
    {
        // Refactor (iter25/cluster-025-nyxid-tool-discovery-actor-cache):
        //   Old pattern: NyxIdSpecCatalog + SpecFetchToken + IServiceDiscoveryCache 在仓库内建第二 catalog(NyxID 真实源的影子)
        //   New principle: NyxID 是唯一真实源;删除 in-process catalog 假权威面; routing 和 spec hints 请求时读取 live NyxID surface;保留 typed tools + live nyxid_proxy
        // Refactor (iter10/cluster-019):
        // Old: singleton tool clients constructed or pinned raw HttpClient instances.
        // New: stateless API calls use AddHttpClient<T>; stateful caches use named clients through IHttpClientFactory.
        services.AddNyxIdApiAccess(configure);
        services.TryAddTransient<NyxIdServiceInstanceClient>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<
            IExternalWorkflowCapabilitySource,
            NyxIdExternalWorkflowCapabilitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<
            IExternalWorkflowCapabilitySource,
            NyxIdExplicitWorkflowCapabilitySource>());
        services.Replace(ServiceDescriptor.Singleton<
            IWorkflowFileMultipartUploadPort,
            NyxIdWorkflowFileMultipartUploadPort>());
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(IFileArtifactIngressPort)))
        {
            services.TryAddSingleton<INyxIdProxyFileArtifactIngress>(sp =>
                new NyxIdProxyWorkflowFileArtifactIngress(
                    sp.GetRequiredService<IFileArtifactIngressPort>()));
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Transient<IAgentToolSource, NyxIdAgentToolSource>());
        services.TryAddTransient<NyxIdConnectedServiceInventoryToolSource>();

        // Refactor (iter23/cluster-001-nyxid-tool-approval-polling):
        //   Old pattern: NyxID was registered as a generic local approval handler that blocked while polling.
        //   New principle: NyxID is a remote submit/status port; local approval/yield remains host-owned.
        services.TryAddTransient<IRemoteToolApprovalPort, NyxIdRemoteToolApprovalPort>();

        return services;
    }

    /// <summary>
    /// Registers only reusable NyxID REST access. Singleton consumers should depend on
    /// <see cref="INyxIdApiClientFactory"/> and create a client for each operation.
    /// </summary>
    public static IServiceCollection AddNyxIdApiAccess(
        this IServiceCollection services,
        Action<NyxIdToolOptions>? configure = null) =>
        AddNyxIdApiAccessCore(services, null, configure);

    /// <summary>
    /// Registers reusable NyxID REST access and resolves the API base independently from
    /// the browser/OIDC authority when the canonical API base setting is present.
    /// </summary>
    public static IServiceCollection AddNyxIdApiAccess(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<NyxIdToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AddNyxIdApiAccessCore(services, configuration, configure);
    }

    private static IServiceCollection AddNyxIdApiAccessCore(
        IServiceCollection services,
        IConfiguration? configuration,
        Action<NyxIdToolOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services
            .Where(static descriptor => descriptor.ServiceType == typeof(NyxIdToolOptions))
            .Select(static descriptor => descriptor.ImplementationInstance)
            .OfType<NyxIdToolOptions>()
            .LastOrDefault() ?? new NyxIdToolOptions();
        services.RemoveAll<NyxIdToolOptions>();
        services.AddSingleton(options);

        var configuredBaseUrl = FirstConfiguredValue(
            configuration,
            "Aevatar:NyxId:ApiBaseUrl",
            "Aevatar:NyxId:Authority",
            "Cli:App:NyxId:Authority",
            "Aevatar:Authentication:Authority");
        if (configuredBaseUrl is not null)
            options.BaseUrl = configuredBaseUrl;
        configure?.Invoke(options);

        if (!services.Any(static descriptor =>
                descriptor.ServiceType == typeof(NyxIdApiAccessRegistrationMarker)))
        {
            // Without an explicit Timeout the typed client inherits HttpClient's 100s default,
            // which aborts long codex_exec runs (managed 180s, private SSH 300s) before their
            // own deadline can report an honest failure.
            // Resolve the options from the provider rather than closing over the local: a later
            // AddNyxIdApiAccess skips this block (the marker below is already registered) but can
            // still register a different NyxIdToolOptions instance, and the client must follow
            // whichever instance DI actually resolves.
            services.AddHttpClient<NyxIdApiClient>((provider, client) =>
                client.Timeout = (provider.GetService<NyxIdToolOptions>() ?? new NyxIdToolOptions())
                    .EffectiveMaxRequestDuration);
            services.AddSingleton<NyxIdApiAccessRegistrationMarker>();
        }
        services.TryAddSingleton<INyxIdApiClientFactory, HttpClientFactoryNyxIdApiClientFactory>();
        return services;
    }

    private static string? FirstConfiguredValue(
        IConfiguration? configuration,
        params string[] keys)
    {
        if (configuration is null)
            return null;

        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private sealed class NyxIdApiAccessRegistrationMarker;

    // Registers the NyxID-backed current-user resolver behind the aevatar admin authorization seam.
    public static IServiceCollection AddNyxIdPlatformAuthorization(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<ObservatoryAdminAuthorizationOptions>? configure = null)
    {
        services.AddMemoryCache();
        var optionsBuilder = services.AddOptions<ObservatoryAdminAuthorizationOptions>();
        if (configuration is not null)
        {
            // Bind the retired section first so an existing CrossScopeEnabled=false kill switch
            // remains fail-safe during deployment migration. The canonical section is applied
            // second and therefore wins when operators have explicitly moved the setting.
            optionsBuilder.Bind(configuration.GetSection(ObservatoryAdminAuthorizationOptions.LegacyConfigSection));
            optionsBuilder.Bind(configuration.GetSection(ObservatoryAdminAuthorizationOptions.ConfigSection));
        }
        if (configure is not null)
            optionsBuilder.Configure(configure);

        services.TryAddTransient<INyxIdUserReadApi>(static sp => sp.GetRequiredService<NyxIdApiClient>());
        services.TryAddScoped<IPlatformAdminAuthorizer, NyxIdPlatformAdminAuthorizer>();
        services.TryAddScoped<IPlatformUserDirectory, NyxIdPlatformUserDirectory>();
        return services;
    }
}
