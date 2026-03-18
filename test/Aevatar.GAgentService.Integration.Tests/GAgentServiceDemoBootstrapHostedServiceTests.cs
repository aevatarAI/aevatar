using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Hosting.Demo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class GAgentServiceDemoBootstrapHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WhenEnabled_ShouldBootstrapAllDemoWorkflowServices()
    {
        var commandPort = new RecordingServiceCommandPort();
        var queryPort = new RecordingServiceQueryPort();
        var hostedService = CreateHostedService(
            commandPort,
            queryPort,
            new GAgentServiceDemoOptions
            {
                Enabled = true,
            },
            Environments.Development);

        await hostedService.StartAsync(CancellationToken.None);

        commandPort.CreateServiceCommands.Select(x => x.Spec.Identity.ServiceId)
            .Should()
            .Equal("demo-uppercase", "demo-count-lines", "demo-take-first-three");
        commandPort.CreateRevisionCommands.Should().HaveCount(3);
        commandPort.CreateRevisionCommands.Should().OnlyContain(x =>
            x.Spec.ImplementationKind == ServiceImplementationKind.Workflow &&
            !string.IsNullOrWhiteSpace(x.Spec.WorkflowSpec.WorkflowYaml));
        commandPort.PrepareRevisionCommands.Select(x => x.RevisionId)
            .Should()
            .OnlyContain(x => x == "builtin-v1");
        commandPort.PublishRevisionCommands.Select(x => x.RevisionId)
            .Should()
            .OnlyContain(x => x == "builtin-v1");
        commandPort.SetDefaultServingRevisionCommands.Select(x => x.RevisionId)
            .Should()
            .OnlyContain(x => x == "builtin-v1");
        commandPort.ActivateServiceRevisionCommands.Select(x => x.RevisionId)
            .Should()
            .OnlyContain(x => x == "builtin-v1");
        commandPort.ReplaceServingTargetsCommands.Should().HaveCount(3);
        commandPort.ReplaceServingTargetsCommands.Should().OnlyContain(x =>
            x.Targets.Count == 1 &&
            x.Targets[0].AllocationWeight == 100 &&
            x.Targets[0].ServingState == ServiceServingState.Active &&
            x.Targets[0].EnabledEndpointIds.Count == 1 &&
            x.Targets[0].EnabledEndpointIds[0] == "chat");
    }

    [Fact]
    public async Task StartAsync_WhenExplicitlyDisabled_ShouldSkipBootstrapEvenInDevelopment()
    {
        var commandPort = new RecordingServiceCommandPort();
        var queryPort = new RecordingServiceQueryPort();
        var hostedService = CreateHostedService(
            commandPort,
            queryPort,
            new GAgentServiceDemoOptions
            {
                Enabled = false,
            },
            Environments.Development);

        await hostedService.StartAsync(CancellationToken.None);

        commandPort.CreateServiceCommands.Should().BeEmpty();
        commandPort.CreateRevisionCommands.Should().BeEmpty();
        commandPort.PrepareRevisionCommands.Should().BeEmpty();
        commandPort.PublishRevisionCommands.Should().BeEmpty();
        commandPort.SetDefaultServingRevisionCommands.Should().BeEmpty();
        commandPort.ActivateServiceRevisionCommands.Should().BeEmpty();
        commandPort.ReplaceServingTargetsCommands.Should().BeEmpty();
    }

    private static IHostedService CreateHostedService(
        RecordingServiceCommandPort commandPort,
        RecordingServiceQueryPort queryPort,
        GAgentServiceDemoOptions options,
        string environmentName)
    {
        var bootstrapType = typeof(Aevatar.GAgentService.Hosting.DependencyInjection.ServiceCollectionExtensions)
            .Assembly
            .GetType("Aevatar.GAgentService.Hosting.Demo.GAgentServiceDemoBootstrapHostedService", throwOnError: true)!;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IServiceCommandPort>(commandPort);
        services.AddSingleton<IServiceLifecycleQueryPort>(queryPort);
        services.AddSingleton<IServiceServingQueryPort>(queryPort);
        services.AddSingleton<IOptions<GAgentServiceDemoOptions>>(Options.Create(options));
        services.AddSingleton<IHostEnvironment>(new RecordingHostEnvironment
        {
            EnvironmentName = environmentName,
        });
        services.AddSingleton(typeof(IHostedService), sp =>
            (IHostedService)ActivatorUtilities.CreateInstance(sp, bootstrapType));

        return services.BuildServiceProvider().GetRequiredService<IHostedService>();
    }

    private sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        public List<CreateServiceDefinitionCommand> CreateServiceCommands { get; } = [];

        public List<UpdateServiceDefinitionCommand> UpdateServiceCommands { get; } = [];

        public List<CreateServiceRevisionCommand> CreateRevisionCommands { get; } = [];

        public List<PrepareServiceRevisionCommand> PrepareRevisionCommands { get; } = [];

        public List<PublishServiceRevisionCommand> PublishRevisionCommands { get; } = [];

        public List<SetDefaultServingRevisionCommand> SetDefaultServingRevisionCommands { get; } = [];

        public List<ActivateServiceRevisionCommand> ActivateServiceRevisionCommands { get; } = [];

        public List<ReplaceServiceServingTargetsCommand> ReplaceServingTargetsCommands { get; } = [];

        public List<DeactivateServiceDeploymentCommand> DeactivateServiceDeploymentCommands { get; } = [];

        public List<StartServiceRolloutCommand> StartServiceRolloutCommands { get; } = [];

        public List<AdvanceServiceRolloutCommand> AdvanceServiceRolloutCommands { get; } = [];

        public List<PauseServiceRolloutCommand> PauseServiceRolloutCommands { get; } = [];

        public List<ResumeServiceRolloutCommand> ResumeServiceRolloutCommands { get; } = [];

        public List<RollbackServiceRolloutCommand> RollbackServiceRolloutCommands { get; } = [];

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            CreateServiceCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Spec.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            UpdateServiceCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Spec.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default)
        {
            CreateRevisionCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Spec.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default)
        {
            PrepareRevisionCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default)
        {
            PublishRevisionCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default)
        {
            SetDefaultServingRevisionCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            ActivateServiceRevisionCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default)
        {
            DeactivateServiceDeploymentCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default)
        {
            ReplaceServingTargetsCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default)
        {
            StartServiceRolloutCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default)
        {
            AdvanceServiceRolloutCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default)
        {
            PauseServiceRolloutCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default)
        {
            ResumeServiceRolloutCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default)
        {
            RollbackServiceRolloutCommands.Add(command.Clone());
            return Task.FromResult(CreateReceipt(command.Identity));
        }

        private static ServiceCommandAcceptedReceipt CreateReceipt(ServiceIdentity identity) =>
            new(ServiceKeys.Build(identity), Guid.NewGuid().ToString("N"), ServiceKeys.Build(identity));
    }

    private sealed class RecordingServiceQueryPort : IServiceLifecycleQueryPort, IServiceServingQueryPort
    {
        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceCatalogSnapshot?>(null);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(null);

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);

        public Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceServingSetSnapshot?>(null);

        public Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRolloutSnapshot?>(null);

        public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceTrafficViewSnapshot?>(null);
    }

    private sealed class RecordingHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Aevatar.GAgentService.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
