using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Infrastructure.Artifacts;
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
            new RecordingCatalogQueryReader(),
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
    public async Task RevisionCommands_ShouldRequireDefinitionAndUseRevisionCatalogProjection()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var missingDefinitionService = CreateService(
            new RecordingCommandTargetProvisioner(),
            new RecordingActorDispatchPort(),
            new RecordingCatalogQueryReader(),
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort());

        var missingDefinition = () => missingDefinitionService.CreateRevisionAsync(new CreateServiceRevisionCommand
        {
            Spec = GAgentServiceTestKit.CreateStaticRevisionSpec(identity, "r1"),
        });

        await missingDefinition.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Service definition*was not found*");

        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var revisionProjectionPort = new RecordingRevisionProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
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

        provisioner.RevisionCatalogRequests.Should().HaveCount(3);
        revisionProjectionPort.ActorIds.Should().Equal(
            ServiceActorIds.RevisionCatalog(identity),
            ServiceActorIds.RevisionCatalog(identity),
            ServiceActorIds.RevisionCatalog(identity));
        dispatchPort.Calls.Should().HaveCount(3);
        createReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r1");
        prepareReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r2");
        publishReceipt.CorrelationId.Should().Be($"{ServiceKeys.Build(identity)}:r3");
    }

    [Fact]
    public async Task ActivateServiceRevisionAsync_ShouldUseDeploymentTarget_AndProjection()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var deploymentProjectionPort = new RecordingProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort(),
            deploymentProjectionPort: deploymentProjectionPort);

        var receipt = await service.ActivateServiceRevisionAsync(new ActivateServiceRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "rev-2",
        });

        receipt.TargetActorId.Should().Be(ServiceActorIds.Deployment(identity));
        provisioner.DeploymentRequests.Should().ContainSingle();
        deploymentProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.Deployment(identity));
        dispatchPort.Calls.Should().ContainSingle(x => x.actorId == ServiceActorIds.Deployment(identity));
    }

    [Fact]
    public async Task ReplaceServiceServingTargetsAsync_ShouldResolveDeploymentAndArtifactEndpoints()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "rev-1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var dispatchPort = new RecordingActorDispatchPort();
        var servingProjectionPort = new RecordingProjectionPort();
        var trafficProjectionPort = new RecordingProjectionPort();
        var service = CreateService(
            new RecordingCommandTargetProvisioner(),
            dispatchPort,
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort(),
            deploymentQueryReader: new RecordingDeploymentQueryReader
            {
                GetResult = new ServiceDeploymentCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        new ServiceDeploymentSnapshot("dep-1", "rev-1", "actor-1", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    ],
                    DateTimeOffset.UtcNow),
            },
            servingProjectionPort: servingProjectionPort,
            trafficProjectionPort: trafficProjectionPort,
            artifactStore: artifactStore);

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
        servingProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.ServingSet(identity));
        trafficProjectionPort.ActorIds.Should().ContainSingle(ServiceActorIds.ServingSet(identity));
        var envelope = dispatchPort.Calls.Should().ContainSingle().Subject.envelope;
        envelope.Payload.Is(ReplaceServiceServingTargetsCommand.Descriptor).Should().BeTrue();
        var dispatched = envelope.Payload.Unpack<ReplaceServiceServingTargetsCommand>();
        dispatched.Targets.Should().ContainSingle();
        dispatched.Targets[0].DeploymentId.Should().Be("dep-1");
        dispatched.Targets[0].PrimaryActorId.Should().Be("actor-1");
        dispatched.Targets[0].AllocationWeight.Should().Be(100);
        dispatched.Targets[0].EnabledEndpointIds.Should().ContainSingle("chat");
    }

    [Fact]
    public async Task StartServiceRolloutAsync_ShouldResolvePlanAndBaselineTargets()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "rev-2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-2",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "run")));
        var provisioner = new RecordingCommandTargetProvisioner();
        var dispatchPort = new RecordingActorDispatchPort();
        var servingProjectionPort = new RecordingProjectionPort();
        var trafficProjectionPort = new RecordingProjectionPort();
        var rolloutProjectionPort = new RecordingProjectionPort();
        var service = CreateService(
            provisioner,
            dispatchPort,
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort(),
            deploymentQueryReader: new RecordingDeploymentQueryReader
            {
                GetResult = new ServiceDeploymentCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        new ServiceDeploymentSnapshot("dep-2", "rev-2", "actor-2", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    ],
                    DateTimeOffset.UtcNow),
            },
            servingSetQueryReader: new RecordingServingSetQueryReader
            {
                GetResult = new ServiceServingSetSnapshot(
                    ServiceKeys.Build(identity),
                    1,
                    string.Empty,
                    [
                        new ServiceServingTargetSnapshot("dep-1", "rev-1", "actor-1", 100, ServiceServingState.Active.ToString(), ["run"]),
                    ],
                    DateTimeOffset.UtcNow),
            },
            servingProjectionPort: servingProjectionPort,
            rolloutProjectionPort: rolloutProjectionPort,
            trafficProjectionPort: trafficProjectionPort,
            artifactStore: artifactStore);

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
        dispatched.BaselineTargets.Should().ContainSingle(x => x.DeploymentId == "dep-1");
        dispatched.Plan.Stages.Should().ContainSingle();
        dispatched.Plan.Stages[0].Targets.Should().ContainSingle();
        dispatched.Plan.Stages[0].Targets[0].DeploymentId.Should().Be("dep-2");
        dispatched.Plan.Stages[0].Targets[0].EnabledEndpointIds.Should().ContainSingle("run");
    }

    private static ServiceCommandApplicationService CreateService(
        RecordingCommandTargetProvisioner provisioner,
        RecordingActorDispatchPort dispatchPort,
        RecordingCatalogQueryReader catalogQueryReader,
        RecordingCatalogProjectionPort catalogProjectionPort,
        RecordingRevisionProjectionPort revisionProjectionPort,
        RecordingDeploymentQueryReader? deploymentQueryReader = null,
        RecordingServingSetQueryReader? servingSetQueryReader = null,
        RecordingProjectionPort? deploymentProjectionPort = null,
        RecordingProjectionPort? servingProjectionPort = null,
        RecordingProjectionPort? rolloutProjectionPort = null,
        RecordingProjectionPort? trafficProjectionPort = null,
        InMemoryServiceRevisionArtifactStore? artifactStore = null) =>
        new(
            dispatchPort,
            provisioner,
            catalogQueryReader,
            catalogProjectionPort,
            revisionProjectionPort,
            deploymentQueryReader ?? new RecordingDeploymentQueryReader(),
            servingSetQueryReader ?? new RecordingServingSetQueryReader(),
            deploymentProjectionPort ?? new RecordingProjectionPort(),
            servingProjectionPort ?? new RecordingProjectionPort(),
            rolloutProjectionPort ?? new RecordingProjectionPort(),
            trafficProjectionPort ?? new RecordingProjectionPort(),
            artifactStore ?? new InMemoryServiceRevisionArtifactStore());

    private static ServiceCatalogSnapshot CreateCatalogSnapshot(ServiceIdentity identity) =>
        new(
            ServiceKeys.Build(identity),
            identity.TenantId,
            identity.AppId,
            identity.Namespace,
            identity.ServiceId,
            "Service",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            ServiceDeploymentStatus.Unspecified.ToString(),
            [],
            [],
            DateTimeOffset.UtcNow);

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

    private sealed class RecordingCatalogQueryReader : IServiceCatalogQueryReader
    {
        public ServiceCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }

    private sealed class RecordingDeploymentQueryReader : IServiceDeploymentCatalogQueryReader
    {
        public ServiceDeploymentCatalogSnapshot? GetResult { get; init; }

        public Task<ServiceDeploymentCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
    }

    private sealed class RecordingServingSetQueryReader : IServiceServingSetQueryReader
    {
        public ServiceServingSetSnapshot? GetResult { get; init; }

        public Task<ServiceServingSetSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(GetResult);
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
