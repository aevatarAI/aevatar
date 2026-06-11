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

    public async Task<UserConfigSaveReceipt> SaveAsync(
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var current = await _queryPort.GetAsync(ct).ConfigureAwait(false);
        var next = await MergePreferenceCommandAsync(current, bearerToken, command, ct).ConfigureAwait(false);
        return await _commandService.SaveAsync(next, ct).ConfigureAwait(false);
    }

    public async Task<UserConfigSaveReceipt> SaveAsync(
        string scopeId,
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentNullException.ThrowIfNull(command);

        var current = await _queryPort.GetAsync(scopeId.Trim(), ct).ConfigureAwait(false);
        var next = await MergePreferenceCommandAsync(current, bearerToken, command, ct).ConfigureAwait(false);
        return await _commandService.SaveAsync(scopeId.Trim(), next, ct).ConfigureAwait(false);
    }

    public async Task<UserConfigSaveReceipt> SaveSelectedOptionAsync(
        string scopeId,
        UserLlmOption option,
        string? model,
        bool preserveCurrentModelWhenMissing,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentNullException.ThrowIfNull(option);

        var normalizedScopeId = scopeId.Trim();
        var current = await _queryPort.GetAsync(normalizedScopeId, ct).ConfigureAwait(false);
        var next = UserLlmPreferenceWriteCore.MergeSelectedOption(
            current,
            option,
            model,
            preserveCurrentModelWhenMissing);
        return await _commandService.SaveAsync(normalizedScopeId, next, ct).ConfigureAwait(false);
    }

    public async Task<UserConfig> MergeLegacyFieldsAsync(
        string? bearerToken,
        UserConfig current,
        string? defaultModel,
        string? preferredLlmRoute,
        CancellationToken ct)
    {
        var requestedModel = UserLlmPreferenceWriteCore.NormalizeOptional(defaultModel);
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
            return UserLlmPreferenceWriteCore.Reset(current);

        if (!string.IsNullOrWhiteSpace(command.ServiceId))
        {
            var options = await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false);
            var option = UserLlmPreferenceWriteCore.FindOption(options, command.ServiceId!);
            if (option is null)
                throw new InvalidOperationException($"LLM service '{command.ServiceId}' is not routable for this user.");

            return UserLlmPreferenceWriteCore.MergeSelectedOption(
                current,
                option,
                command.Model,
                preserveCurrentModelWhenMissing: false);
        }

        if (command.RouteValue is not null)
        {
            var routeValue = UserConfigLlmRoute.Normalize(command.RouteValue);
            if (string.Equals(routeValue, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase))
                return UserLlmPreferenceWriteCore.MergeGatewayRoute(current, command.Model);

            var options = await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false);
            var option = UserLlmPreferenceWriteCore.FindOption(options, routeValue);
            if (option is null)
                throw new InvalidOperationException($"LLM route '{command.RouteValue}' is not routable for this user.");

            return UserLlmPreferenceWriteCore.MergeSelectedOption(
                current,
                option,
                command.Model,
                preserveCurrentModelWhenMissing: false);
        }

        if (!string.IsNullOrWhiteSpace(command.PresetId))
        {
            var activated = await ActivatePresetAsync(bearerToken, command.PresetId!, ct).ConfigureAwait(false);
            return UserLlmPreferenceWriteCore.MergeSelectedOption(
                current,
                activated,
                command.Model,
                preserveCurrentModelWhenMissing: true);
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
            return UserLlmPreferenceWriteCore.MergeRawModel(current, normalized);

        if (UserConfigLlmModel.TryParseRouteModel(normalized) is not { } prefixed)
            return UserLlmPreferenceWriteCore.MergeRawModel(current, normalized);

        var options = await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false);
        var option = UserLlmPreferenceWriteCore.FindOption(options, prefixed.RouteSlug);
        if (option is null)
            throw new InvalidOperationException($"LLM service '{prefixed.RouteSlug}' is not routable for this user.");

        return UserLlmPreferenceWriteCore.MergeSelectedOption(
            current,
            option,
            prefixed.Model,
            preserveCurrentModelWhenMissing: false);
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
        var option = UserLlmPreferenceWriteCore.FindOption(options, routeSlug);
        if (option is null)
            return null;

        UserLlmPreferenceWriteCore.EnsureSelectable(option);
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
            throw new InvalidOperationException(
                $"LLM preset service '{existing.ServiceId}' is not routable for this user.");

        UserLlmPreferenceWriteCore.EnsureSelectable(option);
        return option with { DefaultModel = existing.DefaultModel ?? option.DefaultModel };
    }

    private async Task<UserLlmOption> ActivateProvisioningPresetAsync(
        string bearerToken,
        string provisionEndpointId,
        CancellationToken ct)
    {
        var result = NyxIdLlmServiceMapping.ToOption(
            await _catalogPort.ProvisionAsync(bearerToken, provisionEndpointId, ct).ConfigureAwait(false));
        UserLlmPreferenceWriteCore.EnsureSelectable(result);
        return result;
    }
}
