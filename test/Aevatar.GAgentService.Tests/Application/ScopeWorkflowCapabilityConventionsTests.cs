using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Application.Workflows;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeWorkflowCapabilityConventionsTests
{
    private static readonly ScopeWorkflowCapabilityOptions DefaultOptions = new()
    {
        DefaultServiceId = "default",
        ServiceNamespace = "default",
        // ServiceAppId getter is pinned to FixedServiceAppId = "default"
    };

    // ── ResolveAppId ──────────────────────────────────────────────────────────

    [Fact]
    public void ResolveAppId_ShouldReturnProvidedAppId_WhenNonEmpty()
    {
        var result = ScopeWorkflowCapabilityConventions.ResolveAppId(DefaultOptions, "my-app");

        result.Should().Be("my-app");
    }

    [Fact]
    public void ResolveAppId_ShouldTrimProvidedAppId_WhenItHasLeadingOrTrailingWhitespace()
    {
        var result = ScopeWorkflowCapabilityConventions.ResolveAppId(DefaultOptions, "  my-app  ");

        result.Should().Be("my-app");
    }

    [Fact]
    public void ResolveAppId_ShouldFallbackToFixedAppId_WhenAppIdIsNull()
    {
        var result = ScopeWorkflowCapabilityConventions.ResolveAppId(DefaultOptions, null);

        result.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
    }

    [Fact]
    public void ResolveAppId_ShouldFallbackToFixedAppId_WhenAppIdIsEmpty()
    {
        var result = ScopeWorkflowCapabilityConventions.ResolveAppId(DefaultOptions, string.Empty);

        result.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("  \n  ")]
    public void ResolveAppId_ShouldFallbackToFixedAppId_WhenAppIdIsWhitespace(string whitespace)
    {
        var result = ScopeWorkflowCapabilityConventions.ResolveAppId(DefaultOptions, whitespace);

        result.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
    }

    [Fact]
    public void ResolveAppId_ShouldThrow_WhenOptionsIsNull()
    {
        var act = () => ScopeWorkflowCapabilityConventions.ResolveAppId(null!, "app");

        act.Should().Throw<ArgumentNullException>();
    }

    // ── BuildServiceIdentity ─────────────────────────────────────────────────

    [Fact]
    public void BuildServiceIdentity_ShouldUseCustomAppId_WhenProvided()
    {
        var identity = ScopeWorkflowCapabilityConventions.BuildServiceIdentity(
            DefaultOptions, "scope-x", "svc-1", "custom-app");

        identity.AppId.Should().Be("custom-app");
        identity.TenantId.Should().Be("scope-x");
        identity.ServiceId.Should().Be("svc-1");
        identity.Namespace.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceNamespace);
    }

    [Fact]
    public void BuildServiceIdentity_ShouldFallbackToFixedAppId_WhenAppIdIsNull()
    {
        var identity = ScopeWorkflowCapabilityConventions.BuildServiceIdentity(
            DefaultOptions, "scope-x", "svc-1", null);

        identity.AppId.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
    }

    [Fact]
    public void BuildServiceIdentity_ShouldFallbackToFixedAppId_WhenAppIdIsWhitespace()
    {
        var identity = ScopeWorkflowCapabilityConventions.BuildServiceIdentity(
            DefaultOptions, "scope-x", "svc-1", "   ");

        identity.AppId.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
    }

    // ── BuildDefaultServiceIdentity ──────────────────────────────────────────

    [Fact]
    public void BuildDefaultServiceIdentity_ShouldUseCustomAppId_WhenProvided()
    {
        var identity = ScopeWorkflowCapabilityConventions.BuildDefaultServiceIdentity(
            DefaultOptions, "scope-y", "tenant-app");

        identity.AppId.Should().Be("tenant-app");
        identity.ServiceId.Should().Be(DefaultOptions.DefaultServiceId);
        identity.TenantId.Should().Be("scope-y");
    }

    [Fact]
    public void BuildDefaultServiceIdentity_ShouldFallbackToFixedAppId_WhenAppIdIsNull()
    {
        var identity = ScopeWorkflowCapabilityConventions.BuildDefaultServiceIdentity(
            DefaultOptions, "scope-y", null);

        identity.AppId.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
    }

    // ── BuildIdentity (workflow variant) ─────────────────────────────────────

    [Fact]
    public void BuildIdentity_ShouldUseCustomAppId_WhenProvided()
    {
        var identity = ScopeWorkflowCapabilityConventions.BuildIdentity(
            DefaultOptions, "scope-z", "my-workflow", "wf-app");

        identity.AppId.Should().Be("wf-app");
        identity.TenantId.Should().Be("scope-z");
        identity.ServiceId.Should().Be("my-workflow");
    }

    [Fact]
    public void BuildIdentity_ShouldFallbackToFixedAppId_WhenAppIdIsNull()
    {
        var identity = ScopeWorkflowCapabilityConventions.BuildIdentity(
            DefaultOptions, "scope-z", "my-workflow", null);

        identity.AppId.Should().Be(ScopeWorkflowCapabilityOptions.FixedServiceAppId);
    }
}
