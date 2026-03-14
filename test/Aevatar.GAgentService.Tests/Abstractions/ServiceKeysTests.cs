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
}
