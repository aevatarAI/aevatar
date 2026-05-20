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
        dispatchPort.Calls.Should().HaveCount(3);
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
        dispatchPort.Calls.Should().HaveCount(2);
        dispatchPort.Calls[0].envelope.Payload.Unpack<ReplaceServiceServingTargetsCommand>()
            .Targets.Should().ContainSingle();
        dispatchPort.Calls[1].envelope.Payload.Unpack<StartServiceRolloutCommand>()
            .BaselineTargets.Should().BeEmpty();
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

    private sealed class ThrowingActorDispatchPort : IActorDispatchPort
    {
        private readonly Exception _exception;

        public ThrowingActorDispatchPort(Exception exception)
        {
            _exception = exception;
        }

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.FromException(_exception);
    }
}
