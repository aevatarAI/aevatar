using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class UserLlmSettingsViewBuilderTests
{
    private const string SharedRoute = "/api/v1/proxy/s/shared-llm";
    private readonly UserLlmSettingsViewBuilder _builder = new("NyxID Gateway");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildAvailable_WithoutCommittedSelection_ShouldKeepSavedRouteUnspecified(
        bool useUnspecifiedSelection)
    {
        var selection = useUnspecifiedSelection
            ? new UserLlmSelectionValue(
                UserLlmSelectionKind.Unspecified,
                UserConfigLlmRouteDefaults.Gateway,
                "us-legacy",
                "legacy")
            : null;

        var view = _builder.BuildAvailable(
            new NyxIdLlmServicesResult([], null),
            selection,
            string.Empty,
            string.Empty);

        view.SavedRouteKind.Should().Be(UserLlmSelectionKindWire.Unspecified);
        view.SavedRoute.Should().BeEmpty();
        view.SavedUserServiceId.Should().BeNull();
        view.SavedServiceSlug.Should().BeNull();
        view.EffectiveRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        view.RouteFallbackActive.Should().BeFalse();
        view.FallbackReason.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildUnavailable_WithoutCommittedSelection_ShouldExposeOnlyEffectiveGatewayFallback(
        bool useUnspecifiedSelection)
    {
        var selection = useUnspecifiedSelection
            ? new UserLlmSelectionValue(
                UserLlmSelectionKind.Unspecified,
                "/api/v1/proxy/s/legacy",
                "us-legacy",
                "legacy")
            : null;

        var view = _builder.BuildUnavailable(
            selection,
            "/api/v1/proxy/s/legacy",
            string.Empty);

        view.SavedRouteKind.Should().Be(UserLlmSelectionKindWire.Unspecified);
        view.SavedRoute.Should().BeEmpty();
        view.SavedRouteLabel.Should().BeEmpty();
        view.EffectiveRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        view.EffectiveRouteLabel.Should().Be("NyxID Gateway");
        view.FallbackReason.Should().Be(UserLlmFallbackReason.CatalogUnavailable);
        view.RouteOptions.Should().ContainSingle().Which.Should().Match<UserLlmRouteOption>(option =>
            option.RouteValue == UserConfigLlmRouteDefaults.Gateway &&
            option.Source == UserLlmRouteSource.GatewayProvider &&
            !option.Allowed &&
            !option.Ready);
    }

    [Fact]
    public void BuildAvailable_WithTypedServiceMissingIdAndGatewayRoute_ShouldMarkSelectionUnavailable()
    {
        var view = Build(new UserLlmSelectionValue(
            UserLlmSelectionKind.NyxIdUserService,
            UserConfigLlmRouteDefaults.Gateway,
            " ",
            "shared-llm"));

        view.SavedRouteKind.Should().Be(UserLlmSelectionKindWire.NyxIdUserService);
        view.SavedUserServiceId.Should().BeNull();
        view.RouteFallbackActive.Should().BeTrue();
        view.FallbackReason.Should().Be(UserLlmFallbackReason.SavedRouteUnavailable);
    }

    [Fact]
    public void BuildAvailable_WithTypedServiceMissingIdAndDuplicateRoute_ShouldNotMatchByRoute()
    {
        var view = Build(new UserLlmSelectionValue(
            UserLlmSelectionKind.NyxIdUserService,
            SharedRoute,
            string.Empty,
            "shared-llm"));

        view.SavedRoute.Should().Be(SharedRoute);
        view.SavedUserServiceId.Should().BeNull();
        view.EffectiveRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        view.RouteFallbackActive.Should().BeTrue();
    }

    [Fact]
    public void BuildAvailable_WithTypedGatewayAndServiceRouteSnapshot_ShouldUseCanonicalGateway()
    {
        var view = Build(new UserLlmSelectionValue(
            UserLlmSelectionKind.Gateway,
            SharedRoute,
            "us-alpha",
            "shared-llm"));

        view.SavedRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        view.SavedRouteKind.Should().Be(UserLlmSelectionKindWire.Gateway);
        view.SavedUserServiceId.Should().BeNull();
        view.SavedServiceSlug.Should().BeNull();
        view.EffectiveRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        view.RouteFallbackActive.Should().BeFalse();
    }

    [Fact]
    public void BuildAvailable_WithValidTypedServiceAndDuplicateRoute_ShouldResolveExactInventoryId()
    {
        var view = Build(new UserLlmSelectionValue(
            UserLlmSelectionKind.NyxIdUserService,
            SharedRoute,
            "us-beta",
            "shared-llm"));

        view.SavedUserServiceId.Should().Be("us-beta");
        view.SavedRouteLabel.Should().Be("Beta service");
        view.EffectiveRoute.Should().Be(SharedRoute);
        view.RouteFallbackActive.Should().BeFalse();
    }

    private UserLlmSettingsView Build(UserLlmSelectionValue selection) =>
        _builder.BuildAvailable(
            new NyxIdLlmServicesResult(
                [
                    InventoryService("us-alpha", "Alpha service"),
                    InventoryService("us-beta", "Beta service"),
                ],
                null),
            selection,
            selection.RouteValue,
            "gpt-5.5");

    private static NyxIdLlmService InventoryService(string id, string displayName) => new(
        CatalogEntryId: null,
        ServiceSlug: "shared-llm",
        DisplayName: displayName,
        RouteValue: SharedRoute,
        DefaultModel: "gpt-5.5",
        Models: ["gpt-5.5"],
        Status: UserLlmRouteStatus.Ready,
        Source: UserLlmRouteSource.UserService,
        Allowed: true,
        Description: null,
        Identity: new UserLlmServiceIdentity(
            UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            id));
}
