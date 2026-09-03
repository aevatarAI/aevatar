using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.NyxId.ExactServiceApprovals;
using Aevatar.Authentication.Abstractions;
using Aevatar.Configuration;
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
        return AddNyxIdToolConsumers(services);
    }

    /// <summary>
    /// Registers the NyxID tool system with independently resolved internal transport, public API,
    /// and public authority settings.
    /// </summary>
    public static IServiceCollection AddNyxIdTools(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<NyxIdToolOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddNyxIdApiAccess(configuration, configure);
        return AddNyxIdToolConsumers(services);
    }

    private static IServiceCollection AddNyxIdToolConsumers(IServiceCollection services)
    {
        services.TryAddTransient<NyxIdServiceInstanceClient>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<
            IExternalWorkflowCapabilitySource,
            NyxIdExternalWorkflowCapabilitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<
            IExternalWorkflowCapabilitySource,
            NyxIdExplicitWorkflowCapabilitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<
            IExternalWorkflowCapabilitySource,
            NyxIdCodeExecutionWorkflowCapabilitySource>());
        services.TryAddTransient<NyxIdUserServiceAuthorityReader>();
        services.TryAddTransient<NyxIdUserServiceRouteConverger>();
        services.TryAddTransient<NyxIdCodeExecutionRoutePolicyReconciler>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<
            IExternalWorkflowCapabilityAdmissionPreparer,
            NyxIdCodeExecutionRouteAdmissionPreparer>());
        services.Replace(ServiceDescriptor.Singleton<
            IWorkflowFileMultipartUploadPort,
            NyxIdWorkflowFileMultipartUploadPort>());
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(IFileArtifactIngressPort)))
        {
            services.TryAddSingleton<INyxIdProxyFileArtifactIngress>(sp =>
                new NyxIdProxyWorkflowFileArtifactIngress(
                    sp.GetRequiredService<IFileArtifactIngressPort>()));
        }

        services.TryAddTransient<NyxIdAgentToolSource>();
        services.TryAddTransient<NyxIdExecutionAgentToolSource>();
        services.TryAddTransient<NyxIdWorkflowAgentToolSource>();
        services.TryAddTransient<NyxIdConnectedServiceInventoryToolSource>();
        services.TryAddTransient<NyxIdConnectedServiceToolSource>();
        services.TryAddTransient<IWorkflowInputPreferenceContextProvider>(sp =>
            sp.GetService<IAgentToolExecutionPort>() is { } toolExecutionPort
                ? new NyxIdWorkflowInputPreferenceContextProvider(
                    sp.GetRequiredService<NyxIdConnectedServiceToolSource>(),
                    toolExecutionPort)
                : EmptyWorkflowInputPreferenceContextProvider.Instance);
        services.TryAddTransient<INyxIdAdmittedOperationToolFactory,
            NyxIdAdmittedOperationToolFactory>();

        // Refactor (iter23/cluster-001-nyxid-tool-approval-polling):
        //   Old pattern: NyxID was registered as a generic local approval handler that blocked while polling.
        //   New principle: NyxID is a remote submit/status port; local approval/yield remains host-owned.
        services.TryAddTransient<IRemoteToolApprovalPort, NyxIdRemoteToolApprovalPort>();
        services.TryAddSingleton<INyxIdExactServiceApprovalPort>(sp =>
            new NyxIdExactServiceApprovalPort(
                sp.GetRequiredService<INyxIdApiClientFactory>().CreateClient()));

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

        var configuredInternalApiBaseUrl = FirstConfiguredValue(
            configuration,
            "Aevatar:NyxId:InternalApiBaseUrl");
        var configuredApiBaseUrl = FirstConfiguredValue(
            configuration,
            "Aevatar:NyxId:ApiBaseUrl");
        var configuredAuthority = FirstConfiguredValue(
            configuration,
            "Aevatar:NyxId:Authority",
            "Cli:App:NyxId:Authority",
            "Aevatar:Authentication:Authority");
        var configuredInternalFallbackTimeout = FirstConfiguredValue(
            configuration,
            NyxIdTransportFallbackPolicy.TimeoutSecondsConfigurationKey);

        if (configuredInternalApiBaseUrl is not null)
        {
            options.InternalApiBaseUrl = configuredInternalApiBaseUrl;
            options.BaseUrl = configuredInternalApiBaseUrl;
            options.ApiBaseUrl = configuredApiBaseUrl;
            options.Authority = configuredAuthority;
            options.PublicTransportFallbackBaseUrl =
                configuredApiBaseUrl is not null &&
                !UrlsEqual(configuredInternalApiBaseUrl, configuredApiBaseUrl)
                    ? configuredApiBaseUrl
                    : null;
        }
        else
        {
            var legacyPublicApiBaseUrl = configuredApiBaseUrl ?? configuredAuthority;
            if (legacyPublicApiBaseUrl is not null)
            {
                options.InternalApiBaseUrl = null;
                options.BaseUrl = legacyPublicApiBaseUrl;
                options.ApiBaseUrl = legacyPublicApiBaseUrl;
                options.Authority = configuredAuthority ?? configuredApiBaseUrl;
                options.PublicTransportFallbackBaseUrl = null;
            }
        }
        if (int.TryParse(configuredInternalFallbackTimeout, out var internalFallbackTimeoutSeconds) &&
            internalFallbackTimeoutSeconds > 0)
        {
            options.InternalApiFallbackTimeoutSeconds =
                NyxIdTransportFallbackPolicy.NormalizeTimeoutSeconds(internalFallbackTimeoutSeconds);
        }
        configure?.Invoke(options);

        if (!services.Any(static descriptor =>
                descriptor.ServiceType == typeof(NyxIdApiAccessRegistrationMarker)))
        {
            services.TryAddSingleton(new NyxIdApiClientTransportPolicy());
            // Without an explicit Timeout the typed client inherits HttpClient's 100s default,
            // which aborts long codex_exec runs (managed 180s, private SSH 300s) before their
            // own deadline can report an honest failure.
            // Resolve the options from the provider rather than closing over the local: a later
            // AddNyxIdApiAccess skips this block (the marker below is already registered) but can
            // still register a different NyxIdToolOptions instance, and the client must follow
            // whichever instance DI actually resolves.
            services.AddHttpClient<NyxIdApiClient>((provider, client) =>
                    client.Timeout = (provider.GetService<NyxIdToolOptions>() ?? new NyxIdToolOptions())
                        .EffectiveMaxRequestDuration)
                .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
                {
                    // A redirect can reach the primary before failing DNS at a second host. Keeping
                    // redirects visible guarantees DNS fallback only replays a request that never
                    // connected to its original target.
                    AllowAutoRedirect = false,
                });
            services.AddSingleton<NyxIdApiAccessRegistrationMarker>();
        }
        services.TryAddSingleton<INyxIdApiClientFactory, HttpClientFactoryNyxIdApiClientFactory>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<NyxIdDelegationTokenLease>();
        services.TryAddSingleton<NyxIdMcpOperationCatalogReader>();
        services.TryAddSingleton<INyxIdActionEvidenceReadPort>(provider =>
            new NyxIdActionEvidenceReadPort(
                provider.GetRequiredService<INyxIdApiClientFactory>(),
                provider.GetRequiredService<NyxIdMcpOperationCatalogReader>()));
        services.TryAddSingleton<INyxIdActionContinuationCredentialVisibilityPort>(provider =>
            new NyxIdActionContinuationCredentialVisibilityPort(
                provider.GetRequiredService<NyxIdMcpOperationCatalogReader>()));
        services.TryAddTransient<INyxIdUserReadApi>(static sp => sp.GetRequiredService<NyxIdApiClient>());
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

    private static bool UrlsEqual(string left, string right)
    {
        if (!Uri.TryCreate(left.TrimEnd('/') + "/", UriKind.Absolute, out var leftUri) ||
            !Uri.TryCreate(right.TrimEnd('/') + "/", UriKind.Absolute, out var rightUri))
        {
            return false;
        }

        return string.Equals(leftUri.Scheme, rightUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(leftUri.Host, rightUri.Host, StringComparison.OrdinalIgnoreCase) &&
               leftUri.Port == rightUri.Port &&
               string.Equals(leftUri.AbsolutePath, rightUri.AbsolutePath, StringComparison.Ordinal);
    }

    private sealed class EmptyWorkflowInputPreferenceContextProvider : IWorkflowInputPreferenceContextProvider
    {
        public static EmptyWorkflowInputPreferenceContextProvider Instance { get; } = new();

        public ValueTask<WorkflowInputPreferenceContext> ReadAsync(
            WorkflowInputPreferenceContextRequest request,
            CancellationToken ct = default) =>
            ValueTask.FromResult(WorkflowInputPreferenceContext.Empty);
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
