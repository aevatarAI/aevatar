using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class UserLlmPreferenceService : IUserLlmPreferenceService
{
    private const string DefaultGatewayRouteLabel = "NyxID Gateway";

    private readonly IUserConfigQueryPort _queryPort;
    private readonly IUserLlmCatalogPort _catalogPort;
    private readonly UserLlmSettingsViewBuilder _viewBuilder;

    public UserLlmPreferenceService(
        IUserConfigQueryPort queryPort,
        IUserLlmCatalogPort catalogPort,
        IOptions<UserLlmSettingsOptions>? options = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
        var gatewayRouteLabel = UserLlmPreferenceWriteCore.NormalizeOptional(options?.Value.GatewayRouteLabel) ??
                                DefaultGatewayRouteLabel;
        _viewBuilder = new UserLlmSettingsViewBuilder(gatewayRouteLabel);
    }

    public async Task<UserLlmSettingsView> GetSettingsAsync(string? bearerToken, CancellationToken ct)
    {
        var config = await _queryPort.GetAsync(ct).ConfigureAwait(false);
        var savedRoute = config.PreferredLlmRoute?.Trim() ?? string.Empty;
        var defaultModel = config.DefaultModel?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(bearerToken))
            return _viewBuilder.BuildUnavailable(config.LlmSelection, savedRoute, defaultModel);

        try
        {
            var result = await _catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
            return _viewBuilder.BuildAvailable(result, config.LlmSelection, savedRoute, defaultModel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return _viewBuilder.BuildUnavailable(config.LlmSelection, savedRoute, defaultModel);
        }
    }
}
