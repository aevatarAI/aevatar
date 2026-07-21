using System.Reflection;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgents.WorkOrder;
using Aevatar.Studio.Application.Provisioning;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Application.Studio.WorkflowBoards;
using Aevatar.Studio.Hosting;
using Aevatar.Studio.Hosting.WorkOrders;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    public void AddStudioApplication_ShouldResolveDefaultAutomationPortsToSameSingleton()
    {
        var services = new ServiceCollection();
        AddSchedulePortDependencies(services);
        services.AddStudioApplication();

        using var provider = services.BuildServiceProvider();
        var concretePort = provider.GetRequiredService<StudioMemberWorkflowSchedulePort>();
        var mutationPort = provider.GetRequiredService<IStudioMemberWorkflowSchedulePort>();
        var queryPort = provider.GetRequiredService<IStudioMemberAutomationQueryPort>();

        mutationPort.Should().BeSameAs(concretePort);
        queryPort.Should().BeSameAs(mutationPort);
    }

    [Fact]
    public void AddStudioApplication_ShouldResolveAutomationQueryThroughHostMutationPortOverride()
    {
        var services = new ServiceCollection();
        var hostMutationPort = Stub<IStudioMemberWorkflowSchedulePort>();
        services.AddSingleton(hostMutationPort);
        services.AddStudioApplication();

        using var provider = services.BuildServiceProvider();
        var mutationPort = provider.GetRequiredService<IStudioMemberWorkflowSchedulePort>();
        var queryPort = provider.GetRequiredService<IStudioMemberAutomationQueryPort>();

        mutationPort.Should().BeSameAs(hostMutationPort);
        queryPort.Should().BeSameAs(hostMutationPort);
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

    [Fact]
    public void AddStudioApplication_ShouldAliasWorkOrderSchedulerAndRegisterQueueSingleton()
    {
        var services = new ServiceCollection();

        services.AddStudioApplication();

        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IWorkOrderExecutionQueue) &&
            x.ImplementationType == typeof(WorkOrderExecutionQueue) &&
            x.Lifetime == ServiceLifetime.Singleton);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IWorkOrderExecutionScheduler>().Should().BeSameAs(
            provider.GetRequiredService<WorkOrderExecutionScheduler>());
        var queue = provider.GetRequiredService<IWorkOrderExecutionQueue>();
        provider.GetRequiredService<IWorkOrderExecutionQueue>().Should().BeSameAs(queue);
    }

    [Fact]
    public void AddStudioHostingCore_ShouldRegisterWorkOrderExecutionWorker()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddStudioHostingCore(configuration);

        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkOrderExecutionWorker));
    }

    private static void AddSchedulePortDependencies(IServiceCollection services)
    {
        services.AddSingleton(Stub<IStudioMemberService>());
        services.AddSingleton(Stub<IScheduledDispatchApplicationService>());
        services.AddSingleton(Stub<IScheduledInvocationAuthorizationPlanner>());
        services.AddSingleton(Stub<IScheduledInvocationAuthorizationRevalidator>());
        services.AddSingleton(Stub<IStudioScheduledCredentialMaterializer>());
    }

    private static T Stub<T>() where T : class =>
        DispatchProxy.Create<T, ThrowingDispatchProxy>();

    public class ThrowingDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} is not used by this DI test.");
    }
}
