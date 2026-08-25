using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Assemblers;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Hosting.Demo;
using Aevatar.Workflow.Abstractions;
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
        commandPort.ActivateServiceRevisionCommands.Select(x => x.RevisionId)
            .Should()
            .OnlyContain(x => x == "builtin-v1");
        commandPort.ActivateServiceRevisionCommands.Should().OnlyContain(x =>
            !string.IsNullOrWhiteSpace(x.ExpectedArtifactHash));
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
        services.AddSingleton<IServiceImplementationAdapter, DemoWorkflowImplementationAdapter>();
        services.AddSingleton<PreparedServiceRevisionArtifactAssembler>();
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

    private sealed class DemoWorkflowImplementationAdapter : IServiceImplementationAdapter
    {
        public ServiceImplementationKind ImplementationKind => ServiceImplementationKind.Workflow;

        public Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
            PrepareServiceRevisionRequest request,
            CancellationToken ct = default)
        {
            var spec = request.Spec?.WorkflowSpec
                ?? throw new InvalidOperationException("workflow spec is required");
            var executionMode = spec.ExpectedExecutionMode == ExternalCapabilityExecutionMode.Unspecified
                ? ExternalCapabilityExecutionMode.Interactive
                : spec.ExpectedExecutionMode;
            var plan = spec.CapabilityAdmissionPlan?.Clone() ?? new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = executionMode,
            };
            var artifact = new PreparedServiceRevisionArtifact
            {
                Identity = request.Spec.Identity?.Clone(),
                RevisionId = request.Spec.RevisionId,
                ImplementationKind = ServiceImplementationKind.Workflow,
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        ToolCatalogPolicyVersion = spec.ToolCatalogPolicyVersion,
                        WorkflowName = spec.WorkflowName,
                        WorkflowYaml = spec.WorkflowYaml,
                        WorkflowId = string.IsNullOrWhiteSpace(spec.WorkflowId)
                            ? request.Spec.RevisionId
                            : spec.WorkflowId,
                        RevisionId = request.Spec.RevisionId,
                        ExecutionMode = executionMode,
                        CapabilityAdmissionPlan = plan,
                    },
                },
            };
            artifact.Endpoints.Add(new ServiceEndpointDescriptor
            {
                EndpointId = "chat",
                Kind = ServiceEndpointKind.Chat,
            });
            return Task.FromResult(artifact);
        }
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

        public Task<ServiceRolloutCommandObservationSnapshot?> GetServiceRolloutCommandObservationAsync(
            ServiceIdentity identity,
            string commandId,
            CancellationToken ct = default) =>
            Task.FromResult<ServiceRolloutCommandObservationSnapshot?>(null);

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
