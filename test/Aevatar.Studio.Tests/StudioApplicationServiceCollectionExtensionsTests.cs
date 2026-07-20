using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Application.Studio.WorkflowBoards;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class StudioApplicationServiceCollectionExtensionsTests
{
    [Fact]
    public void AddStudioApplication_ShouldAliasAutomationQueryAndMutationPortsToOneSingleton()
    {
        var services = new ServiceCollection();
        services.AddStudioApplication();

        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(StudioMemberWorkflowSchedulePort) &&
            x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IStudioMemberAutomationQueryPort) &&
            x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IStudioMemberWorkflowSchedulePort) &&
            x.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddStudioApplication_ShouldRegisterAuthoritativeTeamEntryMemberResolver()
    {
        var services = new ServiceCollection();

        services.AddStudioApplication();

        services.Should().ContainSingle(x => x.ServiceType == typeof(ITeamEntryMemberResolver))
            .Which.ImplementationType.Should().Be(typeof(StudioTeamEntryMemberResolver));
        services.Should().ContainSingle(x => x.ServiceType == typeof(IStudioTeamGAgentStreamInvocationService))
            .Which.ImplementationType.Should().Be(typeof(StudioTeamGAgentStreamInvocationService));
        services.Should().ContainSingle(x => x.ServiceType == typeof(IWorkflowBoardSnapshotQueryPort));
        services.Should().ContainSingle(x => x.ServiceType == typeof(IWorkflowBoardRosterQueryPort));
        services.Should().ContainSingle(x => x.ServiceType == typeof(IWorkflowBoardClock));
        services.Should().ContainSingle(x => x.ServiceType == typeof(IUserConfigService))
            .Which.ImplementationType.Should().Be(typeof(UserConfigService));
    }
}
