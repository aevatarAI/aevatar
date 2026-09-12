using System.Text.Json;
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
    private const int CustomOpenApiMaxBytes = 128 * 1024;

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

    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.EffectiveTransportBaseUrl))
            return [];
        var context = AgentToolRequestContext.Current;
        if (context is null)
            return [];

        var executionToken = context.Credentials.NyxIdAccessToken;
        var inventoryToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context.Credentials)
                             ?? executionToken;
        if (string.IsNullOrWhiteSpace(executionToken) || string.IsNullOrWhiteSpace(inventoryToken))
            return [];

        try
        {
            var credentialKind = context.Credentials.NyxIdCredentialKind;
            NyxIdMcpCatalogRead? catalog;
            IReadOnlyList<NyxIdServiceInstanceBinding> bindings;
            if (credentialKind == AgentToolNyxIdCredentialKind.AgentKey)
            {
                bindings = ReadAgentKeySelectorBindings(context, executionToken);
                if (bindings.Count == 0)
                    return [];
                catalog = null;
            }
            else
            {
                bindings = (await _client.DiscoverAsync(
                            inventoryToken,
                            AgentToolRequestContext.NyxIdOrgToken,
                            ct)
                        .ConfigureAwait(false))
                    .Where(static binding =>
                        NyxIdServiceInstanceClient.IsCallerExecutable(binding.Instance))
                    .ToArray();
                if (bindings.Count == 0)
                    return [];
                catalog = await ReadMcpCatalogAsync(executionToken, ct);
                if (catalog is null)
                    return [];
            }
            if (bindings.Count == 0)
                return [];
            var catalogServiceIds = catalog?.Services
                .Select(static service => service.UserServiceId)
                .ToHashSet(StringComparer.Ordinal) ?? [];
            var customOpenApiServices = credentialKind == AgentToolNyxIdCredentialKind.AgentKey
                ? await ReadAgentKeyOpenApiServicesAsync(bindings, ct).ConfigureAwait(false)
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
            var services = (catalog?.Services ?? []).Concat(customOpenApiServices).ToArray();
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
                catalog?.Discovery.CandidateCount ?? customOpenApiServices.Count,
                (catalog?.Discovery.Capabilities.Count ?? 0) +
                customOpenApiServices.Sum(static service => service.Endpoints.Count),
                catalog?.Discovery.RejectedCount ?? 0,
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

    private static IReadOnlyList<NyxIdServiceInstanceBinding> ReadAgentKeySelectorBindings(
        AgentToolExecutionContext context,
        string agentKey)
    {
        if (string.IsNullOrWhiteSpace(context.ConnectedServices.ContextJson))
            return [];
        try
        {
            using var document = JsonDocument.Parse(context.ConnectedServices.ContextJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("nyxid_service_selectors", out var selectors) ||
                selectors.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return selectors.EnumerateArray()
                .Select(selector => ReadAgentKeySelectorBinding(selector, agentKey))
                .Where(static binding => binding is not null)
                .Select(static binding => binding!)
                .GroupBy(static binding => binding.Instance.DisplaySlug, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static NyxIdServiceInstanceBinding? ReadAgentKeySelectorBinding(JsonElement selector, string agentKey)
    {
        if (selector.ValueKind != JsonValueKind.Object ||
            !selector.TryGetProperty("service_slug", out var slugElement) ||
            slugElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var serviceSlug = Normalize(slugElement.GetString());
        if (serviceSlug is null || serviceSlug.Contains('/') || serviceSlug.Contains('\\'))
            return null;
        var instance = new NyxIdServiceInstance
        {
            UserServiceId = BuildAgentKeySyntheticServiceId(serviceSlug),
            DisplaySlug = serviceSlug,
            Label = serviceSlug,
            IsActive = true,
            CredentialAllowed = true,
            AccessTokenSource = NyxIdServiceAccessTokenSource.User,
            CredentialSource = NyxIdServiceCredentialSource.Personal,
            CallerExecutionReadiness = new NyxIdServiceCallerExecutionReadiness
            {
                Connected = true,
                CredentialStatus = NyxIdServiceCredentialStatus.Active,
                NodeStatus = NyxIdServiceNodeStatus.NotBound,
            },
            RouteConstraint = new NyxIdProxyRouteConstraint { ServiceSlug = serviceSlug },
        };
        instance.CatalogServiceSlug = serviceSlug;
        return new NyxIdServiceInstanceBinding(instance, agentKey);
    }

    private async Task<IReadOnlyList<NyxIdMcpService>> ReadAgentKeyOpenApiServicesAsync(
        IReadOnlyList<NyxIdServiceInstanceBinding> bindings,
        CancellationToken ct)
    {
        var services = new List<NyxIdMcpService>();
        foreach (var binding in bindings)
        {
            foreach (var catalogSpecSlug in EnumerateCatalogSpecSlugs(binding.Instance.DisplaySlug))
            {
                try
                {
                    var response = await _apiClient.GetCatalogOpenApiSpecAsync(
                        binding.AccessToken,
                        catalogSpecSlug,
                        ct).ConfigureAwait(false);
                    var parsed = NyxIdMcpOperationCatalog.ParseCustomOpenApi(
                        response,
                        binding.Instance,
                        $"agent-key:{binding.Instance.DisplaySlug}",
                        DateTimeOffset.UtcNow,
                        CatalogFreshnessWindow);
                    foreach (var diagnostic in parsed.Discovery.Diagnostics)
                    {
                        _logger.LogInformation(
                            "NyxID Agent Key OpenAPI discovery diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                            diagnostic.Code,
                            diagnostic.Count);
                    }
                    if (parsed.Services.Count > 0)
                    {
                        services.AddRange(parsed.Services);
                        break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    _logger.LogWarning(
                        "NyxID Agent Key OpenAPI discovery diagnostic. code={DiagnosticCode}, count={DiagnosticCount}",
                        ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable,
                        1);
                }
            }
        }

        return services;
    }

    private static IEnumerable<string> EnumerateCatalogSpecSlugs(string serviceSlug)
    {
        yield return serviceSlug;
        const string apiPrefix = "api-";
        if (serviceSlug.StartsWith(apiPrefix, StringComparison.OrdinalIgnoreCase) &&
            serviceSlug.Length > apiPrefix.Length)
        {
            yield return serviceSlug[apiPrefix.Length..];
        }
    }

    private static string? Normalize(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    internal static string BuildAgentKeySyntheticServiceId(string serviceSlug) =>
        "agent-key:" + serviceSlug;

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
