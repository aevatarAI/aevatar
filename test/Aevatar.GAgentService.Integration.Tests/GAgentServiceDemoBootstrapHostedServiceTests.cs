using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Hosting.Demo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
            TrafficViewFactory = CreateReadyTrafficView,
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
        commandPort.ReplaceServingTargetsCommands.Should().BeEmpty();
        queryPort.TrafficViewQueryCount.Should().Be(3);
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

    [Fact]
    public async Task StartAsync_WhenTrafficViewAppearsAfterActivation_ShouldWaitForReadiness()
    {
        var commandPort = new RecordingServiceCommandPort();
        var queryPort = new RecordingServiceQueryPort();
        queryPort.TrafficViewFactory = identity =>
            queryPort.TrafficViewQueryCount < 2 ? null : CreateReadyTrafficView(identity);
        var hostedService = CreateHostedService(
            commandPort,
            queryPort,
            new GAgentServiceDemoOptions
            {
                Enabled = true,
            },
            Environments.Development);

        await hostedService.StartAsync(CancellationToken.None);

        queryPort.DelayCount.Should().Be(1);
        queryPort.TrafficViewQueryCount.Should().Be(4);
    }

    [Fact]
    public async Task StartAsync_WhenTrafficViewNeverAppears_ShouldReportSetupFailure()
    {
        var commandPort = new RecordingServiceCommandPort();
        var queryPort = new RecordingServiceQueryPort();
        var hostedService = CreateHostedService(
            commandPort,
            queryPort,
            new GAgentServiceDemoOptions
            {
                Enabled = true,
                ServingReadinessTimeoutSeconds = 1,
                ServingReadinessPollIntervalMilliseconds = 1000,
            },
            Environments.Development);

        var now = DateTimeOffset.Parse("2026-06-29T12:00:00Z");
        queryPort.UtcNow = () => now;
        queryPort.Delay = delay =>
        {
            now += delay;
            return Task.CompletedTask;
        };

        var act = async () => await hostedService.StartAsync(CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Demo service 'demo:gagent-service:samples:demo-uppercase' revision 'builtin-v1' did not expose an active serving traffic view for endpoint 'chat' within 1s. Last traffic view: <missing>");
        queryPort.DelayCount.Should().Be(1);
        queryPort.TrafficViewQueryCount.Should().BeGreaterThan(1);
    }

    private static IHostedService CreateHostedService(
        RecordingServiceCommandPort commandPort,
        RecordingServiceQueryPort queryPort,
        GAgentServiceDemoOptions options,
        string environmentName)
    {
        return new GAgentServiceDemoBootstrapHostedService(
            commandPort,
            queryPort,
            queryPort,
            Options.Create(options),
            new RecordingHostEnvironment
            {
                EnvironmentName = environmentName,
            },
            NullLogger<GAgentServiceDemoBootstrapHostedService>.Instance,
            (delay, _) => queryPort.DelayAsync(delay),
            () => queryPort.UtcNow());
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
        public Func<ServiceIdentity, ServiceTrafficViewSnapshot?>? TrafficViewFactory { get; set; }

        public int TrafficViewQueryCount { get; private set; }

        public int DelayCount { get; private set; }

        public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

        public Func<TimeSpan, Task> Delay { get; set; } = _ => Task.CompletedTask;

        public async Task DelayAsync(TimeSpan delay)
        {
            DelayCount++;
            await Delay(delay);
        }

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

        public Task<ServiceRolloutCommandObservationSnapshot?> GetServiceRolloutCommandObservationAsync(
            ServiceIdentity identity,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRolloutCommandObservationSnapshot?>(null);

        public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            TrafficViewQueryCount++;
            return Task.FromResult(TrafficViewFactory?.Invoke(identity));
        }
    }

    private static ServiceTrafficViewSnapshot CreateReadyTrafficView(ServiceIdentity identity)
    {
        var deploymentId = $"{ServiceActorIds.Deployment(identity)}:builtin-v1";
        return new ServiceTrafficViewSnapshot(
            ServiceKeys.Build(identity),
            1,
            string.Empty,
            [
                new ServiceTrafficEndpointSnapshot(
                    "chat",
                    [
                        new ServiceTrafficTargetSnapshot(
                            deploymentId,
                            "builtin-v1",
                            $"gagent-service:workflow-definition:{deploymentId}",
                            100,
                            ServiceServingState.Active.ToString()),
                    ]),
            ],
            DateTimeOffset.UtcNow);
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
