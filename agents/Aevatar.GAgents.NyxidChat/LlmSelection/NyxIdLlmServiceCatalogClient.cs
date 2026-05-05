using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class NyxIdLlmServiceCatalogClient : INyxIdLlmServiceCatalogClient
{
    private readonly NyxIdApiClient _nyxClient;
    private readonly ILogger<NyxIdLlmServiceCatalogClient> _logger;

    public NyxIdLlmServiceCatalogClient(
        NyxIdApiClient nyxClient,
        ILogger<NyxIdLlmServiceCatalogClient>? logger = null)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
        _logger = logger ?? NullLogger<NyxIdLlmServiceCatalogClient>.Instance;
    }

    public async Task<NyxIdLlmServicesResult> GetServicesAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var response = await _nyxClient.GetLlmServicesAsync(accessToken, ct).ConfigureAwait(false);
        var result = NyxIdLlmServiceCatalogParser.ParseServicesResult(response);
        return await MergeProxyRouteCandidatesAsync(result, accessToken, ct).ConfigureAwait(false);
    }

    public async Task<UserLlmSetupHint> GetSetupHintAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct)
    {
        var result = await GetServicesAsync(query, accessToken, ct).ConfigureAwait(false);
        return result.SetupHint ?? new UserLlmSetupHint(string.Empty, []);
    }

    public async Task<NyxIdLlmService> ProvisionAsync(
        UserLlmSelectionContext context,
        string accessToken,
        string provisionEndpointId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionEndpointId);

        var response = await _nyxClient
            .ProvisionLlmServiceAsync(accessToken, provisionEndpointId, ct)
            .ConfigureAwait(false);
        return NyxIdLlmServiceCatalogParser.ParseProvisionedService(response);
    }

    private async Task<NyxIdLlmServicesResult> MergeProxyRouteCandidatesAsync(
        NyxIdLlmServicesResult result,
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            var proxyServices = await _nyxClient.DiscoverProxyServicesAsync(accessToken, ct).ConfigureAwait(false);
            return NyxIdLlmServiceCatalogParser.MergeProxyRouteCandidates(result, proxyServices);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to merge NyxID proxy services into LLM route catalog");
            return result;
        }
    }
}
