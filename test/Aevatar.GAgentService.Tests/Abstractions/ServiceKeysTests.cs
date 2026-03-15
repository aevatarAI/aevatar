using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Abstractions;

public sealed class ServiceKeysTests
{
    [Fact]
    public void Build_ShouldTrimAllIdentitySegments()
    {
        var key = ServiceKeys.Build(new ServiceIdentity
        {
            TenantId = " tenant ",
            AppId = " app ",
            Namespace = " ns ",
            ServiceId = " svc ",
        });

        key.Should().Be("tenant:app:ns:svc");
    }

    [Fact]
    public void Build_ShouldRejectNullIdentity()
    {
        var act = () => ServiceKeys.Build((ServiceIdentity)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_ShouldRejectBlankSegments()
    {
        var act = () => ServiceKeys.Build("tenant", "app", "ns", " ");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("serviceId is required.");
    }

    [Fact]
    public void Build_ShouldRejectBlankTenantId()
    {
        var act = () => ServiceKeys.Build(" ", "app", "ns", "svc");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("tenantId is required.");
    }

    [Fact]
    public void Build_ShouldFormatExplicitSegments()
    {
        var key = ServiceKeys.Build("tenant", "app", "default", "svc");

        key.Should().Be("tenant:app:default:svc");
    }
}
