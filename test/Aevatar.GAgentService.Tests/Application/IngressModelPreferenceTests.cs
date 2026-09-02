using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class IngressModelPreferenceTests
{
    [Fact]
    public void ResolveModel_WhenExplicitCallerModelIsQualified_ShouldWinOutright()
    {
        var resolved = IngressModelPreference.ResolveModel(
            explicitCallerModel: "chrono-llm/gpt-5.5",
            accountDefaultModel: "account/model-a",
            routePolicyForwardModel: "route/model-b",
            deploymentDefaultModel: "deployment-default");

        resolved.Should().Be("chrono-llm/gpt-5.5");
    }

    [Fact]
    public void ResolveModel_WhenExplicitCallerModelIsQualified_ShouldBeTrimmed()
    {
        var resolved = IngressModelPreference.ResolveModel(
            explicitCallerModel: "  chrono-llm/gpt-5.5  ",
            accountDefaultModel: null,
            routePolicyForwardModel: null,
            deploymentDefaultModel: "deployment-default");

        resolved.Should().Be("chrono-llm/gpt-5.5");
    }

    [Fact]
    public void ResolveModel_WhenExplicitCallerModelIsBare_ShouldFallThroughToAccountPreference()
    {
        var resolved = IngressModelPreference.ResolveModel(
            explicitCallerModel: "gpt-4o-mini",
            accountDefaultModel: "account/model-a",
            routePolicyForwardModel: "route/model-b",
            deploymentDefaultModel: "deployment-default");

        resolved.Should().Be("account/model-a");
    }

    [Fact]
    public void ResolveModel_WhenExplicitCallerModelIsBareAndNoOtherPreference_ShouldFallThroughToDeploymentDefault()
    {
        // The deployment default IS the bare caller value after request normalization,
        // so an under-specified caller model ends up as the chain tail rather than winning.
        var resolved = IngressModelPreference.ResolveModel(
            explicitCallerModel: "gpt-4o-mini",
            accountDefaultModel: null,
            routePolicyForwardModel: null,
            deploymentDefaultModel: "gpt-4o-mini");

        resolved.Should().Be("gpt-4o-mini");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveModel_WhenCallerModelIsBlank_ShouldUseAccountPreference(string? callerModel)
    {
        var resolved = IngressModelPreference.ResolveModel(
            explicitCallerModel: callerModel,
            accountDefaultModel: "account/model-a",
            routePolicyForwardModel: "route/model-b",
            deploymentDefaultModel: "deployment-default");

        resolved.Should().Be("account/model-a");
    }

    [Fact]
    public void ResolveModel_WhenAccountPreferenceIsBare_ShouldStillWinOverRouteAndDeployment()
    {
        // Account preference is normalized (not slug-qualified) so any non-blank account
        // value wins over downstream route/deployment fallbacks.
        var resolved = IngressModelPreference.ResolveModel(
            explicitCallerModel: null,
            accountDefaultModel: "  account-bare-model  ",
            routePolicyForwardModel: "route/model-b",
            deploymentDefaultModel: "deployment-default");

        resolved.Should().Be("account-bare-model");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveModel_WhenAccountPreferenceIsBlank_ShouldUseRoutePolicyForwardModel(string? accountModel)
    {
        var resolved = IngressModelPreference.ResolveModel(
            explicitCallerModel: null,
            accountDefaultModel: accountModel,
            routePolicyForwardModel: "  route/model-b  ",
            deploymentDefaultModel: "deployment-default");

        resolved.Should().Be("route/model-b");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveModel_WhenOnlyDeploymentDefaultIsAvailable_ShouldUseDeploymentDefault(string? blank)
    {
        var resolved = IngressModelPreference.ResolveModel(
            explicitCallerModel: blank,
            accountDefaultModel: blank,
            routePolicyForwardModel: blank,
            deploymentDefaultModel: "deployment-default");

        resolved.Should().Be("deployment-default");
    }

    [Fact]
    public void ResolveModel_ShouldNotTrimDeploymentDefault()
    {
        // The deployment default is documented as already-normalized; it is returned verbatim
        // as the guaranteed chain tail.
        var resolved = IngressModelPreference.ResolveModel(
            explicitCallerModel: null,
            accountDefaultModel: null,
            routePolicyForwardModel: null,
            deploymentDefaultModel: "  deployment-default  ");

        resolved.Should().Be("  deployment-default  ");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ShouldReturnNull_ForBlankValues(string? value)
    {
        IngressModelPreference.Normalize(value).Should().BeNull();
    }

    [Theory]
    [InlineData("gpt-4o-mini", "gpt-4o-mini")]
    [InlineData("  vendor/model  ", "vendor/model")]
    public void Normalize_ShouldTrimAndReturnNonBlankValues(string value, string expected)
    {
        IngressModelPreference.Normalize(value).Should().Be(expected);
    }
}
