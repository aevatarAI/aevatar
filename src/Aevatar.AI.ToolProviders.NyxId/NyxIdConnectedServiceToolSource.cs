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
        var executionToken = context?.Credentials.NyxIdAccessToken;
        var inventoryToken = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(context?.Credentials)
                             ?? executionToken;
        if (string.IsNullOrWhiteSpace(executionToken) || string.IsNullOrWhiteSpace(inventoryToken))
            return [];

        try
        {
            var discovered = await _client.DiscoverAsync(
                inventoryToken,
                AgentToolRequestContext.NyxIdOrgToken,
                ct);
            var bindings = discovered
                .Where(static binding =>
                    NyxIdServiceInstanceClient.IsCallerExecutable(binding.Instance))
                .ToArray();
            if (bindings.Length == 0)
                return [];

            var catalog = await ReadMcpCatalogAsync(executionToken, ct);
            if (catalog is null)
                return [];

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
            var tools = catalog.Services
                .Where(service => HasExactRouteBinding(service, bindingsById))
                .SelectMany(service => service.Endpoints
                    .Where(endpoint => endpoint.IsReadOnly ||
                        _options.EnableAssistantConnectedServiceEffects)
                    .Select(endpoint =>
                    NyxIdConnectedServiceOperationToolFactory.Create(
                        proxy,
                        service,
                        endpoint,
                        catalog.Source.ContentDigest,
                        bindingsById[service.UserServiceId].Instance,
                        NyxIdAssistantReadinessCapabilityRegistry.Resolve(
                            _options,
                            bindingsById[service.UserServiceId].Instance.CatalogServiceSlug),
                        NyxIdAssistantOperationReadBackRegistry.Resolve(
                            _options,
                            service,
                            endpoint,
                            catalog.Source.ContentDigest,
                            bindingsById[service.UserServiceId].Instance))))
                .Where(static tool => tool is not null)
                .Select(static tool => tool!)
                .Where(static tool => !string.IsNullOrWhiteSpace(tool.Name))
                .GroupBy(static tool => tool.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.Count() == 1)
                .Select(static group => group.First())
                .ToArray();
            _logger.LogInformation(
                "NyxID current-turn MCP discovery completed. candidateCount={CandidateCount}, descriptorCount={DescriptorCount}, rejectedCount={RejectedCount}, exposedOperationCount={ExposedOperationCount}",
                catalog.Discovery.CandidateCount,
                catalog.Discovery.Capabilities.Count,
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
            var response = await _apiClient.GetMcpConfigAsync(accessToken, ct);
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
