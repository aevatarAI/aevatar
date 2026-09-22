using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdConnectedServiceToolSource : IAgentToolSource
{
    private static readonly TimeSpan CatalogFreshnessWindow = TimeSpan.FromMinutes(5);
    // Published platform documents include multiple capabilities; keep their transport bounded.
    private const int CustomOpenApiMaxBytes = 1024 * 1024;

    private readonly NyxIdToolOptions _options;
    private readonly NyxIdApiClient _apiClient;
    private readonly NyxIdServiceInstanceClient _client;
    private readonly NyxIdDelegationTokenLease _delegationTokenLease;
    private readonly ILogger _logger;
    private readonly INyxIdProxyFileArtifactIngress? _fileArtifactIngress;

    public NyxIdConnectedServiceToolSource(
        NyxIdToolOptions options,
        NyxIdApiClient apiClient,
        NyxIdServiceInstanceClient client,
        ILogger<NyxIdConnectedServiceToolSource>? logger = null,
        INyxIdProxyFileArtifactIngress? fileArtifactIngress = null,
        NyxIdDelegationTokenLease? delegationTokenLease = null)
    {
        _options = options;
        _apiClient = apiClient;
        _client = client;
        _delegationTokenLease = delegationTokenLease ?? new NyxIdDelegationTokenLease(apiClient);
        _logger = logger ?? NullLogger<NyxIdConnectedServiceToolSource>.Instance;
        _fileArtifactIngress = fileArtifactIngress;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        DiscoverToolsAsync(catalogServiceSlug: null, ct);

    /// <summary>Limits remote contract discovery to caller-visible instances of an authoritative catalog service.</summary>
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsForCatalogServiceAsync(
        string catalogServiceSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogServiceSlug);
        return DiscoverToolsAsync(catalogServiceSlug, ct);
    }

    private async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(
        string? catalogServiceSlug, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.EffectiveTransportBaseUrl))
        {
            _logger.LogInformation(
                "NyxID connected-service discovery skipped. reason={Reason} catalogServiceSlug={CatalogServiceSlug}",
                "transport_base_url_missing",
                catalogServiceSlug ?? string.Empty);
            return [];
        }

        var context = AgentToolRequestContext.Current;
        if (context is null)
        {
            _logger.LogInformation(
                "NyxID connected-service discovery skipped. reason={Reason} catalogServiceSlug={CatalogServiceSlug}",
                "request_context_missing",
                catalogServiceSlug ?? string.Empty);
            return [];
        }

        var executionToken = context.Credentials.NyxIdAccessToken;
        var inventoryToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context.Credentials)
                             ?? executionToken;
        if (string.IsNullOrWhiteSpace(executionToken) || string.IsNullOrWhiteSpace(inventoryToken))
        {
            _logger.LogInformation(
                "NyxID connected-service discovery skipped. reason={Reason} credentialKind={CredentialKind} catalogServiceSlug={CatalogServiceSlug} hasExecutionToken={HasExecutionToken} hasInventoryToken={HasInventoryToken}",
                "credential_missing",
                context.Credentials.NyxIdCredentialKind,
                catalogServiceSlug ?? string.Empty,
                !string.IsNullOrWhiteSpace(executionToken),
                !string.IsNullOrWhiteSpace(inventoryToken));
            return [];
        }

        try
        {
            var credentialKind = context.Credentials.NyxIdCredentialKind;
            _logger.LogInformation(
                "NyxID connected-service discovery started. credentialKind={CredentialKind} catalogServiceSlug={CatalogServiceSlug}",
                credentialKind,
                catalogServiceSlug ?? string.Empty);
            var discoveredBindings = credentialKind == AgentToolNyxIdCredentialKind.AgentKey
                ? await _client.DiscoverAgentKeyAsync(executionToken, ct).ConfigureAwait(false)
                : await _client.DiscoverAsync(
                    inventoryToken,
                    AgentToolRequestContext.NyxIdOrgToken,
                    ct).ConfigureAwait(false);
            var bindings = discoveredBindings
                .Where(binding =>
                    NyxIdServiceInstanceClient.IsCallerExecutable(binding.Instance) &&
                    MatchesCatalogService(binding.Instance, catalogServiceSlug))
                .ToArray();
            _logger.LogInformation(
                "NyxID caller connected-service bindings filtered. credentialKind={CredentialKind} catalogServiceSlug={CatalogServiceSlug} discoveredBindingCount={DiscoveredBindingCount} filteredBindingCount={FilteredBindingCount} filteredSlugs={FilteredSlugs}",
                credentialKind,
                catalogServiceSlug ?? string.Empty,
                discoveredBindings.Count,
                bindings.Length,
                string.Join(',', bindings.Select(static binding => binding.Instance.DisplaySlug).Order(StringComparer.OrdinalIgnoreCase)));
            if (bindings.Length == 0)
            {
                _logger.LogInformation(
                    "NyxID connected-service discovery skipped. reason={Reason} credentialKind={CredentialKind} catalogServiceSlug={CatalogServiceSlug} discoveredBindingCount={DiscoveredBindingCount}",
                    "caller_executable_binding_missing",
                    credentialKind,
                    catalogServiceSlug ?? string.Empty,
                    discoveredBindings.Count);
                return [];
            }

            var catalog = await ReadMcpCatalogAsync(executionToken, ct).ConfigureAwait(false);
            if (catalog is null ||
                credentialKind == AgentToolNyxIdCredentialKind.AgentKey &&
                (catalog.AccessDenied || catalog.SourceUnavailable))
            {
                _logger.LogInformation(
                    "NyxID connected-service discovery skipped. reason={Reason} credentialKind={CredentialKind} catalogServiceSlug={CatalogServiceSlug} bindingCount={BindingCount}",
                    catalog?.AccessDenied == true ? "mcp_catalog_access_denied" : "mcp_catalog_unavailable",
                    credentialKind,
                    catalogServiceSlug ?? string.Empty,
                    bindings.Length);
                return [];
            }
            var catalogServiceIds = catalog.Services
                .Select(static service => service.UserServiceId)
                .ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<NyxIdMcpService> customOpenApiServices = credentialKind == AgentToolNyxIdCredentialKind.AgentKey
                ? []
                : await ReadCustomOpenApiServicesAsync(
                    bindings,
                    catalogServiceIds,
                    ct).ConfigureAwait(false);

            var bindingsById = bindings.ToDictionary(
                static binding => binding.Instance.UserServiceId,
                StringComparer.Ordinal);
            var proxy = new NyxIdProxyTool(
                _apiClient,
                _logger,
                _fileArtifactIngress,
                _options.EffectiveProxyFileArtifactMaxBytes,
                _options.ManagedWorkflowAdmissionMode,
                _delegationTokenLease);
            var services = catalog.Services.Concat(customOpenApiServices).ToArray();
            _logger.LogInformation(
                "NyxID connected-service contracts resolved. credentialKind={CredentialKind} catalogServiceSlug={CatalogServiceSlug} bindingCount={BindingCount} catalogServiceCount={CatalogServiceCount} customOpenApiServiceCount={CustomOpenApiServiceCount} contractServiceCount={ContractServiceCount} serviceSlugs={ServiceSlugs}",
                credentialKind,
                catalogServiceSlug ?? string.Empty,
                bindings.Length,
                catalog.Services.Count,
                customOpenApiServices.Count,
                services.Length,
                string.Join(',', services.Select(static service => service.ServiceSlug).Where(static slug => !string.IsNullOrWhiteSpace(slug)).Order(StringComparer.OrdinalIgnoreCase)));
            var tools = services
                .Where(service => HasExactRouteBinding(service, bindingsById))
                .SelectMany(service => service.Endpoints
                    .Where(endpoint => endpoint.IsReadOnly ||
                        _options.EnableAssistantConnectedServiceEffects)
                    .Select(endpoint =>
                    NyxIdConnectedServiceOperationToolFactory.Create(
                        proxy,
                        service,
                        endpoint,
                        service.Source.ContentDigest,
                        bindingsById[service.UserServiceId].Instance,
                        NyxIdAssistantReadinessCapabilityRegistry.Resolve(
                            _options,
                            bindingsById[service.UserServiceId].Instance.CatalogServiceSlug),
                        NyxIdAssistantOperationReadBackRegistry.Resolve(
                            _options,
                            service,
                            endpoint,
                            service.Source.ContentDigest,
                            bindingsById[service.UserServiceId].Instance))))
                .Where(static tool => tool is not null)
                .Select(static tool => tool!)
                .Where(static tool => !string.IsNullOrWhiteSpace(tool.Name))
                .GroupBy(static tool => tool.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() == 1)
                .Select(static group => group.First())
                .ToArray();
            _logger.LogInformation(
                "NyxID current-turn connected-service discovery completed. candidateCount={CandidateCount}, descriptorCount={DescriptorCount}, rejectedCount={RejectedCount}, exposedOperationCount={ExposedOperationCount}",
                catalog.Discovery.CandidateCount,
                catalog.Discovery.Capabilities.Count +
                customOpenApiServices.Sum(static service => service.Endpoints.Count),
                catalog.Discovery.RejectedCount,
                tools.Length);
            return tools;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "NyxID connected-service discovery diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable,
                1);
            return [];
        }
    }

    private static bool MatchesCatalogService(NyxIdServiceInstance instance, string? catalogServiceSlug) =>
        catalogServiceSlug is null ||
        string.Equals(instance.CatalogServiceSlug, catalogServiceSlug, StringComparison.Ordinal);

    // Recognize previously frozen admissions; new catalogs use exact UserService IDs.
    internal static bool IsAgentKeySyntheticServiceId(string? serviceId) =>
        serviceId?.StartsWith("agent-key:", StringComparison.Ordinal) == true;

    private async Task<IReadOnlyList<NyxIdMcpService>> ReadCustomOpenApiServicesAsync(
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings,
        IReadOnlySet<string> catalogServiceIds,
        CancellationToken ct)
    {
        var services = new List<NyxIdMcpService>();
        foreach (var binding in bindings)
        {
            if (catalogServiceIds.Contains(binding.Instance.UserServiceId) ||
                string.IsNullOrWhiteSpace(binding.Instance.OpenapiSpecUrl) ||
                !TryBuildCustomOpenApiProxyPath(binding.Instance, out var proxyPath))
            {
                continue;
            }

            try
            {
                var response = await _apiClient.ProxyRequestBoundedAsync(
                    binding.AccessToken,
                    binding.Instance.DisplaySlug,
                    binding.Instance.UserServiceId,
                    proxyPath,
                    HttpMethod.Get.Method,
                    body: null,
                    extraHeaders: null,
                    CustomOpenApiMaxBytes,
                    ct);
                if (!response.Succeeded)
                    continue;
                var parsed = NyxIdMcpOperationCatalog.ParseCustomOpenApi(
                    response.Content,
                    binding.Instance,
                    $"caller-custom:{binding.Instance.UserServiceId}",
                    DateTimeOffset.UtcNow,
                    CatalogFreshnessWindow);
                foreach (var diagnostic in parsed.Discovery.Diagnostics)
                {
                    _logger.LogInformation(
                        "NyxID custom OpenAPI discovery diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                        diagnostic.Code,
                        diagnostic.Count);
                }
                services.AddRange(parsed.Services);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                _logger.LogWarning(
                    "NyxID custom OpenAPI discovery diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                    ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable,
                    1);
            }
        }

        return services;
    }

    private static bool TryBuildCustomOpenApiProxyPath(
        NyxIdServiceInstance instance,
        out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(instance.OpenapiSpecUrl))
            return false;
        if (!Uri.TryCreate(instance.OpenapiSpecUrl.Trim(), UriKind.RelativeOrAbsolute, out var openApiUri))
            return false;

        if (openApiUri.IsAbsoluteUri)
        {
            if (!Uri.TryCreate(instance.EndpointUrl, UriKind.Absolute, out var endpointUri) ||
                !string.Equals(openApiUri.Scheme, endpointUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(openApiUri.Host, endpointUri.Host, StringComparison.OrdinalIgnoreCase) ||
                openApiUri.Port != endpointUri.Port ||
                !string.IsNullOrEmpty(openApiUri.Fragment))
            {
                return false;
            }

            path = openApiUri.PathAndQuery;
        }
        else
        {
            path = instance.OpenapiSpecUrl.Trim();
            if (!path.StartsWith("/", StringComparison.Ordinal))
                path = "/" + path;
        }

        return IsSafeCustomOpenApiProxyPath(path);
    }

    private static bool IsSafeCustomOpenApiProxyPath(string path)
    {
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        var resourcePath = queryIndex >= 0 ? path[..queryIndex] : path;
        return resourcePath is { Length: > 0 } &&
               resourcePath[0] == '/' &&
               !resourcePath.StartsWith("//", StringComparison.Ordinal) &&
               !resourcePath.Contains("..", StringComparison.Ordinal) &&
               !resourcePath.Contains('\\', StringComparison.Ordinal) &&
               !path.Contains('#', StringComparison.Ordinal) &&
               !path.Any(char.IsControl);
    }

    private static bool HasExactRouteBinding(
        NyxIdMcpService service,
        IReadOnlyDictionary<string, NyxIdServiceInstanceBinding> bindingsById) =>
        bindingsById.TryGetValue(service.UserServiceId, out var binding) &&
        !string.IsNullOrWhiteSpace(binding.Instance.DisplaySlug) &&
        !string.IsNullOrWhiteSpace(service.ServiceSlug) &&
        string.Equals(
            binding.Instance.DisplaySlug,
            service.ServiceSlug,
            StringComparison.Ordinal);

    private async Task<NyxIdMcpCatalogRead?> ReadMcpCatalogAsync(
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            var response = await _apiClient.GetMcpConfigAsync(accessToken, ct).ConfigureAwait(false);
            var catalog = NyxIdMcpOperationCatalog.Parse(
                response,
                "caller",
                DateTimeOffset.UtcNow,
                CatalogFreshnessWindow);
            foreach (var diagnostic in catalog.Discovery.Diagnostics)
            {
                _logger.LogInformation(
                    "NyxID current-turn MCP discovery diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                    diagnostic.Code,
                    diagnostic.Count);
            }
            return catalog;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "NyxID current-turn MCP discovery diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable,
                1);
            return null;
        }
    }
}
