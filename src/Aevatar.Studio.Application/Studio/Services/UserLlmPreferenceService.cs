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
        var savedRoute = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute);
        var defaultModel = config.DefaultModel?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(bearerToken))
            return _viewBuilder.BuildUnavailable(savedRoute, defaultModel);

        try
        {
            var result = await _catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
            (savedRoute, defaultModel) = ResolveLegacyPrefixedModel(result, savedRoute, defaultModel);
            return _viewBuilder.BuildAvailable(result, savedRoute, defaultModel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return _viewBuilder.BuildUnavailable(savedRoute, defaultModel);
        }
    }

    private static (string SavedRoute, string DefaultModel) ResolveLegacyPrefixedModel(
        NyxIdLlmServicesResult result,
        string savedRoute,
        string defaultModel)
    {
        if (!string.IsNullOrWhiteSpace(savedRoute) ||
            UserConfigLlmModel.TryParseRouteModel(defaultModel) is not { } prefixed)
        {
            return (savedRoute, defaultModel);
        }

        var prefixedOption = result.Services
            .Select(NyxIdLlmServiceMapping.ToOption)
            .FirstOrDefault(option => UserLlmPreferenceWriteCore.IsSameOption(option, prefixed.RouteSlug));
        return prefixedOption is null
            ? (savedRoute, defaultModel)
            : (UserConfigLlmRoute.Normalize(prefixedOption.RouteValue), prefixed.Model);
    }
}
