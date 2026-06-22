using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Authentication.Abstractions;
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
        var options = new NyxIdToolOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.AddHttpClient<NyxIdApiClient>();
        services.TryAddSingleton<INyxIdApiClientFactory, HttpClientFactoryNyxIdApiClientFactory>();
        services.AddHttpClient(ConnectedServiceSpecCache.HttpClientName, _ => { });
        services.TryAddSingleton<IConnectedServiceSpecSource, ConnectedServiceSpecCache>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IWorkflowConnectedServiceFileSubmitAdapter,
            NyxIdWorkflowConnectedServiceFileSubmitAdapter>());
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(IWorkflowFileIngressPort)))
        {
            services.TryAddSingleton<INyxIdProxyFileArtifactIngress>(sp =>
                new NyxIdProxyWorkflowFileArtifactIngress(
                    sp.GetRequiredService<IWorkflowFileIngressPort>()));
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Transient<IAgentToolSource, NyxIdAgentToolSource>());

        // Refactor (iter23/cluster-001-nyxid-tool-approval-polling):
        //   Old pattern: NyxID was registered as a generic local approval handler that blocked while polling.
        //   New principle: NyxID is a remote submit/status port; local approval/yield remains host-owned.
        services.TryAddTransient<IRemoteToolApprovalPort, NyxIdRemoteToolApprovalPort>();

        return services;
    }

    // 06-20-observatory-admin-cross-scope (G3/G8): registers the NyxID-backed platform-admin authorizer + user
    //   directory behind their provider-agnostic interfaces, plus the per-token decision cache. Requires
    //   AddNyxIdTools to have registered NyxIdApiClient (the typed client backing INyxIdUserReadApi).
    //   Authorizer/directory are scoped (per-request); the IMemoryCache that backs the decision cache is the
    //   singleton, so positive decisions are shared across requests within the TTL.
    public static IServiceCollection AddNyxIdPlatformAuthorization(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<ObservatoryAdminAuthorizationOptions>? configure = null)
    {
        services.AddMemoryCache();
        var optionsBuilder = services.AddOptions<ObservatoryAdminAuthorizationOptions>();
        if (configuration is not null)
            optionsBuilder.Bind(configuration.GetSection(ObservatoryAdminAuthorizationOptions.ConfigSection));
        if (configure is not null)
            optionsBuilder.Configure(configure);

        services.TryAddTransient<INyxIdUserReadApi>(static sp => sp.GetRequiredService<NyxIdApiClient>());
        services.TryAddScoped<IPlatformAdminAuthorizer, NyxIdPlatformAdminAuthorizer>();
        services.TryAddScoped<IPlatformUserDirectory, NyxIdPlatformUserDirectory>();
        return services;
    }
}
