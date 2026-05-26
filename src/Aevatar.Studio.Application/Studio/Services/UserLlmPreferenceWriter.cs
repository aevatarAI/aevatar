using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class UserLlmPreferenceWriter
{
    private readonly IUserConfigQueryPort _queryPort;
    private readonly IUserConfigCommandService _commandService;
    private readonly IUserLlmCatalogPort _catalogPort;

    public UserLlmPreferenceWriter(
        IUserConfigQueryPort queryPort,
        IUserConfigCommandService commandService,
        IUserLlmCatalogPort catalogPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
    }

    public async Task<UserConfig> SaveAsync(
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var current = await _queryPort.GetAsync(ct).ConfigureAwait(false);
        var next = await MergePreferenceCommandAsync(current, bearerToken, command, ct).ConfigureAwait(false);
        await _commandService.SaveAsync(next, ct).ConfigureAwait(false);
        return next;
    }

    public async Task<UserConfig> MergeLegacyFieldsAsync(
        string? bearerToken,
        UserConfig current,
        string? defaultModel,
        string? preferredLlmRoute,
        CancellationToken ct)
    {
        var requestedModel = NormalizeOptional(defaultModel);
        var requestedRoute = preferredLlmRoute is null
            ? current.PreferredLlmRoute
            : UserConfigLlmRoute.Normalize(preferredLlmRoute);

        if (requestedModel is { } model &&
            UserConfigLlmModel.TryParseRouteModel(model) is { } prefixed &&
            string.IsNullOrWhiteSpace(requestedRoute) &&
            await TryResolveRouteModelAsync(bearerToken, prefixed.RouteSlug, ct).ConfigureAwait(false) is { } option)
        {
            return current with
            {
                PreferredLlmRoute = UserConfigLlmRoute.Normalize(option.RouteValue),
                DefaultModel = prefixed.Model,
            };
        }

        return current with
        {
            PreferredLlmRoute = requestedRoute,
            DefaultModel = defaultModel is null ? current.DefaultModel : requestedModel ?? string.Empty,
        };
    }

    private async Task<UserConfig> MergePreferenceCommandAsync(
        UserConfig current,
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct)
    {
        if (command.Reset == true)
        {
            return current with
            {
                DefaultModel = string.Empty,
                PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
            };
        }

        if (!string.IsNullOrWhiteSpace(command.ServiceId))
        {
            var options = await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false);
            var option = FindLlmOption(options, command.ServiceId!);
            if (option is null)
                throw new InvalidOperationException($"LLM service '{command.ServiceId}' is not routable for this user.");

            EnsureSelectable(option);
            return current with
            {
                PreferredLlmRoute = UserConfigLlmRoute.Normalize(option.RouteValue),
                DefaultModel = NormalizeModelForRoute(NormalizeOptional(command.Model), option) ??
                               option.DefaultModel ??
                               string.Empty,
            };
        }

        if (!string.IsNullOrWhiteSpace(command.RouteValue))
        {
            var routeValue = UserConfigLlmRoute.Normalize(command.RouteValue);
            if (string.Equals(routeValue, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase))
            {
                return current with
                {
                    PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
                    DefaultModel = NormalizeOptional(command.Model) ?? current.DefaultModel,
                };
            }

            var options = await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false);
            var option = FindLlmOption(options, routeValue);
            if (option is null)
                throw new InvalidOperationException($"LLM route '{command.RouteValue}' is not routable for this user.");

            EnsureSelectable(option);
            return current with
            {
                PreferredLlmRoute = UserConfigLlmRoute.Normalize(option.RouteValue),
                DefaultModel = NormalizeModelForRoute(NormalizeOptional(command.Model), option) ??
                               option.DefaultModel ??
                               string.Empty,
            };
        }

        if (!string.IsNullOrWhiteSpace(command.PresetId))
        {
            var activated = await ActivatePresetAsync(bearerToken, command.PresetId!, ct).ConfigureAwait(false);
            return current with
            {
                PreferredLlmRoute = UserConfigLlmRoute.Normalize(activated.RouteValue),
                DefaultModel = NormalizeModelForRoute(NormalizeOptional(command.Model), activated) ??
                               activated.DefaultModel ??
                               current.DefaultModel,
            };
        }

        if (command.Model is not null)
            return await MergeModelOnlyAsync(current, bearerToken, command.Model, ct).ConfigureAwait(false);

        throw new InvalidOperationException("Specify serviceId, presetId, model, or reset.");
    }

    private async Task<UserConfig> MergeModelOnlyAsync(
        UserConfig current,
        string? bearerToken,
        string model,
        CancellationToken ct)
    {
        var normalized = model.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return current with { DefaultModel = string.Empty };

        if (UserConfigLlmModel.TryParseRouteModel(normalized) is not { } prefixed)
            return current with { DefaultModel = normalized };

        var options = await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false);
        var option = FindLlmOption(options, prefixed.RouteSlug);
        if (option is null)
            throw new InvalidOperationException($"LLM service '{prefixed.RouteSlug}' is not routable for this user.");

        EnsureSelectable(option);
        return current with
        {
            PreferredLlmRoute = UserConfigLlmRoute.Normalize(option.RouteValue),
            DefaultModel = prefixed.Model,
        };
    }

    private async Task<IReadOnlyList<UserLlmOption>> LoadOptionsAsync(string? bearerToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new InvalidOperationException("Bearer token is required to read LLM services.");

        var result = await _catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
        return result.Services.Select(NyxIdLlmServiceMapping.ToOption).ToArray();
    }

    private async Task<UserLlmOption?> TryResolveRouteModelAsync(
        string? bearerToken,
        string routeSlug,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            return null;

        var options = await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false);
        var option = FindLlmOption(options, routeSlug);
        if (option is null)
            return null;

        EnsureSelectable(option);
        return option;
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

    private static string? NormalizeModelForRoute(string? model, UserLlmOption option)
    {
        if (model is null)
            return null;

        if (UserConfigLlmModel.TryParseRouteModel(model) is { } prefixed &&
            IsSameOption(option, prefixed.RouteSlug))
        {
            return prefixed.Model;
        }

        return model;
    }

    private static UserLlmOption? FindLlmOption(IReadOnlyList<UserLlmOption> options, string requested)
    {
        var normalized = requested.Trim();
        return options.FirstOrDefault(option => IsSameOption(option, normalized));
    }

    private static bool IsSameOption(UserLlmOption option, string requested) =>
        string.Equals(option.ServiceId, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.ServiceSlug, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.DisplayName, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.RouteValue, UserConfigLlmRoute.Normalize(requested), StringComparison.OrdinalIgnoreCase);

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
