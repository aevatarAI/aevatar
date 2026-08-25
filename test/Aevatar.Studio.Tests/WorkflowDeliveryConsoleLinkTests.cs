using Aevatar.Studio.Application.Delivery;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowDeliveryConsoleLinkTests
{
    [Theory]
    [InlineData("https://aevatar-console.aevatar.ai")]
    [InlineData("https://aevatar-console.aevatar.ai/")]
    [InlineData("https://aevatar-console.aevatar.ai/console")]
    [InlineData("http://localhost:8000")]
    [InlineData("http://127.0.0.1:8000")]
    public void TryNormalizeBaseUrl_ShouldAcceptHttpsAndLoopbackHttpOrigins(string value)
    {
        WorkflowDeliveryConsoleLink.TryNormalizeBaseUrl(value, out var normalized).Should().BeTrue();
        normalized.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/scopes/scope-a/teams/team-a/members/member-a/workflow")]
    [InlineData("aevatar-console.aevatar.ai")]
    [InlineData("http://aevatar-console.aevatar.ai")]
    [InlineData("https://user:secret@aevatar-console.aevatar.ai")]
    [InlineData("https://aevatar-console.aevatar.ai?next=/scopes")]
    [InlineData("https://aevatar-console.aevatar.ai#/scopes")]
    [InlineData("ftp://aevatar-console.aevatar.ai")]
    public void TryNormalizeBaseUrl_ShouldRejectRelativeNonHttpsAndDecoratedValues(string value)
    {
        WorkflowDeliveryConsoleLink.TryNormalizeBaseUrl(value, out _).Should().BeFalse();
    }

    [Fact]
    public void BuildMemberInvokeUrl_ShouldProduceAnAbsoluteConsoleUrl()
    {
        WorkflowDeliveryConsoleLink.TryNormalizeBaseUrl("https://aevatar-console.aevatar.ai", out var baseUri)
            .Should().BeTrue();

        var url = WorkflowDeliveryConsoleLink.BuildMemberInvokeUrl(baseUri, "scope-a", "team-a", "member-a");

        url.Should().Be("https://aevatar-console.aevatar.ai/scopes/scope-a/teams/team-a/members/member-a/invoke");
    }

    [Fact]
    public void BuildMemberInvokeUrl_ShouldPreserveAConfiguredPathBase()
    {
        WorkflowDeliveryConsoleLink.TryNormalizeBaseUrl("https://aevatar.ai/console/", out var baseUri)
            .Should().BeTrue();

        var url = WorkflowDeliveryConsoleLink.BuildMemberInvokeUrl(baseUri, "scope-a", "team-a", "member-a");

        url.Should().Be("https://aevatar.ai/console/scopes/scope-a/teams/team-a/members/member-a/invoke");
    }

    [Fact]
    public void BuildMemberInvokeUrl_ShouldEscapeIdentitySegments()
    {
        WorkflowDeliveryConsoleLink.TryNormalizeBaseUrl("https://aevatar-console.aevatar.ai", out var baseUri)
            .Should().BeTrue();

        var url = WorkflowDeliveryConsoleLink.BuildMemberInvokeUrl(baseUri, "scope a", "team/a", "member?a");

        url.Should().Be("https://aevatar-console.aevatar.ai/scopes/scope%20a/teams/team%2Fa/members/member%3Fa/invoke");
    }

    [Fact]
    public void BuildMemberInvokeUrl_WhenConsoleOriginIsUnconfigured_ShouldReturnNullRatherThanARelativePath()
    {
        WorkflowDeliveryConsoleLink.BuildMemberInvokeUrl(null, "scope-a", "team-a", "member-a")
            .Should().BeNull();
    }

    [Fact]
    public void BuildMemberInvokeUrl_WhenMemberIsNotProvisionedYet_ShouldReturnNull()
    {
        WorkflowDeliveryConsoleLink.TryNormalizeBaseUrl("https://aevatar-console.aevatar.ai", out var baseUri)
            .Should().BeTrue();

        WorkflowDeliveryConsoleLink.BuildMemberInvokeUrl(baseUri, "scope-a", "team-a", null)
            .Should().BeNull();
    }
}
