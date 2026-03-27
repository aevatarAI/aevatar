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
    public async Task DefinitionCommands_ShouldUseDefinitionTargetProjectionAndDispatch()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var catalogProjectionPort = new RecordingCatalogProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            catalogProjectionPort,
            new RecordingRevisionProjectionPort());

        var createReceipt = await service.CreateServiceAsync(new CreateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var updateReceipt = await service.UpdateServiceAsync(new UpdateServiceDefinitionCommand
        {
            Spec = GAgentServiceTestKit.CreateDefinitionSpec(identity),
        });
        var defaultReceipt = await service.SetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "rev-1",
        });

        provisioner.DefinitionRequests.Should().HaveCount(3);
        catalogProjectionPort.ActorIds.Should().Equal(
            ServiceActorIds.Definition(identity),
            ServiceActorIds.Definition(identity),
            ServiceActorIds.Definition(identity));
        dispatchPort.Calls.Should().HaveCount(3);
        createReceipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
        updateReceipt.TargetActorId.Should().Be(ServiceActorIds.Definition(identity));
        defaultReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:rev-1");
    }

    [Fact]
    public async Task RevisionCommands_ShouldUseRevisionCatalogProjectionAndDispatch()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var revisionProjectionPort = new RecordingRevisionProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogProjectionPort(),
            revisionProjectionPort);

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

        provisioner.RevisionCatalogRequests.Should().HaveCount(4);
        revisionProjectionPort.ActorIds.Should().Equal(
            ServiceActorIds.RevisionCatalog(identity),
            ServiceActorIds.RevisionCatalog(identity),
            ServiceActorIds.RevisionCatalog(identity),
            ServiceActorIds.RevisionCatalog(identity));
        dispatchPort.Calls.Should().HaveCount(4);
        createReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r1");
        prepareReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r2");
        publishReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r3");
        retireReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r4");
    }

    [Fact]
    public async Task ActivateServiceRevisionAsync_ShouldUseDeploymentTargetAndProjection()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var deploymentProjectionPort = new RecordingProjectionPort();
        var servingProjectionPort = new RecordingProjectionPort();
        var trafficProjectionPort = new RecordingProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort(),
            deploymentProjectionPort: deploymentProjectionPort,
            servingProjectionPort: servingProjectionPort,
            trafficProjectionPort: trafficProjectionPort);

        var receipt = await service.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "rev-2",
        });

        receipt.TargetActorId.Should().Be(ServiceActorIds.Deployment(identity));
        provisioner.DeploymentRequests.Should().ContainSingle();
        provisioner.ServingSetRequests.Should().ContainSingle();
        deploymentProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Deployment(identity));
        servingProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.ServingSet(identity));
        trafficProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.ServingSet(identity));
        dispatchPort.Calls.Should().ContainSingle(x => x.actorId == ServiceActorIds.Deployment(identity));
    }

    [Fact]
    public async Task DeploymentAndRolloutLifecycleCommands_ShouldUseExpectedTargetsAndProjections()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var deploymentProjectionPort = new RecordingProjectionPort();
        var servingProjectionPort = new RecordingProjectionPort();
        var rolloutProjectionPort = new RecordingProjectionPort();
        var trafficProjectionPort = new RecordingProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort(),
            deploymentProjectionPort: deploymentProjectionPort,
            servingProjectionPort: servingProjectionPort,
            rolloutProjectionPort: rolloutProjectionPort,
            trafficProjectionPort: trafficProjectionPort);

        var deactivateReceipt = await service.DeactivateServiceDeploymentAsync(new DeactivateServiceDeploymentCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-1",
        });
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
        var resumeReceipt = await service.ResumeServiceRolloutAsync(new ResumeServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-1",
        });
        var rollbackReceipt = await service.RollbackServiceRolloutAsync(new RollbackServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-1",
            Reason = "rollback",
        });

        deactivateReceipt.TargetActorId.Should().Be(ServiceActorIds.Deployment(identity));
        advanceReceipt.TargetActorId.Should().Be(ServiceActorIds.Rollout(identity));
        pauseReceipt.TargetActorId.Should().Be(ServiceActorIds.Rollout(identity));
        resumeReceipt.TargetActorId.Should().Be(ServiceActorIds.Rollout(identity));
        rollbackReceipt.TargetActorId.Should().Be(ServiceActorIds.Rollout(identity));
        deactivateReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:dep-1");
        advanceReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:rollout-1");
        pauseReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:rollout-1");
        resumeReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:rollout-1");
        rollbackReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:rollout-1");
        provisioner.DeploymentRequests.Should().ContainSingle();
        provisioner.RolloutRequests.Should().HaveCount(4);
        provisioner.ServingSetRequests.Should().HaveCount(3);
        deploymentProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Deployment(identity));
        rolloutProjectionPort.ActorIds.Should().Equal(
            ServiceActorIds.Rollout(identity),
            ServiceActorIds.Rollout(identity),
            ServiceActorIds.Rollout(identity),
            ServiceActorIds.Rollout(identity));
        servingProjectionPort.ActorIds.Should().Equal(
            ServiceActorIds.ServingSet(identity),
            ServiceActorIds.ServingSet(identity),
            ServiceActorIds.ServingSet(identity));
        trafficProjectionPort.ActorIds.Should().Equal(
            ServiceActorIds.ServingSet(identity),
            ServiceActorIds.ServingSet(identity),
            ServiceActorIds.ServingSet(identity));
        dispatchPort.Calls.Should().HaveCount(5);
    }

    [Fact]
    public async Task ReplaceServiceServingTargetsAsync_ShouldDispatchRawTargetsToServingSet()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var servingProjectionPort = new RecordingProjectionPort();
        var trafficProjectionPort = new RecordingProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort(),
            servingProjectionPort: servingProjectionPort,
            trafficProjectionPort: trafficProjectionPort);

        var receipt = await service.ReplaceServiceServingTargetsAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec
                {
                    RevisionId = "rev-1",
                },
            },
        });

        receipt.TargetActorId.Should().Be(ServiceActorIds.ServingSet(identity));
        provisioner.ServingSetRequests.Should().ContainSingle();
        servingProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.ServingSet(identity));
        trafficProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.ServingSet(identity));
        var dispatched = dispatchPort.Calls.Should().ContainSingle().Subject.envelope.Payload.Unpack<ReplaceServiceServingTargetsCommand>();
        dispatched.Targets.Should().ContainSingle();
        dispatched.Targets[0].RevisionId.Should().Be("rev-1");
        dispatched.Targets[0].DeploymentId.Should().BeEmpty();
        dispatched.Targets[0].PrimaryActorId.Should().BeEmpty();
        dispatched.Targets[0].EnabledEndpointIds.Should().BeEmpty();
    }

    [Fact]
    public async Task StartServiceRolloutAsync_ShouldDispatchRawPlanAndEmptyBaseline()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var servingProjectionPort = new RecordingProjectionPort();
        var rolloutProjectionPort = new RecordingProjectionPort();
        var trafficProjectionPort = new RecordingProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort(),
            servingProjectionPort: servingProjectionPort,
            rolloutProjectionPort: rolloutProjectionPort,
            trafficProjectionPort: trafficProjectionPort);

        var receipt = await service.StartServiceRolloutAsync(new StartServiceRolloutCommand
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
                        Targets =
                        {
                            new ServiceServingTargetSpec
                            {
                                RevisionId = "rev-2",
                            },
                        },
                    },
                },
            },
        });

        receipt.TargetActorId.Should().Be(ServiceActorIds.Rollout(identity));
        provisioner.ServingSetRequests.Should().ContainSingle();
        provisioner.RolloutRequests.Should().ContainSingle();
        servingProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.ServingSet(identity));
        trafficProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.ServingSet(identity));
        rolloutProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Rollout(identity));

        var dispatched = dispatchPort.Calls.Should().ContainSingle().Subject.envelope.Payload.Unpack<StartServiceRolloutCommand>();
        dispatched.BaselineTargets.Should().BeEmpty();
        dispatched.Plan.Stages.Should().ContainSingle();
        dispatched.Plan.Stages[0].Targets.Should().ContainSingle();
        dispatched.Plan.Stages[0].Targets[0].RevisionId.Should().Be("rev-2");
        dispatched.Plan.Stages[0].Targets[0].DeploymentId.Should().BeEmpty();
        dispatched.Plan.Stages[0].Targets[0].PrimaryActorId.Should().BeEmpty();
    }

    [Fact]
    public async Task StartServiceRolloutAsync_ShouldRejectMissingPlan()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = CreateService(
            new RecordingCommandTargetProvisioner(),
            new RecordingActorDispatchPort(),
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort());

        var act = () => service.StartServiceRolloutAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
        });

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static ServiceCommandApplicationService CreateService(
        RecordingCommandTargetProvisioner provisioner,
        RecordingActorDispatchPort dispatchPort,
        RecordingCatalogProjectionPort catalogProjectionPort,
        RecordingRevisionProjectionPort revisionProjectionPort,
        RecordingProjectionPort? deploymentProjectionPort = null,
        RecordingProjectionPort? servingProjectionPort = null,
        RecordingProjectionPort? rolloutProjectionPort = null,
        RecordingProjectionPort? trafficProjectionPort = null) =>
        new(
            dispatchPort,
            provisioner,
            catalogProjectionPort,
            revisionProjectionPort,
            deploymentProjectionPort ?? new RecordingProjectionPort(),
            servingProjectionPort ?? new RecordingProjectionPort(),
            rolloutProjectionPort ?? new RecordingProjectionPort(),
            trafficProjectionPort ?? new RecordingProjectionPort());

    private sealed class RecordingCommandTargetProvisioner : IServiceCommandTargetProvisioner
    {
        public List<ServiceIdentity> DefinitionRequests { get; } = [];

        public List<ServiceIdentity> RevisionCatalogRequests { get; } = [];

        public List<ServiceIdentity> DeploymentRequests { get; } = [];

        public List<ServiceIdentity> ServingSetRequests { get; } = [];

        public List<ServiceIdentity> RolloutRequests { get; } = [];

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
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCatalogProjectionPort : IServiceCatalogProjectionPort
    {
        public List<string> ActorIds { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRevisionProjectionPort : IServiceRevisionCatalogProjectionPort
    {
        public List<string> ActorIds { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProjectionPort :
        IServiceDeploymentCatalogProjectionPort,
        IServiceServingSetProjectionPort,
        IServiceRolloutProjectionPort,
        IServiceTrafficViewProjectionPort
    {
        public List<string> ActorIds { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            ActorIds.Add(actorId);
            return Task.CompletedTask;
        }
    }
}
