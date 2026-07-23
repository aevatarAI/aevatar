using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class UserLlmSettingsViewBuilderTests
{
    private const string SharedRoute = "/api/v1/proxy/s/shared-llm";
    private readonly UserLlmSettingsViewBuilder _builder = new("NyxID Gateway");

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
