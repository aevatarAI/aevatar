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
        var queryPort = new RecordingServiceQueryPort
        {
            ReturnActiveTrafficAfterFirstTrafficQuery = true,
        };
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
    public async Task StartAsync_WhenTrafficViewAlreadyActive_ShouldNotRepairServingTargets()
    {
        var commandPort = new RecordingServiceCommandPort();
        var queryPort = new RecordingServiceQueryPort
        {
            ReturnActiveTrafficImmediately = true,
        };
        queryPort.SeedPublishedDefaultDemoServices();
        var hostedService = CreateHostedService(
            commandPort,
            queryPort,
            new GAgentServiceDemoOptions
            {
                Enabled = true,
            },
            Environments.Development);

        await hostedService.StartAsync(CancellationToken.None);

        commandPort.ActivateServiceRevisionCommands.Should().BeEmpty();
        commandPort.ReplaceServingTargetsCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_WhenDefaultServingHasNoActiveTrafficView_ShouldRepairThenFailClearly()
    {
        var commandPort = new RecordingServiceCommandPort();
        var queryPort = new RecordingServiceQueryPort();
        queryPort.SeedPublishedDefaultDemoServices();
        var hostedService = CreateHostedService(
            commandPort,
            queryPort,
            new GAgentServiceDemoOptions
            {
                Enabled = true,
                ReadinessObservationTimeout = TimeSpan.Zero,
                ReadinessObservationPollInterval = TimeSpan.Zero,
            },
            Environments.Development);

        var act = () => hostedService.StartAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Demo service 'demo:gagent-service:samples:demo-uppercase' has no active serving traffic view.");
        commandPort.ActivateServiceRevisionCommands.Should().ContainSingle();
        commandPort.ReplaceServingTargetsCommands.Should().ContainSingle();
    }

    [Fact]
    public async Task StartAsync_WhenDefaultServingTrafficViewConvergesAfterRepair_ShouldPassReadiness()
    {
        var commandPort = new RecordingServiceCommandPort();
        var queryPort = new RecordingServiceQueryPort
        {
            ReturnActiveTrafficAfterFirstTrafficQuery = true,
        };
        queryPort.SeedPublishedDefaultDemoServices();
        var hostedService = CreateHostedService(
            commandPort,
            queryPort,
            new GAgentServiceDemoOptions
            {
                Enabled = true,
            },
            Environments.Development);

        await hostedService.StartAsync(CancellationToken.None);

        commandPort.ActivateServiceRevisionCommands.Should().HaveCount(3);
        commandPort.ReplaceServingTargetsCommands.Should().HaveCount(3);
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

        public List<RetireServiceRevisionCommand> RetireRevisionCommands { get; } = [];

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

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default)
        {
            RetireRevisionCommands.Add(command.Clone());
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
        private static readonly string[] DemoServiceIds =
        [
            "demo-uppercase",
            "demo-count-lines",
            "demo-take-first-three",
        ];

        private readonly Dictionary<string, int> _trafficViewQueryCounts = [];
        private readonly Dictionary<string, ServiceCatalogSnapshot> _services = [];
        private readonly Dictionary<string, ServiceRevisionCatalogSnapshot> _revisions = [];
        private readonly Dictionary<string, ServiceDeploymentCatalogSnapshot> _deployments = [];
        private readonly Dictionary<string, ServiceServingSetSnapshot> _servingSets = [];

        public bool ReturnActiveTrafficImmediately { get; init; }

        public bool ReturnActiveTrafficAfterFirstTrafficQuery { get; init; }

        public void SeedPublishedDefaultDemoServices()
        {
            foreach (var serviceId in DemoServiceIds)
            {
                var identity = CreateDemoIdentity(serviceId);
                var serviceKey = ServiceKeys.Build(identity);
                _services[serviceKey] = new ServiceCatalogSnapshot(
                    serviceKey,
                    identity.TenantId,
                    identity.AppId,
                    identity.Namespace,
                    identity.ServiceId,
                    serviceId,
                    "builtin-v1",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    [],
                    [],
                    DateTimeOffset.UnixEpoch);
                _revisions[serviceKey] = new ServiceRevisionCatalogSnapshot(
                    serviceKey,
                    [new ServiceRevisionSnapshot(
                        "builtin-v1",
                        ServiceImplementationKind.Workflow.ToString(),
                        ServiceRevisionStatus.Published.ToString(),
                        string.Empty,
                        string.Empty,
                        [],
                        null,
                        null,
                        DateTimeOffset.UnixEpoch,
                        null)],
                    DateTimeOffset.UnixEpoch);
            }
        }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_services.GetValueOrDefault(ServiceKeys.Build(identity)));

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_revisions.GetValueOrDefault(ServiceKeys.Build(identity)));

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_deployments.GetValueOrDefault(ServiceKeys.Build(identity)));

        public Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(_servingSets.GetValueOrDefault(ServiceKeys.Build(identity)));

        public Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRolloutSnapshot?>(null);

        public Task<ServiceRolloutCommandObservationSnapshot?> GetServiceRolloutCommandObservationAsync(
            ServiceIdentity identity,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRolloutCommandObservationSnapshot?>(null);

        public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            var serviceKey = ServiceKeys.Build(identity);
            _trafficViewQueryCounts[serviceKey] = _trafficViewQueryCounts.GetValueOrDefault(serviceKey) + 1;
            if (ReturnActiveTrafficImmediately ||
                ReturnActiveTrafficAfterFirstTrafficQuery && _trafficViewQueryCounts[serviceKey] > 1)
            {
                return Task.FromResult<ServiceTrafficViewSnapshot?>(CreateActiveTrafficView(identity));
            }

            return Task.FromResult<ServiceTrafficViewSnapshot?>(null);
        }

        private static ServiceIdentity CreateDemoIdentity(string serviceId) =>
            new()
            {
                TenantId = "demo",
                AppId = "gagent-service",
                Namespace = "samples",
                ServiceId = serviceId,
            };

        private static ServiceTrafficViewSnapshot CreateActiveTrafficView(ServiceIdentity identity)
        {
            var deploymentId = $"{ServiceActorIds.Deployment(identity)}:builtin-v1";
            return new ServiceTrafficViewSnapshot(
                ServiceKeys.Build(identity),
                1,
                string.Empty,
                [new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [new ServiceTrafficTargetSnapshot(
                        deploymentId,
                        "builtin-v1",
                        $"gagent-service:workflow-definition:{deploymentId}",
                        100,
                        ServiceServingState.Active.ToString())])],
                DateTimeOffset.UnixEpoch);
        }
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
