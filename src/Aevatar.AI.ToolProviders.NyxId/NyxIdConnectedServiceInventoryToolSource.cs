using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Request-local, read-only view of the caller's exact NyxID connected-service
/// instances. This narrow source is safe for the default chat surface; mutation,
/// routing, generic request, and dynamic operation tools remain in the explicit
/// <c>nyxid.connected_services</c> tool set.
/// </summary>
public sealed class NyxIdConnectedServiceInventoryToolSource : IAgentToolSource
{
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdServiceInstanceClient _client;
    private readonly ILogger _logger;

    public NyxIdConnectedServiceInventoryToolSource(
        NyxIdToolOptions options,
        NyxIdServiceInstanceClient client,
        ILogger<NyxIdConnectedServiceInventoryToolSource>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<NyxIdConnectedServiceInventoryToolSource>.Instance;
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
            var bindings = (await _client
                    .DiscoverAsync(userToken, AgentToolRequestContext.NyxIdOrgToken, ct)
                    .ConfigureAwait(false))
                .Where(static binding =>
                    binding.Instance.IsActive &&
                    binding.Instance.CredentialAllowed)
                .ToArray();
            return [NyxIdServiceTools.CreateInventory(bindings)];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID connected-service inventory discovery failed");
            return [];
        }
    }
}
