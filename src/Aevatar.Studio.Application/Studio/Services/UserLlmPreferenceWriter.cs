using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class UserLlmPreferenceWriter
{
    private readonly IUserConfigCommandService _commandService;
    private readonly IUserLlmCatalogPort _catalogPort;

    public UserLlmPreferenceWriter(
        IUserConfigCommandService commandService,
        IUserLlmCatalogPort catalogPort)
    {
        _commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
    }

    public async Task<UserConfigSaveReceipt> SaveAsync(
        UserConfigResourceKey resource,
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var update = await BuildUpdateAsync(bearerToken, command, ct).ConfigureAwait(false);
        return await _commandService.UpdateAsync(resource, update, ct).ConfigureAwait(false);
    }

    public Task<UserConfigSaveReceipt> SaveSelectedOptionAsync(
        UserConfigResourceKey resource,
        UserLlmOption option,
        string? model,
        bool preserveCurrentModelWhenMissing,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(option);
        return _commandService.UpdateAsync(
            resource,
            BuildSelectedOptionUpdate(option, model, preserveCurrentModelWhenMissing),
            ct);
    }

    private async Task<UserConfigUpdate> BuildUpdateAsync(
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct)
    {
        var userServiceId = UserLlmPreferenceWriteCore.NormalizeOptional(command.UserServiceId);
        var normalizedModel = UserLlmPreferenceWriteCore.NormalizeOptional(command.Model);
        var prefixedModel = UserConfigLlmModel.TryParseRouteModel(normalizedModel);

        if (command.Reset == true)
        {
            return new UserConfigUpdate(
                DefaultModel: string.Empty,
                LlmSelection: UserLlmPreferenceWriteCore.BuildGatewaySelection());
        }

        if (userServiceId is not null)
        {
            if (command.RouteValue is not null &&
                string.Equals(
                    UserConfigLlmRoute.Normalize(command.RouteValue),
                    UserConfigLlmRouteDefaults.Gateway,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("userServiceId cannot be combined with the Gateway route.");
            }

            var option = UserLlmPreferenceWriteCore.RequireInventoryOption(
                await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false),
                userServiceId);
            return BuildSelectedOptionUpdate(
                option,
                command.Model,
                preserveCurrentModelWhenMissing: false);
        }

        if (command.RouteValue is not null)
        {
            var routeValue = UserConfigLlmRoute.Normalize(command.RouteValue);
            if (!string.Equals(routeValue, UserConfigLlmRouteDefaults.Gateway, StringComparison.Ordinal))
                throw new InvalidOperationException("userServiceId is required for a NyxID service selection.");
            if (prefixedModel is not null)
                throw new InvalidOperationException("userServiceId is required for a route-prefixed model selection.");

            return new UserConfigUpdate(
                DefaultModel: command.Model is null ? null : normalizedModel ?? string.Empty,
                LlmSelection: UserLlmPreferenceWriteCore.BuildGatewaySelection());
        }

        if (!string.IsNullOrWhiteSpace(command.PresetId))
        {
            var option = await ActivatePresetAsync(bearerToken, command.PresetId, ct).ConfigureAwait(false);
            return BuildSelectedOptionUpdate(
                option,
                command.Model,
                preserveCurrentModelWhenMissing: true);
        }

        if (command.Model is not null)
        {
            if (prefixedModel is not null)
                throw new InvalidOperationException("userServiceId is required for a route-prefixed model selection.");

            return new UserConfigUpdate(DefaultModel: normalizedModel ?? string.Empty);
        }

        throw new InvalidOperationException("Specify userServiceId, routeValue, presetId, model, or reset.");
    }

    private static UserConfigUpdate BuildSelectedOptionUpdate(
        UserLlmOption option,
        string? model,
        bool preserveCurrentModelWhenMissing)
    {
        var requestedModel = UserLlmPreferenceWriteCore.NormalizeModelForRoute(model, option);
        var defaultModel = requestedModel ?? option.DefaultModel;
        if (defaultModel is null && !preserveCurrentModelWhenMissing)
            defaultModel = string.Empty;

        return new UserConfigUpdate(
            DefaultModel: defaultModel,
            LlmSelection: UserLlmPreferenceWriteCore.BuildInventorySelection(option));
    }

    private async Task<IReadOnlyList<UserLlmOption>> LoadOptionsAsync(
        string? bearerToken,
        CancellationToken ct)
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

        var result = await _catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
        var preset = result.SetupHint?.Presets.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, presetId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            throw new InvalidOperationException($"LLM preset '{presetId}' is not available.");

        return preset.Activation switch
        {
            UseExistingService existing => ActivateExistingPreset(
                result.Services.Select(NyxIdLlmServiceMapping.ToOption).ToArray(),
                existing),
            ProvisionThenUse provisioning => await ActivateProvisioningPresetAsync(
                bearerToken,
                provisioning.ProvisionEndpointId,
                ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported LLM preset activation for '{preset.Id}'."),
        };
    }

    private static UserLlmOption ActivateExistingPreset(
        IReadOnlyList<UserLlmOption> options,
        UseExistingService existing)
    {
        try
        {
            var option = UserLlmPreferenceWriteCore.RequireInventoryOption(options, existing.UserServiceId);
            UserLlmPreferenceWriteCore.EnsureSelectable(option);
            return option with { DefaultModel = existing.DefaultModel ?? option.DefaultModel };
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"LLM preset user service '{existing.UserServiceId}' is not selectable for this user.");
        }
    }

    private async Task<UserLlmOption> ActivateProvisioningPresetAsync(
        string bearerToken,
        string provisionEndpointId,
        CancellationToken ct)
    {
        var provisioned = await _catalogPort
            .ProvisionAsync(bearerToken, provisionEndpointId, ct)
            .ConfigureAwait(false);
        var userServiceId = provisioned.Identity is
            {
                Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            } identity
            ? UserLlmPreferenceWriteCore.NormalizeOptional(identity.NyxIdUserServiceId)
            : null;
        if (userServiceId is null)
            throw new InvalidOperationException("Provisioned LLM service did not return an inventory identity.");

        var refreshed = await LoadOptionsAsync(bearerToken, ct).ConfigureAwait(false);
        return UserLlmPreferenceWriteCore.RequireInventoryOption(refreshed, userServiceId);
    }
}
