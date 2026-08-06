using Aevatar.AI.Abstractions;
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
        UserLlmPreferenceIntent intent,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var selection = await BuildSelectionAsync(bearerToken, intent, ct).ConfigureAwait(false);
        return await _commandService.UpdateAsync(
            resource,
            new UserConfigUpdate(LlmSelection: selection),
            ct).ConfigureAwait(false);
    }

    // Channel callers are migrated to typed intents in the atomic Channel task.
    public Task<UserConfigSaveReceipt> SaveAsync(
        UserConfigResourceKey resource,
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct) =>
        SaveAsync(resource, bearerToken, ToIntent(command), ct);

    private async Task<LLMSelection> BuildSelectionAsync(
        string? bearerToken,
        UserLlmPreferenceIntent intent,
        CancellationToken ct) => intent switch
        {
            ResetUserLlmPreferenceIntent => UserLlmPreferenceWriteCore.BuildResetSelection(),
            SelectGatewayUserLlmPreferenceIntent gateway => await BuildGatewaySelectionAsync(
                bearerToken,
                gateway.ModelSelection,
                ct).ConfigureAwait(false),
            SelectUserServiceUserLlmPreferenceIntent service => await BuildUserServiceSelectionAsync(
                bearerToken,
                service.UserServiceId,
                service.ModelSelection,
                ct).ConfigureAwait(false),
            ActivateUserLlmPresetIntent preset => await BuildPresetSelectionAsync(
                bearerToken,
                preset.PresetId,
                ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported LLM preference intent."),
        };

    private async Task<LLMSelection> BuildGatewaySelectionAsync(
        string? bearerToken,
        LLMModelSelection modelSelection,
        CancellationToken ct)
    {
        var result = await LoadFreshCatalogAsync(bearerToken, ct).ConfigureAwait(false);
        var option = RequireGatewayOption(result.Services.Select(NyxIdLlmServiceMapping.ToOption).ToArray());
        ValidateModelSelection(option, modelSelection);
        return UserLlmPreferenceWriteCore.BuildGatewaySelection(modelSelection);
    }

    private async Task<LLMSelection> BuildUserServiceSelectionAsync(
        string? bearerToken,
        string userServiceId,
        LLMModelSelection modelSelection,
        CancellationToken ct)
    {
        var options = await LoadFreshOptionsAsync(bearerToken, ct).ConfigureAwait(false);
        var option = UserLlmPreferenceWriteCore.RequireInventoryOption(options, userServiceId);
        ValidateModelSelection(option, modelSelection);
        return UserLlmPreferenceWriteCore.BuildInventorySelection(option, modelSelection);
    }

    private async Task<LLMSelection> BuildPresetSelectionAsync(
        string? bearerToken,
        string presetId,
        CancellationToken ct)
    {
        var normalizedPresetId = UserLlmPreferenceWriteCore.NormalizeOptional(presetId) ??
                                 throw new InvalidOperationException("LLM preset ID is required.");
        var token = RequireBearer(bearerToken, "activate an LLM preset");
        var result = await _catalogPort.GetFreshServicesAsync(token, ct).ConfigureAwait(false);
        var preset = result.SetupHint?.Presets.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, normalizedPresetId, StringComparison.Ordinal));
        if (preset is null)
            throw new InvalidOperationException($"LLM preset '{normalizedPresetId}' is not available.");

        return preset.Activation switch
        {
            UseExistingService existing => BuildExistingPresetSelection(result, existing),
            ProvisionThenUse provisioning => await BuildProvisionedPresetSelectionAsync(
                token,
                provisioning.ProvisionEndpointId,
                ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Unsupported LLM preset activation for '{normalizedPresetId}'."),
        };
    }

    private static LLMSelection BuildExistingPresetSelection(
        NyxIdLlmServicesResult result,
        UseExistingService existing)
    {
        var option = UserLlmPreferenceWriteCore.RequireInventoryOption(
            result.Services.Select(NyxIdLlmServiceMapping.ToOption).ToArray(),
            existing.UserServiceId);
        var modelSelection = ToModelSelection(existing.DefaultModel);
        ValidateModelSelection(option, modelSelection);
        return UserLlmPreferenceWriteCore.BuildInventorySelection(option, modelSelection);
    }

    private async Task<LLMSelection> BuildProvisionedPresetSelectionAsync(
        string bearerToken,
        string provisionEndpointId,
        CancellationToken ct)
    {
        var provisioned = await _catalogPort
            .ProvisionAsync(bearerToken, provisionEndpointId, ct)
            .ConfigureAwait(false);
        var userServiceId = UserLlmPreferenceWriteCore.NormalizeOptional(provisioned.CatalogEntryId) ??
                            throw new InvalidOperationException(
                                "Provisioned LLM service did not return a user service ID candidate.");
        var options = await LoadFreshOptionsAsync(bearerToken, ct).ConfigureAwait(false);
        var option = UserLlmPreferenceWriteCore.RequireInventoryOption(options, userServiceId);
        var modelSelection = new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault };
        ValidateModelSelection(option, modelSelection);
        return UserLlmPreferenceWriteCore.BuildInventorySelection(option, modelSelection);
    }

    private async Task<NyxIdLlmServicesResult> LoadFreshCatalogAsync(
        string? bearerToken,
        CancellationToken ct)
    {
        var token = RequireBearer(bearerToken, "read LLM services");
        return await _catalogPort.GetFreshServicesAsync(token, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<UserLlmOption>> LoadFreshOptionsAsync(
        string? bearerToken,
        CancellationToken ct)
    {
        var result = await LoadFreshCatalogAsync(bearerToken, ct).ConfigureAwait(false);
        return result.Services.Select(NyxIdLlmServiceMapping.ToOption).ToArray();
    }

    private static UserLlmOption RequireGatewayOption(IReadOnlyList<UserLlmOption> options)
    {
        var matches = options.Where(option =>
            string.Equals(option.RouteValue, UserConfigLlmRouteDefaults.Gateway, StringComparison.Ordinal) &&
            string.Equals(
                UserLlmCatalogNormalization.NormalizeSource(option.Source).ToWireValue(),
                UserLlmRouteSource.GatewayProvider,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("LLM Gateway is not selectable.");

        UserLlmPreferenceWriteCore.EnsureSelectable(matches[0]);
        return matches[0];
    }

    private static void ValidateModelSelection(UserLlmOption option, LLMModelSelection modelSelection)
    {
        ArgumentNullException.ThrowIfNull(modelSelection);
        switch (modelSelection.Kind)
        {
            case LLMModelSelectionKind.ProviderDefault when string.IsNullOrEmpty(modelSelection.ModelId):
                if (option.ModelCatalog.Certainty is
                    LLMModelCatalogCertainty.Enumerated or
                    LLMModelCatalogCertainty.NotVerifiable)
                {
                    return;
                }

                break;
            case LLMModelSelectionKind.ExplicitModel:
                if (option.ModelCatalog.Certainty != LLMModelCatalogCertainty.Enumerated ||
                    !option.ModelCatalog.ModelIds.Contains(modelSelection.ModelId, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"LLM model '{modelSelection.ModelId}' is not available for the selected route.");
                }

                return;
        }


        throw new InvalidOperationException("Select a verifiable provider default or one explicit LLM model.");
    }

    private static LLMModelSelection ToModelSelection(string? model)
    {
        var normalized = UserLlmPreferenceWriteCore.NormalizeOptional(model);
        var selection = new LLMModelSelection
        {
            Kind = normalized is null
                ? LLMModelSelectionKind.ProviderDefault
                : LLMModelSelectionKind.ExplicitModel,
            ModelId = normalized ?? string.Empty,
        };
        _ = UserLlmPreferenceWriteCore.BuildGatewaySelection(selection);
        return selection;
    }

    private static UserLlmPreferenceIntent ToIntent(SaveUserLlmPreferenceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Reset == true)
            return new ResetUserLlmPreferenceIntent();

        var userServiceId = UserLlmPreferenceWriteCore.NormalizeOptional(command.UserServiceId);
        if (userServiceId is not null)
        {
            if (command.RouteValue is not null)
                throw new InvalidOperationException("userServiceId cannot be combined with routeValue.");
            return new SelectUserServiceUserLlmPreferenceIntent(
                userServiceId,
                ToModelSelection(command.Model));
        }

        if (command.RouteValue is not null)
        {
            if (!UserLlmPreferenceWriteCore.IsGatewayWriteAlias(command.RouteValue))
                throw new InvalidOperationException("userServiceId is required for a NyxID service selection.");
            return new SelectGatewayUserLlmPreferenceIntent(ToModelSelection(command.Model));
        }

        var presetId = UserLlmPreferenceWriteCore.NormalizeOptional(command.PresetId);
        if (presetId is not null)
            return new ActivateUserLlmPresetIntent(presetId);

        throw new InvalidOperationException("A complete LLM route selection is required.");
    }

    private static string RequireBearer(string? bearerToken, string operation) =>
        !string.IsNullOrWhiteSpace(bearerToken)
            ? bearerToken
            : throw new InvalidOperationException($"Bearer token is required to {operation}.");
}
