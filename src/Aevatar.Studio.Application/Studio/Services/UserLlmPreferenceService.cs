using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class UserLlmPreferenceService : IUserLlmPreferenceService
{
    private readonly IUserConfigQueryPort _queryPort;
    private readonly IUserConfigCommandService _commandService;
    private readonly IUserLlmCatalogPort _catalogPort;

    public UserLlmPreferenceService(
        IUserConfigQueryPort queryPort,
        IUserConfigCommandService commandService,
        IUserLlmCatalogPort catalogPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
    }

    public async Task<UserLlmOptionsView> GetOptionsAsync(string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return UserLlmOptionsView.Empty;

        var result = await _catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
        var config = await _queryPort.GetAsync(ct).ConfigureAwait(false);
        var route = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute);
        var current = result.Services
            .Select(NyxIdLlmServiceMapping.ToOption)
            .FirstOrDefault(option => string.Equals(option.RouteValue, route, StringComparison.OrdinalIgnoreCase));
        if (current is not null && !string.IsNullOrWhiteSpace(config.DefaultModel))
            current = current with { DefaultModel = config.DefaultModel.Trim() };

        return new UserLlmOptionsView(
            current,
            result.Services.Select(NyxIdLlmServiceMapping.ToOption).ToArray(),
            result.SetupHint);
    }

    public async Task<UserConfig> SaveAsync(
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var current = await _queryPort.GetAsync(ct).ConfigureAwait(false);
        UserConfig next;
        if (command.Reset == true)
        {
            next = current with
            {
                DefaultModel = string.Empty,
                PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
            };
        }
        else if (!string.IsNullOrWhiteSpace(command.ServiceId))
        {
            var options = await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false);
            var option = FindLlmOption(options, command.ServiceId!);
            if (option is null)
                throw new InvalidOperationException($"LLM service '{command.ServiceId}' is not routable for this user.");

            EnsureSelectable(option);
            next = current with
            {
                PreferredLlmRoute = UserConfigLlmRoute.Normalize(option.RouteValue),
                DefaultModel = NormalizeOptional(command.Model) ?? option.DefaultModel ?? string.Empty,
            };
        }
        else if (!string.IsNullOrWhiteSpace(command.RouteValue))
        {
            var routeValue = UserConfigLlmRoute.Normalize(command.RouteValue);
            if (string.Equals(routeValue, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase))
            {
                next = current with
                {
                    PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
                    DefaultModel = NormalizeOptional(command.Model) ?? current.DefaultModel,
                };
            }
            else
            {
                var options = await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false);
                var option = FindLlmOption(options, routeValue);
                if (option is null)
                    throw new InvalidOperationException($"LLM route '{command.RouteValue}' is not routable for this user.");

                EnsureSelectable(option);
                next = current with
                {
                    PreferredLlmRoute = UserConfigLlmRoute.Normalize(option.RouteValue),
                    DefaultModel = NormalizeOptional(command.Model) ?? option.DefaultModel ?? string.Empty,
                };
            }
        }
        else if (!string.IsNullOrWhiteSpace(command.PresetId))
        {
            var activated = await ActivatePresetAsync(bearerToken, command.PresetId!, ct).ConfigureAwait(false);
            next = current with
            {
                PreferredLlmRoute = UserConfigLlmRoute.Normalize(activated.RouteValue),
                DefaultModel = NormalizeOptional(command.Model) ?? activated.DefaultModel ?? current.DefaultModel,
            };
        }
        else if (command.Model is not null)
        {
            next = current with { DefaultModel = command.Model.Trim() };
        }
        else
        {
            throw new InvalidOperationException("Specify serviceId, presetId, model, or reset.");
        }

        await _commandService.SaveAsync(next, ct).ConfigureAwait(false);
        return next;
    }

    private async Task<IReadOnlyList<UserLlmOption>> LoadOptionsAsync(string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new InvalidOperationException("Bearer token is required to read LLM services.");

        var result = await _catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
        return result.Services.Select(NyxIdLlmServiceMapping.ToOption).ToArray();
    }

    private async Task<UserLlmOption> ActivatePresetAsync(
        string? bearerToken,
        string presetId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new InvalidOperationException("Bearer token is required to activate an LLM preset.");

        var options = await _catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
        var preset = options.SetupHint?.Presets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, presetId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            throw new InvalidOperationException($"LLM preset '{presetId}' is not available.");

        return preset.Activation switch
        {
            UseExistingService existing => ActivateExistingPreset(
                options.Services.Select(NyxIdLlmServiceMapping.ToOption).ToArray(),
                existing),
            ProvisionThenUse provisioning => await ActivateProvisioningPresetAsync(
                bearerToken,
                provisioning.ProvisionEndpointId,
                ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported LLM preset activation for '{preset.Id}'."),
        };
    }

    private static UserLlmOption ActivateExistingPreset(
        IReadOnlyList<UserLlmOption> services,
        UseExistingService existing)
    {
        var option = services.FirstOrDefault(candidate =>
            string.Equals(candidate.ServiceId, existing.ServiceId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.RouteValue, existing.RouteValue, StringComparison.OrdinalIgnoreCase));
        if (option is null)
            throw new InvalidOperationException($"LLM service '{existing.ServiceId}' is not routable for this user.");

        EnsureSelectable(option);
        return option with { DefaultModel = existing.DefaultModel ?? option.DefaultModel };
    }

    private async Task<UserLlmOption> ActivateProvisioningPresetAsync(
        string bearerToken,
        string provisionEndpointId,
        CancellationToken ct)
    {
        var result = NyxIdLlmServiceMapping.ToOption(
            await _catalogPort.ProvisionAsync(bearerToken, provisionEndpointId, ct).ConfigureAwait(false));
        EnsureSelectable(result);
        return result;
    }

    private static UserLlmOption? FindLlmOption(IReadOnlyList<UserLlmOption> options, string requested)
    {
        var normalized = requested.Trim();
        return options.FirstOrDefault(option =>
            string.Equals(option.ServiceId, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.ServiceSlug, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.DisplayName, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.RouteValue, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureSelectable(UserLlmOption option)
    {
        if (!option.Allowed)
            throw new InvalidOperationException($"LLM service '{option.DisplayName}' is not allowed for this user.");

        if (!string.Equals(option.Status, "ready", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"LLM service '{option.DisplayName}' is not ready: {option.Status}.");
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
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
