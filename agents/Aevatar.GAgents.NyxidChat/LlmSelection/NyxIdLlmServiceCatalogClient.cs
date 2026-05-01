using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed class NyxIdLlmServiceCatalogClient : INyxIdLlmServiceCatalogClient
{
    private readonly NyxIdApiClient _nyxClient;

    public NyxIdLlmServiceCatalogClient(NyxIdApiClient nyxClient)
    {
        _nyxClient = nyxClient ?? throw new ArgumentNullException(nameof(nyxClient));
    }

    public async Task<NyxIdLlmServicesResult> GetServicesAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var response = await _nyxClient.GetLlmServicesAsync(accessToken, ct).ConfigureAwait(false);
        return NyxIdLlmServiceCatalogParser.ParseServicesResult(response);
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
}
