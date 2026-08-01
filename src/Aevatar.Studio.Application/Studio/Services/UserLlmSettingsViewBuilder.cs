using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

internal sealed class UserLlmSettingsViewBuilder
{
    private readonly string _gatewayRouteLabel;

    public UserLlmSettingsViewBuilder(string gatewayRouteLabel)
    {
        _gatewayRouteLabel = UserLlmPreferenceWriteCore.NormalizeOptional(gatewayRouteLabel) ?? "NyxID Gateway";
    }

    public UserLlmSettingsView BuildAvailable(
        NyxIdLlmServicesResult result,
        UserConfig config)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(config);

        var savedSelection = config.LlmSelection?.Clone();
        var options = BuildRouteOptions(result.Services);
        var classification = ClassifyAvailable(config, options);
        var catalogStatus = result.Services.Count == 0
            ? UserLlmCatalogStatusValue.Empty
            : UserLlmCatalogStatusValue.Ready;
        var canSave = options.Any(IsInteractivelySelectable);

        return new UserLlmSettingsView(
            SavedSelection: savedSelection,
            SavedRouteLabel: ResolveSavedRouteLabel(savedSelection, options),
            SelectionStatus: classification.Status,
            CatalogDiagnostic: classification.Diagnostic,
            Remediation: classification.Remediation,
            RouteOptions: options,
            ModelGroupsByRoute: BuildModelGroups(result.Services),
            CatalogStatus: catalogStatus.ToWireValue(),
            Capabilities: new UserLlmSettingsCapabilities(
                CanEditRoute: canSave,
                CanEditModel: options.Any(option =>
                    option.Ready &&
                    option.Allowed &&
                    option.ModelCatalog.Certainty == LLMModelCatalogCertainty.Enumerated),
                CanSave: canSave,
                CanRetryCatalog: false),
            SetupHint: result.SetupHint);
    }

    public UserLlmSettingsView BuildVerificationUnavailable(UserConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var savedSelection = config.LlmSelection?.Clone();
        var persistenceStatus = ClassifyPersisted(config);
        var status = persistenceStatus switch
        {
            PersistedSelectionStatus.SystemDefault => UserLlmSelectionStatus.SystemDefault,
            PersistedSelectionStatus.Ready => UserLlmSelectionStatus.VerificationUnavailable,
            _ => UserLlmSelectionStatus.LegacyRepairRequired,
        };
        var remediation = status switch
        {
            UserLlmSelectionStatus.SystemDefault => UserLlmRemediationKind.None,
            UserLlmSelectionStatus.VerificationUnavailable => UserLlmRemediationKind.RetryCatalog,
            _ => UserLlmRemediationKind.Reselect,
        };
        var routeOption = BuildUnavailableSavedRouteOption(savedSelection);

        return new UserLlmSettingsView(
            SavedSelection: savedSelection,
            SavedRouteLabel: ResolveSavedRouteLabel(savedSelection, [routeOption]),
            SelectionStatus: status,
            CatalogDiagnostic: LLMModelCatalogDiagnosticKind.ObservationUnavailable,
            Remediation: remediation,
            RouteOptions: [routeOption],
            ModelGroupsByRoute: [],
            CatalogStatus: UserLlmCatalogStatusValue.Unavailable.ToWireValue(),
            Capabilities: new UserLlmSettingsCapabilities(
                CanEditRoute: false,
                CanEditModel: false,
                CanSave: false,
                CanRetryCatalog: true),
            SetupHint: null);
    }

    private SelectionClassification ClassifyAvailable(
        UserConfig config,
        IReadOnlyList<UserLlmRouteOption> options)
    {
        var persistenceStatus = ClassifyPersisted(config);
        if (persistenceStatus == PersistedSelectionStatus.SystemDefault)
        {
            return new SelectionClassification(
                UserLlmSelectionStatus.SystemDefault,
                LLMModelCatalogDiagnosticKind.Unspecified,
                UserLlmRemediationKind.None);
        }

        if (persistenceStatus == PersistedSelectionStatus.LegacyRepairRequired)
        {
            return new SelectionClassification(
                UserLlmSelectionStatus.LegacyRepairRequired,
                LLMModelCatalogDiagnosticKind.Unspecified,
                UserLlmRemediationKind.Reselect);
        }

        var selection = config.LlmSelection!;
        var option = FindSavedOption(selection, options);
        if (option is not null && IsSelectionAdmitted(selection, option))
        {
            return new SelectionClassification(
                UserLlmSelectionStatus.Ready,
                LLMModelCatalogDiagnosticKind.Unspecified,
                UserLlmRemediationKind.None);
        }

        var diagnostic = option?.ModelCatalog.DiagnosticKind ?? LLMModelCatalogDiagnosticKind.RouteNotReady;
        if (diagnostic == LLMModelCatalogDiagnosticKind.Unspecified)
            diagnostic = LLMModelCatalogDiagnosticKind.ResponseInvalid;

        return new SelectionClassification(
            UserLlmSelectionStatus.NeedsRepair,
            diagnostic,
            selection.RouteKind == LLMRouteKind.Gateway
                ? UserLlmRemediationKind.ConnectProvider
                : UserLlmRemediationKind.ChooseReplacement);
    }

    private static PersistedSelectionStatus ClassifyPersisted(UserConfig config)
    {
        var selection = config.LlmSelection;
        if (selection is null)
        {
            return string.IsNullOrEmpty(config.PreferredLlmRoute) && string.IsNullOrEmpty(config.DefaultModel)
                ? PersistedSelectionStatus.SystemDefault
                : PersistedSelectionStatus.LegacyRepairRequired;
        }

        try
        {
            LLMSelectionPolicy.ValidateSelection(selection);
        }
        catch (InvalidOperationException)
        {
            return PersistedSelectionStatus.LegacyRepairRequired;
        }

        return selection.RouteKind == LLMRouteKind.Unspecified
            ? PersistedSelectionStatus.SystemDefault
            : PersistedSelectionStatus.Ready;
    }

    private IReadOnlyList<UserLlmRouteOption> BuildRouteOptions(
        IReadOnlyList<NyxIdLlmService> services)
    {
        var options = new List<UserLlmRouteOption>
        {
            BuildGatewayRouteOption(services),
        };
        var seenInventoryIds = new HashSet<string>(StringComparer.Ordinal);
        var seenDiagnostics = new HashSet<string>(StringComparer.Ordinal);

        foreach (var service in services)
        {
            if (!IsUserServiceRoute(service))
                continue;

            var route = UserLlmPreferenceWriteCore.NormalizeOptional(service.RouteValue) ?? string.Empty;
            var userServiceId = InventoryUserServiceId(service);
            if (userServiceId is not null)
            {
                if (!seenInventoryIds.Add(userServiceId))
                    continue;
            }
            else
            {
                var diagnosticKey = UserLlmPreferenceWriteCore.NormalizeOptional(service.CatalogEntryId) is { } entryId
                    ? $"id:{entryId}"
                    : $"route:{route}";
                if (!seenDiagnostics.Add(diagnosticKey))
                    continue;
            }

            options.Add(new UserLlmRouteOption(
                RouteValue: route,
                Label: NormalizeDisplayName(service.DisplayName, service.ServiceSlug),
                Source: UserLlmCatalogNormalization.NormalizeSource(service.Source).ToWireValue(),
                Status: UserLlmCatalogNormalization.NormalizeStatus(service.Status).ToWireValue(),
                Allowed: service.Allowed,
                Ready: UserLlmCatalogNormalization.IsReady(service),
                UserServiceId: userServiceId,
                ServiceSlug: service.ServiceSlug,
                ModelCatalog: service.ModelCatalog.Clone(),
                Description: UserLlmPreferenceWriteCore.NormalizeOptional(service.Description)));
        }

        return options;
    }

    private UserLlmRouteOption BuildGatewayRouteOption(IReadOnlyList<NyxIdLlmService> services)
    {
        var gatewayServices = services
            .Where(IsGatewayRouteService)
            .ToArray();
        var selected = gatewayServices.FirstOrDefault(UserLlmCatalogNormalization.IsReady) ??
                       gatewayServices.FirstOrDefault();
        if (selected is null)
        {
            return new UserLlmRouteOption(
                RouteValue: UserConfigLlmRouteDefaults.Gateway,
                Label: _gatewayRouteLabel,
                Source: UserLlmRouteSourceValue.GatewayProvider.ToWireValue(),
                Status: UserLlmRouteStatusValue.Unavailable.ToWireValue(),
                Allowed: false,
                Ready: false,
                UserServiceId: null,
                ServiceSlug: null,
                ModelCatalog: UnavailableCatalog(LLMModelCatalogDiagnosticKind.RouteNotReady),
                Description: null);
        }

        return new UserLlmRouteOption(
            RouteValue: UserConfigLlmRouteDefaults.Gateway,
            Label: _gatewayRouteLabel,
            Source: UserLlmRouteSourceValue.GatewayProvider.ToWireValue(),
            Status: UserLlmCatalogNormalization.NormalizeStatus(selected.Status).ToWireValue(),
            Allowed: selected.Allowed,
            Ready: UserLlmCatalogNormalization.IsReady(selected),
            UserServiceId: null,
            ServiceSlug: null,
            ModelCatalog: selected.ModelCatalog.Clone(),
            Description: UserLlmPreferenceWriteCore.NormalizeOptional(selected.Description));
    }

    private UserLlmRouteOption BuildUnavailableSavedRouteOption(LLMSelection? selection)
    {
        var isUserService = selection?.RouteKind == LLMRouteKind.NyxIdUserService;
        var route = selection?.RouteKind switch
        {
            LLMRouteKind.Gateway => UserConfigLlmRouteDefaults.Gateway,
            LLMRouteKind.NyxIdUserService => selection.RouteValue,
            _ => UserConfigLlmRouteDefaults.Gateway,
        };
        var label = isUserService
            ? UserLlmPreferenceWriteCore.NormalizeOptional(selection!.ServiceSlugSnapshot) ?? route
            : _gatewayRouteLabel;

        return new UserLlmRouteOption(
            RouteValue: route,
            Label: label,
            Source: isUserService
                ? UserLlmRouteSourceValue.UserService.ToWireValue()
                : UserLlmRouteSourceValue.GatewayProvider.ToWireValue(),
            Status: UserLlmRouteStatusValue.Unavailable.ToWireValue(),
            Allowed: false,
            Ready: false,
            UserServiceId: isUserService
                ? UserLlmPreferenceWriteCore.NormalizeOptional(selection!.NyxIdUserServiceId)
                : null,
            ServiceSlug: isUserService
                ? UserLlmPreferenceWriteCore.NormalizeOptional(selection!.ServiceSlugSnapshot)
                : null,
            ModelCatalog: UnavailableCatalog(LLMModelCatalogDiagnosticKind.ObservationUnavailable),
            Description: null);
    }

    private static bool IsInteractivelySelectable(UserLlmRouteOption option) =>
        option.Ready &&
        option.Allowed &&
        option.ModelCatalog.Certainty is
            LLMModelCatalogCertainty.Enumerated or LLMModelCatalogCertainty.NotVerifiable;

    private static bool IsSelectionAdmitted(LLMSelection selection, UserLlmRouteOption option)
    {
        if (!option.Ready || !option.Allowed || selection.ModelSelection is null)
            return false;

        return selection.ModelSelection.Kind switch
        {
            LLMModelSelectionKind.ProviderDefault => option.ModelCatalog.Certainty is
                LLMModelCatalogCertainty.Enumerated or LLMModelCatalogCertainty.NotVerifiable,
            LLMModelSelectionKind.ExplicitModel =>
                option.ModelCatalog.Certainty == LLMModelCatalogCertainty.Enumerated &&
                option.ModelCatalog.ModelIds.Contains(selection.ModelSelection.ModelId, StringComparer.Ordinal),
            _ => false,
        };
    }

    private static UserLlmRouteOption? FindSavedOption(
        LLMSelection selection,
        IReadOnlyList<UserLlmRouteOption> routeOptions) => selection.RouteKind switch
    {
        LLMRouteKind.Gateway => routeOptions.FirstOrDefault(option =>
            string.Equals(option.Source, UserLlmRouteSource.GatewayProvider, StringComparison.Ordinal)),
        LLMRouteKind.NyxIdUserService => routeOptions.FirstOrDefault(option =>
            string.Equals(option.UserServiceId, selection.NyxIdUserServiceId, StringComparison.Ordinal)),
        _ => null,
    };

    private static IReadOnlyList<UserLlmModelGroup> BuildModelGroups(
        IReadOnlyList<NyxIdLlmService> services)
    {
        var groups = new List<UserLlmModelGroup>();
        foreach (var service in services)
        {
            if (service.ModelCatalog.Certainty != LLMModelCatalogCertainty.Enumerated)
                continue;

            var route = IsGatewayRouteService(service)
                ? UserConfigLlmRouteDefaults.Gateway
                : UserLlmPreferenceWriteCore.NormalizeOptional(service.RouteValue) ?? string.Empty;
            var groupId = InventoryUserServiceId(service) ??
                          UserLlmPreferenceWriteCore.NormalizeOptional(service.CatalogEntryId) ??
                          service.ServiceSlug;
            groups.Add(new UserLlmModelGroup(
                RouteValue: route,
                GroupId: groupId,
                Label: NormalizeDisplayName(service.DisplayName, service.ServiceSlug),
                Models: service.ModelCatalog.ModelIds.ToArray()));
        }

        return groups
            .GroupBy(group => $"{group.RouteValue}\u001f{group.GroupId}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsGatewayRouteService(NyxIdLlmService service) =>
        UserLlmCatalogNormalization.NormalizeSource(service.Source) ==
        UserLlmRouteSourceValue.GatewayProvider;

    private static bool IsUserServiceRoute(NyxIdLlmService service) =>
        UserLlmCatalogNormalization.NormalizeSource(service.Source).IsUserServiceRoute;

    private static string? InventoryUserServiceId(NyxIdLlmService service) =>
        service.Identity is
        {
            Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory,
        } identity
            ? UserLlmPreferenceWriteCore.NormalizeOptional(identity.NyxIdUserServiceId)
            : null;

    private string ResolveSavedRouteLabel(
        LLMSelection? selection,
        IReadOnlyList<UserLlmRouteOption> routeOptions)
    {
        if (selection is null || selection.RouteKind == LLMRouteKind.Unspecified)
            return string.Empty;
        if (selection.RouteKind == LLMRouteKind.Gateway)
            return _gatewayRouteLabel;

        return FindSavedOption(selection, routeOptions)?.Label ??
               UserLlmPreferenceWriteCore.NormalizeOptional(selection.ServiceSlugSnapshot) ??
               UserLlmPreferenceWriteCore.NormalizeOptional(selection.RouteValue) ??
               string.Empty;
    }

    private static string NormalizeDisplayName(string? displayName, string fallback)
    {
        var normalized = UserLlmPreferenceWriteCore.NormalizeOptional(displayName);
        return normalized ?? fallback.Trim();
    }

    private static LLMModelCatalog UnavailableCatalog(LLMModelCatalogDiagnosticKind diagnostic) => new()
    {
        Certainty = LLMModelCatalogCertainty.Unavailable,
        DiagnosticKind = diagnostic,
    };

    private enum PersistedSelectionStatus
    {
        SystemDefault,
        Ready,
        LegacyRepairRequired,
    }

    private sealed record SelectionClassification(
        UserLlmSelectionStatus Status,
        LLMModelCatalogDiagnosticKind Diagnostic,
        UserLlmRemediationKind Remediation);
}
