using System.Reflection;
using Aevatar.GAgents.WorkOrder;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Delivery;
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
using Microsoft.Extensions.Options;

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
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(ILLMModelCatalogPolicyApplicationService) &&
            x.ImplementationType == typeof(LLMModelCatalogPolicyApplicationService) &&
            x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(ILLMModelDiscoveryApplicationService) &&
            x.ImplementationType == typeof(LLMModelDiscoveryApplicationService) &&
            x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(ILLMModelRouteApplicationService) &&
            x.ImplementationType == typeof(LLMModelRouteApplicationService) &&
            x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IScopeWorkflowPublishedServiceDescriptorSource) &&
            x.ImplementationType == typeof(StudioMemberScopeWorkflowDescriptorSource) &&
            x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IScopeWorkflowPublishedServiceDescriptorSource) &&
            x.ImplementationType == typeof(CatalogueScopeWorkflowDescriptorSource) &&
            x.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddStudioApplication_ShouldRegisterActorOwnedWorkflowScheduleProvisioningServices()
    {
        var services = new ServiceCollection();

        services.AddStudioApplication();

        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IStudioWorkflowScheduleProvisioningExecutor) &&
            x.ImplementationType == typeof(StudioWorkflowScheduleProvisioningExecutor) &&
            x.Lifetime == ServiceLifetime.Singleton);
        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IWorkflowScheduleProvisioningPort) &&
            x.ImplementationType == typeof(WorkflowScheduleProvisioningPort) &&
            x.Lifetime == ServiceLifetime.Singleton);
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

    [Fact]
    public void AddStudioHostingCore_WhenDeliverySectionIsMissing_ShouldUseShippedWorkflowAllowlist()
    {
        var options = ResolveDeliveryOptions(new ConfigurationBuilder().Build());

        options.AllowedWorkflowNames.Should().Equal(
            "hr_onboarding_email_approval",
            "hr_monthly_attendance_approval",
            "hr_attendance_fill_reminder",
            "fin_invoice_precheck_approval",
            "fin_budget_variance_monitor");
    }

    [Fact]
    public void AddStudioHostingCore_WhenDeliveryAllowlistIsConfigured_ShouldPreserveExactSubset()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream("""
                {"Aevatar":{"Delivery":{"UseShippedWorkflowAllowlist":true}}}
                """u8.ToArray()))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:AllowedWorkflowNames:0"] =
                    "fin_invoice_precheck_approval",
                [$"{WorkflowDeliveryOptions.SectionName}:AllowedWorkflowNames:1"] =
                    "hr_onboarding_email_approval",
            })
            .Build();

        var options = ResolveDeliveryOptions(configuration);

        options.AllowedWorkflowNames.Should().Equal(
            "fin_invoice_precheck_approval",
            "hr_onboarding_email_approval");
    }

    [Fact]
    public void AddStudioHostingCore_WhenDeliveryAllowlistIsExplicitlyEmpty_ShouldRemainEmpty()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream("""
                {"Aevatar":{"Delivery":{"UseShippedWorkflowAllowlist":true}}}
                """u8.ToArray()))
            .AddJsonStream(new MemoryStream("""
                {"Aevatar":{"Delivery":{"AllowedWorkflowNames":[]}}}
                """u8.ToArray()))
            .Build();

        var options = ResolveDeliveryOptions(configuration);

        options.AllowedWorkflowNames.Should().BeEmpty();
    }

    [Fact]
    public void AddStudioHostingCore_WhenShippedAllowlistIsExplicitlyEnabled_ShouldUseShippedWorkflows()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:UseShippedWorkflowAllowlist"] = "true",
            })
            .Build();

        var options = ResolveDeliveryOptions(configuration);

        options.AllowedWorkflowNames.Should().Equal(WorkflowDeliveryOptions.ShippedWorkflowNames);
    }

    [Fact]
    public void AddStudioHostingCore_WhenDeliverySectionOmitsAllowlistAndOptIn_ShouldRemainEmpty()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:PackageDirectory"] = "delivery-workflows",
            })
            .Build();

        var options = ResolveDeliveryOptions(configuration);

        options.AllowedWorkflowNames.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://aevatar-console.aevatar.ai")]
    [InlineData("http://localhost:8000")]
    public void AddStudioHostingCore_WhenConsoleWebBaseUrlIsAValidOrigin_ShouldKeepIt(string value)
    {
        var options = ResolveDeliveryOptions(DeliveryConfiguration("ConsoleWebBaseUrl", value));

        options.ConsoleWebBaseUrl.Should().Be(value);
    }

    [Fact]
    public void AddStudioHostingCore_WhenConsoleWebBaseUrlIsUnset_ShouldRemainEmpty()
    {
        var options = ResolveDeliveryOptions(new ConfigurationBuilder().Build());

        options.ConsoleWebBaseUrl.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/scopes")]
    [InlineData("http://aevatar-console.aevatar.ai")]
    [InlineData("https://aevatar-console.aevatar.ai?next=/scopes")]
    public void AddStudioHostingCore_WhenConsoleWebBaseUrlIsInvalid_ShouldFailFastInsteadOfDroppingTheConsoleLink(
        string value)
    {
        var action = () => ResolveDeliveryOptions(DeliveryConfiguration("ConsoleWebBaseUrl", value));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConsoleWebBaseUrl*");
    }

    private static IConfiguration DeliveryConfiguration(string key, string value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:{key}"] = value,
            })
            .Build();

    private static WorkflowDeliveryOptions ResolveDeliveryOptions(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddStudioHostingCore(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<WorkflowDeliveryOptions>>().Value;
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
