using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
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
    private readonly ILogger _logger;

    public NyxIdConnectedServiceToolSource(
        NyxIdToolOptions options,
        NyxIdApiClient apiClient,
        NyxIdServiceInstanceClient client,
        ILogger<NyxIdConnectedServiceToolSource>? logger = null)
    {
        _options = options;
        _apiClient = apiClient;
        _client = client;
        _logger = logger ?? NullLogger<NyxIdConnectedServiceToolSource>.Instance;
    }

    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            return [];
        var userToken = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(userToken))
            return [];

        try
        {
            var discovered = await _client.DiscoverAsync(
                userToken,
                AgentToolRequestContext.NyxIdOrgToken,
                ct);
            var bindings = discovered
                .Where(static binding =>
                    binding.Instance.IsActive &&
                    binding.Instance.CredentialAllowed)
                .ToArray();
            if (bindings.Length == 0)
                return [];

            await ObserveMcpCatalogAsync(userToken, ct);
            return NyxIdServiceTools.Create(_client, bindings)
                .Where(static tool => !string.IsNullOrWhiteSpace(tool.Name))
                .GroupBy(static tool => tool.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(static group => group.All(tool => ReferenceEquals(tool, group.First())))
                .Select(static group => group.First())
                .ToArray();
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

    private async Task ObserveMcpCatalogAsync(string accessToken, CancellationToken ct)
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
            _logger.LogInformation(
                "NyxID current-turn MCP discovery completed. candidateCount={CandidateCount}, descriptorCount={DescriptorCount}, rejectedCount={RejectedCount}, exposedOperationCount={ExposedOperationCount}",
                catalog.Discovery.CandidateCount,
                catalog.Discovery.Capabilities.Count,
                catalog.Discovery.RejectedCount,
                0);
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
        }
    }
}
