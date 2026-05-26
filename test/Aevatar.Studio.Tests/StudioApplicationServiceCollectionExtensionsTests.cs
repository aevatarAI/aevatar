using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioApplicationServiceCollectionExtensionsTests
{
    [Fact]
    public void AddStudioApplication_ShouldRegisterAuthoritativeTeamEntryMemberResolver()
    {
        var services = new ServiceCollection();

        services.AddStudioApplication();

        services.Should().ContainSingle(x => x.ServiceType == typeof(ITeamEntryMemberResolver))
            .Which.ImplementationType.Should().Be(typeof(StudioTeamEntryMemberResolver));
        services.Should().ContainSingle(x => x.ServiceType == typeof(IStudioTeamGAgentStreamInvocationService))
            .Which.ImplementationType.Should().Be(typeof(StudioTeamGAgentStreamInvocationService));
        services.Should().ContainSingle(x => x.ServiceType == typeof(IUserConfigService))
            .Which.ImplementationType.Should().Be(typeof(UserConfigService));
    }
}
