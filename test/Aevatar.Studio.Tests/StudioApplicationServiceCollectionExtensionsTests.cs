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
using Aevatar.Studio.Hosting.WorkflowDeliveries;
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
    public void AddStudioHostingCore_ShouldRegisterDeliveryPackageCatalogStartupProbe()
    {
        var services = new ServiceCollection();

        services.AddStudioHostingCore(new ConfigurationBuilder().Build());

        services.Should().ContainSingle(x =>
            x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(WorkflowDeliveryPackageCatalogStartupProbe));
    }

    [Fact]
    public void AddStudioHostingCore_WhenDeliverySectionIsMissing_ShouldExposeEmptyPackageCatalog()
    {
        var options = ResolveDeliveryOptions(new ConfigurationBuilder().Build());

        options.Packages.Should().BeEmpty();
    }

    [Fact]
    public void AddStudioHostingCore_WhenDeliveryPackageIsConfigured_ShouldBindTypedDefinition()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:Packages:0:WorkflowName"] = "workflow-alpha",
                [$"{WorkflowDeliveryOptions.SectionName}:Packages:0:DisplayName"] = "Workflow Alpha",
                [$"{WorkflowDeliveryOptions.SectionName}:Packages:0:Acceptance:Mode"] = "AutomaticPreview",
                [$"{WorkflowDeliveryOptions.SectionName}:Packages:0:Acceptance:Input:0:Key"] = "dry_run",
                [$"{WorkflowDeliveryOptions.SectionName}:Packages:0:Acceptance:Input:0:Kind"] = "Boolean",
                [$"{WorkflowDeliveryOptions.SectionName}:Packages:0:Acceptance:Input:0:Value"] = "true",
            })
            .Build();

        var options = ResolveDeliveryOptions(configuration);

        var package = options.Packages.Should().ContainSingle().Which;
        package.WorkflowName.Should().Be("workflow-alpha");
        package.DisplayName.Should().Be("Workflow Alpha");
        package.Acceptance.Mode.Should().Be(WorkflowDeliveryAcceptanceMode.AutomaticPreview);
        package.Acceptance.Input.Should().ContainSingle().Which.Kind
            .Should().Be(WorkflowDeliveryAcceptanceInputValueKind.Boolean);
    }

    [Fact]
    public void AddStudioHostingCore_WhenDeliveryConfigurationContainsUnknownKey_ShouldFailHostStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:Packagess:0:WorkflowName"] = "workflow-alpha",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddStudioHostingCore(configuration);
        using var provider = services.BuildServiceProvider();

        var action = () => provider.GetRequiredService<IStartupValidator>().Validate();

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Packagess*");
    }

    [Fact]
    public void AddStudioHostingCore_WhenLegacyDeliveryConfigurationIsPresent_ShouldIgnoreRetiredSemantics()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:AllowedWorkflowNames:0"] = "workflow-alpha",
                [$"{WorkflowDeliveryOptions.SectionName}:UseShippedWorkflowAllowlist"] = "true",
                [$"{WorkflowDeliveryOptions.SectionName}:ConsoleBaseUrl"] = "https://api.example.com",
                [$"{WorkflowDeliveryOptions.SectionName}:ConsoleWebBaseUrl"] = "https://console.example.com",
            })
            .Build();

        var options = ResolveDeliveryOptions(configuration);

        options.Packages.Should().BeEmpty();
        options.ConsoleWebBaseUrl.Should().Be("https://console.example.com");
    }

    [Fact]
    public void AddStudioHostingCore_WhenLegacyAndTypedPackagesCoexist_ShouldUseOnlyTypedPackages()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:AllowedWorkflowNames:0"] = "legacy-workflow",
                [$"{WorkflowDeliveryOptions.SectionName}:UseShippedWorkflowAllowlist"] = "true",
                [$"{WorkflowDeliveryOptions.SectionName}:Packages:0:WorkflowName"] = "workflow-alpha",
            })
            .Build();

        var options = ResolveDeliveryOptions(configuration);

        options.Packages.Should().ContainSingle().Which.WorkflowName.Should().Be("workflow-alpha");
    }

    [Fact]
    public void AddStudioHostingCore_WhenLegacyAndUnknownNestedConfigurationCoexist_ShouldStillFailHostStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:UseShippedWorkflowAllowlist"] = "true",
                [$"{WorkflowDeliveryOptions.SectionName}:Packages:0:WorkflowNme"] = "workflow-alpha",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddStudioHostingCore(configuration);
        using var provider = services.BuildServiceProvider();

        var action = () => provider.GetRequiredService<IStartupValidator>().Validate();

        var exception = action.Should().Throw<InvalidOperationException>().Which;
        exception.ToString().Should().Contain("WorkflowNme");
    }

    [Fact]
    public void AddStudioHostingCore_WhenConsoleBaseUrlIsPresent_ShouldNotTranslateItToConsoleWebBaseUrl()
    {
        var options = ResolveDeliveryOptions(DeliveryConfiguration(
            "ConsoleBaseUrl",
            "https://api.example.com"));

        options.ConsoleWebBaseUrl.Should().BeEmpty();
    }

    [Fact]
    public void AddStudioHostingCore_WhenLegacyConfigurationAndInvalidConsoleWebBaseUrlCoexist_ShouldFailHostStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:UseShippedWorkflowAllowlist"] = "true",
                [$"{WorkflowDeliveryOptions.SectionName}:ConsoleWebBaseUrl"] = "http://console.example.com",
            })
            .Build();

        var action = () => ResolveDeliveryOptions(configuration);

        action.Should().Throw<OptionsValidationException>()
            .WithMessage("*ConsoleWebBaseUrl*");
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

        action.Should().Throw<OptionsValidationException>()
            .WithMessage("*ConsoleWebBaseUrl*");
    }

    // Console routing does not imply package publication. Package definitions remain an
    // explicit deployment-owned catalog.
    [Fact]
    public void AddStudioHostingCore_WhenDeliverySectionCarriesOnlyConsoleWebUrl_ShouldExposeNoPackages()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:ConsoleWebBaseUrl"] = "https://console.example.com",
            })
            .Build();

        var options = ResolveDeliveryOptions(configuration);

        options.Packages.Should().BeEmpty();
    }

    [Fact]
    public void AddStudioHostingCore_WhenConsoleWebUrlIsCombinedWithConfiguredPackage_ShouldKeepBoth()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowDeliveryOptions.SectionName}:Packages:0:WorkflowName"] = "workflow-alpha",
                [$"{WorkflowDeliveryOptions.SectionName}:ConsoleWebBaseUrl"] = "https://console.example.com",
            })
            .Build();

        var options = ResolveDeliveryOptions(configuration);

        options.Packages.Should().ContainSingle().Which.WorkflowName.Should().Be("workflow-alpha");
        options.ConsoleWebBaseUrl.Should().Be("https://console.example.com");
    }

    [Fact]
    public void MainnetDistributedDeliveryConfiguration_ShouldKeepEmptyCatalogWithConsoleWebUrl()
    {
        using var stream = File.OpenRead(Path.Combine(
            Aevatar.Configuration.AevatarPaths.RepoRoot,
            "src",
            "Aevatar.Mainnet.Host.Api",
            "appsettings.Distributed.json"));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        var options = ResolveDeliveryOptions(configuration);

        options.Packages.Should().BeEmpty();
        options.ConsoleWebBaseUrl.Should().Be("https://aevatar-console.aevatar.ai");
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
