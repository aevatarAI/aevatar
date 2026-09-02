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

        if (string.IsNullOrWhiteSpace(bearerToken))
            return _viewBuilder.BuildVerificationUnavailable(config);

        try
        {
            var result = await _catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
            return _viewBuilder.BuildAvailable(result, config);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return _viewBuilder.BuildVerificationUnavailable(config);
        }
    }
}
