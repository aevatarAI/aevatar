using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class UserLlmPreferenceService : IUserLlmPreferenceService
{
    private readonly IUserConfigQueryPort _queryPort;
    private readonly IUserLlmCatalogPort _catalogPort;
    public UserLlmPreferenceService(
        IUserConfigQueryPort queryPort,
        IUserLlmCatalogPort catalogPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
    }

    public async Task<UserLlmOptionsView> GetOptionsAsync(string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return UserLlmOptionsView.Empty;

        var result = await _catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
        var config = await _queryPort.GetAsync(ct).ConfigureAwait(false);
        var route = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute);
        var defaultModel = config.DefaultModel.Trim();
        if (string.IsNullOrWhiteSpace(route) &&
            UserConfigLlmModel.TryParseRouteModel(defaultModel) is { } prefixed)
        {
            var prefixedOption = result.Services
                .Select(NyxIdLlmServiceMapping.ToOption)
                .FirstOrDefault(option => IsSameOption(option, prefixed.RouteSlug));
            if (prefixedOption is not null)
            {
                route = UserConfigLlmRoute.Normalize(prefixedOption.RouteValue);
                defaultModel = prefixed.Model;
            }
        }

        var current = result.Services
            .Select(NyxIdLlmServiceMapping.ToOption)
            .FirstOrDefault(option => string.Equals(option.RouteValue, route, StringComparison.OrdinalIgnoreCase));
        if (current is not null && !string.IsNullOrWhiteSpace(defaultModel))
            current = current with { DefaultModel = defaultModel };

        return new UserLlmOptionsView(
            current,
            result.Services.Select(NyxIdLlmServiceMapping.ToOption).ToArray(),
            result.SetupHint);
    }

    private static bool IsSameOption(UserLlmOption option, string requested) =>
        string.Equals(option.ServiceId, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.ServiceSlug, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.DisplayName, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.RouteValue, UserConfigLlmRoute.Normalize(requested), StringComparison.OrdinalIgnoreCase);
}

public static class NyxIdLlmServiceMapping
{
    public static UserLlmOption ToOption(NyxIdLlmService service) => new(
        ServiceId: NormalizeRequired(service.UserServiceId, nameof(service.UserServiceId)),
        ServiceSlug: NormalizeRequired(service.ServiceSlug, nameof(service.ServiceSlug)),
        DisplayName: NormalizeRequired(service.DisplayName, nameof(service.DisplayName)),
        RouteValue: NormalizeRequired(service.RouteValue, nameof(service.RouteValue)),
        DefaultModel: NormalizeOptional(service.DefaultModel),
        AvailableModels: service.Models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        Status: NormalizeRequired(service.Status, nameof(service.Status)),
        Source: NormalizeRequired(service.Source, nameof(service.Source)),
        Allowed: service.Allowed,
        Description: NormalizeOptional(service.Description));

    private static string NormalizeRequired(string value, string name)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{name} must not be empty.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
