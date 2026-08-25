using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ServiceCommandApplicationServiceTests
{
    [Fact]
    public async Task DefinitionRevisionAndDeploymentCommands_ShouldReturnAcceptedReceiptsWithoutProjectionPorts()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(provisioner, dispatchPort);

        var createReceipt = await service.CreateServiceAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var revisionReceipt = await service.CreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });
        var deploymentReceipt = await service.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r1",
        });

        createReceipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
        revisionReceipt.TargetActorId.Should().Be(ServiceActorIds.RevisionCatalog(identity));
        deploymentReceipt.TargetActorId.Should().Be(ServiceActorIds.Deployment(identity));
        provisioner.DefinitionRequests.Should().ContainSingle();
        provisioner.RevisionCatalogRequests.Should().ContainSingle();
        provisioner.DeploymentRequests.Should().ContainSingle();
        provisioner.ServingSetRequests.Should().ContainSingle();
        provisioner.InvocationCatalogRequests.Should().HaveCount(3);
        provisioner.InvocationCatalogRequests.Should().OnlyContain(x =>
            ServiceKeys.Build(x) == ServiceKeys.Build(identity));
        dispatchPort.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task DefinitionLifecycleCommands_ShouldDispatchEachBranchWithoutProjectionPriming()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(provisioner, dispatchPort);

        var createReceipt = await service.CreateServiceAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var updateReceipt = await service.UpdateServiceAsync(new UpdateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var reconcileExposureReceipt = await service.ReconcileExternalExposureAsync(new ReconcileExternalExposureCommand
        {
            Identity = identity.Clone(),
            OpenapiUrl = "https://api.test/api/services/svc/openapi.json",
            DesiredSpecHash = "hash-1",
            CredentialKid = "kid-1",
        });
        var retireExposureReceipt = await service.RetireExternalExposureAsync(new RetireExternalExposureCommand
        {
            Identity = identity.Clone(),
        });
        createReceipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
        updateReceipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
        reconcileExposureReceipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
        retireExposureReceipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
        provisioner.DefinitionRequests.Should().HaveCount(4);
        provisioner.InvocationCatalogRequests.Should().HaveCount(4);
        provisioner.InvocationCatalogRequests.Should().OnlyContain(x =>
            ServiceKeys.Build(x) == ServiceKeys.Build(identity));
        dispatchPort.Calls.Select(x => x.envelope.Payload.TypeUrl).Should().Contain([
            AnyTypeUrl<CreateServiceDefinitionCommand>(),
            AnyTypeUrl<UpdateServiceDefinitionCommand>(),
            AnyTypeUrl<ReconcileExternalExposureCommand>(),
            AnyTypeUrl<RetireExternalExposureCommand>(),
        ]);
    }

    [Fact]
    public async Task RevisionLifecycleCommands_ShouldDispatchEachBranchWithoutProjectionPriming()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(provisioner, dispatchPort);

        var createReceipt = await service.CreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });
        var prepareReceipt = await service.PrepareRevisionAsync(new PrepareServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r2",
        });
        var publishReceipt = await service.PublishRevisionAsync(new PublishServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r3",
        });
        var retireReceipt = await service.RetireRevisionAsync(new RetireServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "r4",
        });

        createReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r1");
        prepareReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r2");
        publishReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r3");
        retireReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r4");
        provisioner.RevisionCatalogRequests.Should().HaveCount(4);
        provisioner.InvocationCatalogRequests.Should().HaveCount(4);
        provisioner.InvocationCatalogRequests.Should().OnlyContain(x =>
            ServiceKeys.Build(x) == ServiceKeys.Build(identity));
        dispatchPort.Calls.Select(x => x.actorId).Should().OnlyContain(x => x == ServiceActorIds.RevisionCatalog(identity));
        dispatchPort.Calls.Select(x => x.envelope.Payload.TypeUrl).Should().Contain([
            AnyTypeUrl<CreateServiceRevisionCommand>(),
            AnyTypeUrl<PrepareServiceRevisionCommand>(),
            AnyTypeUrl<PublishServiceRevisionCommand>(),
            AnyTypeUrl<RetireServiceRevisionCommand>(),
        ]);
    }

    [Fact]
    public async Task DeploymentLifecycleCommands_ShouldDispatchEachBranchWithoutProjectionPriming()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(provisioner, dispatchPort);

        var activateReceipt = await service.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "rev-2",
        });
        var deactivateReceipt = await service.DeactivateServiceDeploymentAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-1",
        });

        activateReceipt.TargetActorId.Should().Be(ServiceActorIds.Deployment(identity));
        deactivateReceipt.TargetActorId.Should().Be(ServiceActorIds.Deployment(identity));
        deactivateReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:dep-1");
        provisioner.DeploymentRequests.Should().HaveCount(2);
        provisioner.ServingSetRequests.Should().ContainSingle();
        provisioner.InvocationCatalogRequests.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(identity);
        dispatchPort.Calls.Select(x => x.envelope.Payload.TypeUrl).Should().Contain([
            AnyTypeUrl<ActivateServiceRevisionCommand>(),
            AnyTypeUrl<DeactivateServiceDeploymentCommand>(),
        ]);
    }

    [Fact]
    public async Task ServingAndRolloutCommands_ShouldDispatchCommandsWithoutProjectionPriming()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(provisioner, dispatchPort);

        var replaceReceipt = await service.ReplaceServiceServingTargetsAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec { RevisionId = "rev-1" },
            },
        });
        var rolloutReceipt = await service.StartServiceRolloutAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = "rollout-1",
                Stages =
                {
                    new ServiceRolloutStageSpec
                    {
                        StageId = "stage-1",
                        Targets = { new ServiceServingTargetSpec { RevisionId = "rev-2" } },
                    },
                },
            },
        });

        replaceReceipt.TargetActorId.Should().Be(ServiceActorIds.ServingSet(identity));
        rolloutReceipt.TargetActorId.Should().Be(ServiceActorIds.Rollout(identity));
        provisioner.ServingSetRequests.Should().HaveCount(2);
        provisioner.RolloutRequests.Should().ContainSingle();
        provisioner.InvocationCatalogRequests.Should().HaveCount(2);
        provisioner.InvocationCatalogRequests.Should().OnlyContain(x =>
            ServiceKeys.Build(x) == ServiceKeys.Build(identity));
        dispatchPort.Calls.Should().HaveCount(2);
        dispatchPort.Calls[0].envelope.Payload.Unpack<ReplaceServiceServingTargetsCommand>()
            .Targets.Should().ContainSingle();
        dispatchPort.Calls[1].envelope.Payload.Unpack<StartServiceRolloutCommand>()
            .BaselineTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task RolloutLifecycleCommands_ShouldDispatchEachBranchWithoutProjectionPriming()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(provisioner, dispatchPort);

        var advanceReceipt = await service.AdvanceServiceRolloutAsync(new AdvanceServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-1",
        });
        var pauseReceipt = await service.PauseServiceRolloutAsync(new PauseServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-1",
            Reason = "pause",
        });
        var rollbackReceipt = await service.RollbackServiceRolloutAsync(new RollbackServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-1",
            Reason = "rollback",
        });

        advanceReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:rollout-1");
        pauseReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:rollout-1");
        rollbackReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:rollout-1");
        provisioner.RolloutRequests.Should().HaveCount(3);
        provisioner.ServingSetRequests.Should().HaveCount(2);
        provisioner.InvocationCatalogRequests.Should().HaveCount(2);
        provisioner.InvocationCatalogRequests.Should().OnlyContain(x =>
            ServiceKeys.Build(x) == ServiceKeys.Build(identity));
        dispatchPort.Calls.Select(x => x.actorId).Should().OnlyContain(x => x == ServiceActorIds.Rollout(identity));
        dispatchPort.Calls.Select(x => x.envelope.Payload.TypeUrl).Should().Contain([
            AnyTypeUrl<AdvanceServiceRolloutCommand>(),
            AnyTypeUrl<PauseServiceRolloutCommand>(),
            AnyTypeUrl<RollbackServiceRolloutCommand>(),
        ]);
    }

    [Fact]
    public void CommandSources_ShouldNotContainProjectionActivationCalls()
    {
        var source = File.ReadAllText(SourcePath(
            "src/platform/Aevatar.GAgentService.Application/Services/ServiceCommandApplicationService.cs"));

        source.Should().NotContain("EnsureProjectionAsync");
        source.Should().NotContain("ActivateAsync(");
    }

    [Fact]
    public async Task StartServiceRolloutAsync_ShouldRejectMissingPlan()
    {
        var service = CreateService(
            new RecordingCommandTargetProvisioner(),
            new RecordingActorDispatchPort());

        var act = () => service.StartServiceRolloutAsync(new StartServiceRolloutCommand
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
        });

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ResumeServiceRolloutAsync_ShouldPropagateDispatchFailure()
    {
        var service = CreateService(
            new RecordingCommandTargetProvisioner(),
            new ThrowingActorDispatchPort(new InvalidOperationException("dispatch failed")));

        var act = () => service.ResumeServiceRolloutAsync(new ResumeServiceRolloutCommand
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            RolloutId = "stale",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dispatch failed");
    }

    private static ServiceCommandApplicationService CreateService(
        RecordingCommandTargetProvisioner provisioner,
        IActorDispatchPort dispatchPort) =>
        new(dispatchPort, provisioner);

    private static string AnyTypeUrl<T>() where T : Google.Protobuf.IMessage<T>, new() =>
        Google.Protobuf.WellKnownTypes.Any.Pack(new T()).TypeUrl;

    private static string SourcePath(string relativePath)
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
                return candidate;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException($"Could not locate source file '{relativePath}'.");
    }

    private sealed class RecordingCommandTargetProvisioner : IServiceCommandTargetProvisioner
    {
        public List<ServiceIdentity> DefinitionRequests { get; } = [];

        public List<ServiceIdentity> RevisionCatalogRequests { get; } = [];

        public List<ServiceIdentity> DeploymentRequests { get; } = [];

        public List<ServiceIdentity> ServingSetRequests { get; } = [];

        public List<ServiceIdentity> RolloutRequests { get; } = [];

        public List<ServiceIdentity> InvocationCatalogRequests { get; } = [];

        public Task<string> EnsureDefinitionTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            DefinitionRequests.Add(identity.Clone());
            return Task.FromResult(ServiceActorIds.Definition(identity));
        }

        public Task<string> EnsureRevisionCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            RevisionCatalogRequests.Add(identity.Clone());
            return Task.FromResult(ServiceActorIds.RevisionCatalog(identity));
        }

        public Task<string> EnsureDeploymentTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            DeploymentRequests.Add(identity.Clone());
            return Task.FromResult(ServiceActorIds.Deployment(identity));
        }

        public Task<string> EnsureServingSetTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            ServingSetRequests.Add(identity.Clone());
            return Task.FromResult(ServiceActorIds.ServingSet(identity));
        }

        public Task<string> EnsureRolloutTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            RolloutRequests.Add(identity.Clone());
            return Task.FromResult(ServiceActorIds.Rollout(identity));
        }

        public Task<string> EnsureInvocationCatalogTargetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            InvocationCatalogRequests.Add(identity.Clone());
            return Task.FromResult(ServiceActorIds.InvocationCatalog(identity));
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class ThrowingActorDispatchPort : IActorDispatchPort
    {
        private readonly Exception _exception;

        public ThrowingActorDispatchPort(Exception exception)
        {
            _exception = exception;
        }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.FromException<DispatchAdmission>(_exception);
    }
}
