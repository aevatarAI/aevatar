using Aevatar.AI.Abstractions;
using Aevatar.AIWorkspace.Application.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AIWorkspace.Application;

public sealed class AIWorkspaceModelsQueryService(
    IUserLlmPreferenceService personalPreferences,
    ILLMModelCatalogPolicyApplicationService scopeCatalog,
    ILogger<AIWorkspaceModelsQueryService>? logger = null)
    : IAIWorkspaceModelsQueryService
{
    private readonly ILogger<AIWorkspaceModelsQueryService> _logger =
        logger ?? NullLogger<AIWorkspaceModelsQueryService>.Instance;

    public async Task<AIWorkspaceModelsView> QueryAsync(
        string scopeId,
        string? bearerToken,
        CancellationToken ct = default)
    {
        var personalTask = ReadPersonalAsync(bearerToken, ct);
        var scopeTask = ReadScopeAsync(scopeId, ct);
        await Task.WhenAll(personalTask, scopeTask).ConfigureAwait(false);
        return new AIWorkspaceModelsView(
            "independent_authorities",
            await personalTask.ConfigureAwait(false),
            await scopeTask.ConfigureAwait(false));
    }

    private async Task<AIWorkspacePersonalModelsView> ReadPersonalAsync(
        string? bearerToken,
        CancellationToken ct)
    {
        try
        {
            var settings = await personalPreferences.GetSettingsAsync(bearerToken, ct).ConfigureAwait(false);
            return new AIWorkspacePersonalModelsView(
                "user_llm_preferences",
                AIWorkspaceSourceAvailability.Available,
                null,
                null,
                ToPersonalSettings(settings),
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AI workspace personal model settings source is unavailable.");
            return new AIWorkspacePersonalModelsView(
                "user_llm_preferences",
                AIWorkspaceSourceAvailability.Unavailable,
                null,
                null,
                null,
                new AIWorkspaceSourceErrorView(
                    "PERSONAL_MODEL_SETTINGS_UNAVAILABLE",
                    "Personal model settings are temporarily unavailable."));
        }
    }

    private async Task<AIWorkspaceCatalogModelsView> ReadScopeAsync(
        string scopeId,
        CancellationToken ct)
    {
        try
        {
            var catalog = await scopeCatalog.GetScopeAsync(scopeId, ct).ConfigureAwait(false);
            return new AIWorkspaceCatalogModelsView(
                "llm_model_catalog_policy",
                AIWorkspaceSourceAvailability.Available,
                catalog.StateVersion,
                catalog.UpdatedAtUtc,
                ToScopePolicy(catalog),
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LLMModelCatalogApplicationException ex)
        {
            _logger.LogWarning(
                ex,
                "AI workspace scope model catalog is unavailable for scope {ScopeId}; code {ErrorCode}.",
                scopeId,
                ex.Code);
            return ScopeUnavailable(
                "MODEL_CATALOG_UNAVAILABLE",
                "Model catalog is temporarily unavailable.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "AI workspace scope model catalog is unavailable for scope {ScopeId}.",
                scopeId);
            return ScopeUnavailable(
                "MODEL_CATALOG_UNAVAILABLE",
                "Model catalog is temporarily unavailable.");
        }
    }

    private static AIWorkspaceCatalogModelsView ScopeUnavailable(
        string code,
        string message) =>
        new(
            "llm_model_catalog_policy",
            AIWorkspaceSourceAvailability.Unavailable,
            null,
            null,
            null,
            new AIWorkspaceSourceErrorView(code, message));

    private static AIWorkspaceModelCatalogPolicyView ToScopePolicy(LLMModelCatalogView view) =>
        new(
            view.Mode == LLMModelCatalogPolicyMode.Custom ? "custom_replace" : "inherit_platform",
            view.Configured,
            view.Sources.Select(ToModelSource).ToArray(),
            view.EffectiveSource == LLMModelCatalogEffectiveSourceKind.Scope ? "custom" : "platform",
            view.EffectiveSources.Select(ToModelSource).ToArray(),
            view.LastMutationId);

    private static AIWorkspaceModelSourceView ToModelSource(LLMModelCatalogPolicySource source)
    {
        var (sourceId, catalogServiceId, userServiceId) = source.SourceIdentity switch
        {
            NyxIDCatalogServiceModelSourceIdentity catalog =>
                ($"catalog:{catalog.CatalogServiceId}", catalog.CatalogServiceId, (string?)null),
            NyxIDUserServiceModelSourceIdentity user =>
                ($"user:{user.UserServiceId}", (string?)null, user.UserServiceId),
            _ => ($"unsupported:{source.SourceIdentity.ServiceId}", null, null),
        };
        return new AIWorkspaceModelSourceView(
            sourceId,
            source.ServiceSlugSnapshot,
            catalogServiceId,
            userServiceId,
            "explicit_models",
            source.ModelSelection.UpstreamModelIds);
    }

    private static AIWorkspaceUserLlmSettingsView ToPersonalSettings(UserLlmSettingsView view) =>
        new(
            view.SavedSelection is null ? null : ToSavedSelection(view.SavedSelection),
            view.SavedRouteLabel,
            view.SelectionStatus.ToWireValue(),
            view.CatalogDiagnostic.ToWireValue(),
            view.Remediation.ToWireValue(),
            view.RouteOptions.Select(ToRouteOption).ToArray(),
            view.ModelGroupsByRoute.Select(static group => new AIWorkspaceUserLlmModelGroupView(
                group.RouteValue,
                group.GroupId,
                group.Label,
                group.Models)).ToArray(),
            view.CatalogStatus,
            new AIWorkspaceUserLlmSettingsCapabilitiesView(
                view.Capabilities.CanEditRoute,
                view.Capabilities.CanEditModel,
                view.Capabilities.CanSave,
                view.Capabilities.CanRetryCatalog),
            view.SetupHint is null
                ? null
                : new AIWorkspaceUserLlmSetupHintView(
                    view.SetupHint.SetupUrl,
                    view.SetupHint.Presets.Select(static preset => new AIWorkspaceUserLlmPresetView(
                        preset.Id,
                        preset.Title,
                        preset.Description,
                        ToPresetActivation(preset.Activation))).ToArray()));

    private static AIWorkspaceUserLlmSelectionView ToSavedSelection(LLMSelection selection) =>
        new(
            selection.RouteKind switch
            {
                LLMRouteKind.Unspecified => "unspecified",
                LLMRouteKind.Gateway => "gateway",
                LLMRouteKind.NyxIdUserService => "nyx_id_user_service",
                _ => "unsupported",
            },
            selection.RouteValue,
            selection.NyxIdUserServiceId,
            selection.ServiceSlugSnapshot,
            selection.ModelSelection is null ? null : ToModelSelection(selection.ModelSelection));

    private static AIWorkspaceUserLlmModelSelectionView ToModelSelection(LLMModelSelection selection) =>
        new(
            selection.Kind switch
            {
                LLMModelSelectionKind.Unspecified => "unspecified",
                LLMModelSelectionKind.ProviderDefault => "provider_default",
                LLMModelSelectionKind.ExplicitModel => "explicit_model",
                _ => "unsupported",
            },
            selection.Kind == LLMModelSelectionKind.ExplicitModel ? selection.ModelId : null);

    private static AIWorkspaceUserLlmRouteOptionView ToRouteOption(UserLlmRouteOption option) =>
        new(
            option.RouteValue,
            option.Label,
            option.Source,
            option.Status,
            option.Allowed,
            option.Ready,
            option.UserServiceId,
            option.ServiceSlug,
            new AIWorkspaceUserLlmModelCatalogView(
                option.ModelCatalog.Certainty.ToWireValue(),
                option.ModelCatalog.ModelIds.ToArray(),
                string.IsNullOrEmpty(option.ModelCatalog.DefaultModelId)
                    ? null
                    : option.ModelCatalog.DefaultModelId,
                option.ModelCatalog.DiagnosticKind.ToWireValue()),
            option.Description);

    private static AIWorkspaceUserLlmPresetActivationView ToPresetActivation(
        UserLlmPresetActivation activation) => activation switch
        {
            UseExistingService existing => new AIWorkspaceUserLlmPresetActivationView(
                "use_existing_service",
                existing.UserServiceId,
                existing.RouteValue,
                existing.DefaultModel,
                null),
            ProvisionThenUse provision => new AIWorkspaceUserLlmPresetActivationView(
                "provision_then_use",
                null,
                null,
                null,
                provision.ProvisionEndpointId),
            _ => new AIWorkspaceUserLlmPresetActivationView(
                "unsupported",
                null,
                null,
                null,
                null),
        };
}
