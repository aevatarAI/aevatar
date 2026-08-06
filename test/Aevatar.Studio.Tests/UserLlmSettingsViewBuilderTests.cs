using Aevatar.AI.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class UserLlmSettingsViewBuilderTests
{
    private readonly UserLlmSettingsViewBuilder _builder = new("NyxID Gateway");

    [Fact]
    public void BuildAvailable_WithoutSelectionOrCompatibilityValues_ShouldUseSystemDefault()
    {
        var view = _builder.BuildAvailable(Services(), Config());

        view.SavedSelection.Should().BeNull();
        view.SelectionStatus.Should().Be(UserLlmSelectionStatus.SystemDefault);
        view.Remediation.Should().Be(UserLlmRemediationKind.None);
    }

    [Fact]
    public void BuildAvailable_WithCompleteUnspecifiedSelection_ShouldUseSystemDefault()
    {
        var saved = SystemDefaultSelection();

        var view = _builder.BuildAvailable(Services(), Config(saved));

        view.SavedSelection.Should().BeEquivalentTo(saved);
        view.SavedSelection.Should().NotBeSameAs(saved);
        view.SelectionStatus.Should().Be(UserLlmSelectionStatus.SystemDefault);
    }

    [Fact]
    public void BuildAvailable_WithCompatibilityOnlySelection_ShouldRequireLegacyRepair()
    {
        var view = _builder.BuildAvailable(
            Services(),
            Config(selection: null, legacyRoute: "/api/v1/proxy/s/legacy", legacyModel: "gpt-legacy"));

        view.SelectionStatus.Should().Be(UserLlmSelectionStatus.LegacyRepairRequired);
        view.Remediation.Should().Be(UserLlmRemediationKind.Reselect);
    }

    [Fact]
    public void BuildAvailable_WithTypedRouteMissingModelSelection_ShouldRequireLegacyRepair()
    {
        var saved = new LLMSelection
        {
            RouteKind = LLMRouteKind.Gateway,
            RouteValue = UserConfigLlmRouteDefaults.Gateway,
        };

        var view = _builder.BuildAvailable(Services(Gateway()), Config(saved));

        view.SavedSelection.Should().BeEquivalentTo(saved);
        view.SelectionStatus.Should().Be(UserLlmSelectionStatus.LegacyRepairRequired);
        view.Remediation.Should().Be(UserLlmRemediationKind.Reselect);
    }

    [Fact]
    public void BuildAvailable_WithSavedUnavailableService_ShouldPreserveSelectionAndRequireRepair()
    {
        var saved = UserServiceSelection("us-alpha", "chrono-llm-public", "gpt-5.5");
        var view = _builder.BuildAvailable(
            Services(UserService("us-alpha", "chrono-llm-public", UnavailableCatalog())),
            Config(saved));

        view.SavedSelection.Should().BeEquivalentTo(saved);
        view.SavedSelection.Should().NotBeSameAs(saved);
        view.SelectionStatus.Should().Be(UserLlmSelectionStatus.NeedsRepair);
        view.CatalogDiagnostic.Should().Be(LLMModelCatalogDiagnosticKind.AccessDenied);
        view.Remediation.Should().Be(UserLlmRemediationKind.ChooseReplacement);
    }

    [Fact]
    public void BuildAvailable_WithSavedReadyServiceAndExactModel_ShouldBeReady()
    {
        var saved = UserServiceSelection("us-alpha", "chrono-llm-public", "MODEL-A");
        var view = _builder.BuildAvailable(
            Services(UserService(
                "us-alpha",
                "chrono-llm-public",
                EnumeratedCatalog("MODEL-A", "model-a"))),
            Config(saved));

        view.SelectionStatus.Should().Be(UserLlmSelectionStatus.Ready);
        view.CatalogDiagnostic.Should().Be(LLMModelCatalogDiagnosticKind.Unspecified);
        view.Remediation.Should().Be(UserLlmRemediationKind.None);
    }

    [Fact]
    public void BuildAvailable_WithCaseMismatchedExplicitModel_ShouldRequireRepair()
    {
        var saved = UserServiceSelection("us-alpha", "chrono-llm-public", "Model-A");
        var view = _builder.BuildAvailable(
            Services(UserService(
                "us-alpha",
                "chrono-llm-public",
                EnumeratedCatalog("MODEL-A", "model-a"))),
            Config(saved));

        view.SelectionStatus.Should().Be(UserLlmSelectionStatus.NeedsRepair);
        view.Remediation.Should().Be(UserLlmRemediationKind.ChooseReplacement);
    }

    [Fact]
    public void BuildAvailable_WithExplicitGatewayMissingFromCatalog_ShouldNotSynthesizeReadyGateway()
    {
        var saved = GatewayProviderDefault();

        var view = _builder.BuildAvailable(Services(), Config(saved));

        view.SelectionStatus.Should().Be(UserLlmSelectionStatus.NeedsRepair);
        view.CatalogDiagnostic.Should().Be(LLMModelCatalogDiagnosticKind.RouteNotReady);
        view.Remediation.Should().Be(UserLlmRemediationKind.ConnectProvider);
        view.RouteOptions.Should().ContainSingle().Which.Should().Match<UserLlmRouteOption>(option =>
            option.RouteValue == UserConfigLlmRouteDefaults.Gateway &&
            !option.Allowed &&
            !option.Ready &&
            option.ModelCatalog.Certainty == LLMModelCatalogCertainty.Unavailable);
    }

    [Fact]
    public void BuildVerificationUnavailable_WithValidSavedSelection_ShouldReportVerificationUnavailable()
    {
        var saved = GatewayProviderDefault();

        var view = _builder.BuildVerificationUnavailable(Config(saved));

        view.SavedSelection.Should().BeEquivalentTo(saved);
        view.SelectionStatus.Should().Be(UserLlmSelectionStatus.VerificationUnavailable);
        view.CatalogDiagnostic.Should().Be(LLMModelCatalogDiagnosticKind.ObservationUnavailable);
        view.Remediation.Should().Be(UserLlmRemediationKind.RetryCatalog);
    }

    [Fact]
    public void BuildAvailable_ShouldCloneCatalogsAtTheViewBoundary()
    {
        var catalog = EnumeratedCatalog("gpt-5.5");

        var view = _builder.BuildAvailable(
            Services(UserService("us-alpha", "chrono-llm-public", catalog)),
            Config());

        var exposed = view.RouteOptions.Single(option => option.UserServiceId == "us-alpha").ModelCatalog;
        exposed.Should().BeEquivalentTo(catalog);
        exposed.Should().NotBeSameAs(catalog);
    }

    private static UserConfig Config(
        LLMSelection? selection = null,
        string legacyRoute = "",
        string legacyModel = "") => new(
        DefaultModel: legacyModel,
        PreferredLlmRoute: legacyRoute,
        LlmSelection: selection);

    private static NyxIdLlmServicesResult Services(params NyxIdLlmService[] services) =>
        new(services, null);

    private static NyxIdLlmService Gateway() => new(
        CatalogEntryId: null,
        ServiceSlug: "gateway",
        DisplayName: "NyxID Gateway",
        RouteValue: UserConfigLlmRouteDefaults.Gateway,
        ModelCatalog: EnumeratedCatalog("gpt-5.5"),
        Status: UserLlmRouteStatus.Ready,
        Source: UserLlmRouteSource.GatewayProvider,
        Allowed: true,
        Description: null);

    private static NyxIdLlmService UserService(
        string id,
        string slug,
        LLMModelCatalog catalog) => new(
        CatalogEntryId: null,
        ServiceSlug: slug,
        DisplayName: slug,
        RouteValue: $"/api/v1/proxy/s/{slug}",
        ModelCatalog: catalog,
        Status: catalog.Certainty == LLMModelCatalogCertainty.Unavailable
            ? UserLlmRouteStatus.Unavailable
            : UserLlmRouteStatus.Ready,
        Source: UserLlmRouteSource.UserService,
        Allowed: catalog.Certainty != LLMModelCatalogCertainty.Unavailable,
        Description: null,
        Identity: new UserLlmServiceIdentity(
            UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            id));

    private static LLMModelCatalog EnumeratedCatalog(params string[] modelIds)
    {
        var catalog = new LLMModelCatalog
        {
            Certainty = LLMModelCatalogCertainty.Enumerated,
            DefaultModelId = modelIds[0],
        };
        catalog.ModelIds.Add(modelIds.Order(StringComparer.Ordinal));
        return catalog;
    }

    private static LLMModelCatalog UnavailableCatalog() => new()
    {
        Certainty = LLMModelCatalogCertainty.Unavailable,
        DiagnosticKind = LLMModelCatalogDiagnosticKind.AccessDenied,
    };

    private static LLMSelection SystemDefaultSelection() => new()
    {
        ModelSelection = new LLMModelSelection
        {
            Kind = LLMModelSelectionKind.Unspecified,
        },
    };

    private static LLMSelection GatewayProviderDefault() => new()
    {
        RouteKind = LLMRouteKind.Gateway,
        RouteValue = UserConfigLlmRouteDefaults.Gateway,
        ModelSelection = new LLMModelSelection
        {
            Kind = LLMModelSelectionKind.ProviderDefault,
        },
    };

    private static LLMSelection UserServiceSelection(string id, string slug, string model) => new()
    {
        RouteKind = LLMRouteKind.NyxIdUserService,
        RouteValue = $"/api/v1/proxy/s/{slug}",
        NyxIdUserServiceId = id,
        ServiceSlugSnapshot = slug,
        ModelSelection = new LLMModelSelection
        {
            Kind = LLMModelSelectionKind.ExplicitModel,
            ModelId = model,
        },
    };
}
