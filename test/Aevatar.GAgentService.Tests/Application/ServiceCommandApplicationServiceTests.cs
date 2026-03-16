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
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
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
    public async Task SetDefaultServingRevisionAsync_ShouldRequireDefinition_AndSkipProvisioningWhenMissing()
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

        var act = () => service.SetDefaultServingRevisionAsync(new SetDefaultServingRevisionCommand
        {
            Identity = identity.Clone(),
            RevisionId = "rev-1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Service definition*was not found*");
        provisioner.DefinitionRequests.Should().BeEmpty();
        catalogProjectionPort.ActorIds.Should().BeEmpty();
        dispatchPort.Calls.Should().BeEmpty();
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
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
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
    public async Task ReplaceServiceServingTargetsAsync_ShouldRejectMissingRevisionId()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = CreateService(
            new RecordingCommandTargetProvisioner(),
            new RecordingActorDispatchPort(),
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort(),
            deploymentQueryReader: new RecordingDeploymentQueryReader
            {
                GetResult = new ServiceDeploymentCatalogSnapshot(ServiceKeys.Build(identity), [], DateTimeOffset.UtcNow),
            });

        var act = () => service.ReplaceServiceServingTargetsAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec(),
            },
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("revision_id is required for serving targets.");
    }

    [Fact]
    public async Task ReplaceServiceServingTargetsAsync_ShouldRejectMissingActiveDeployment()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = CreateService(
            new RecordingCommandTargetProvisioner(),
            new RecordingActorDispatchPort(),
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
                        new ServiceDeploymentSnapshot("dep-x", "rev-x", "actor-x", ServiceDeploymentStatus.Deactivated.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    ],
                    DateTimeOffset.UtcNow),
            });

        var act = () => service.ReplaceServiceServingTargetsAsync(new ReplaceServiceServingTargetsCommand
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

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Active deployment for '{ServiceKeys.Build(identity)}' revision 'rev-1' was not found.");
    }

    [Fact]
    public async Task ReplaceServiceServingTargetsAsync_ShouldRejectMissingDeploymentCatalog()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = CreateService(
            new RecordingCommandTargetProvisioner(),
            new RecordingActorDispatchPort(),
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort());

        var act = () => service.ReplaceServiceServingTargetsAsync(new ReplaceServiceServingTargetsCommand
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

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Deployments for '{ServiceKeys.Build(identity)}' were not found.");
    }

    [Fact]
    public async Task ReplaceServiceServingTargetsAsync_ShouldRejectMissingPreparedArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = CreateService(
            new RecordingCommandTargetProvisioner(),
            new RecordingActorDispatchPort(),
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
            });

        var act = () => service.ReplaceServiceServingTargetsAsync(new ReplaceServiceServingTargetsCommand
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

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Prepared artifact for '{ServiceKeys.Build(identity)}' revision 'rev-1' was not found.");
    }

    [Fact]
    public async Task ReplaceServiceServingTargetsAsync_ShouldPreserveExplicitServingFields()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "rev-1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "run"),
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var dispatchPort = new RecordingActorDispatchPort();
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
            artifactStore: artifactStore);

        await service.ReplaceServiceServingTargetsAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec
                {
                    RevisionId = "rev-1",
                    AllocationWeight = 55,
                    ServingState = ServiceServingState.Paused,
                    EnabledEndpointIds = { "chat" },
                },
            },
        });

        var dispatched = dispatchPort.Calls.Should().ContainSingle().Subject.envelope.Payload.Unpack<ReplaceServiceServingTargetsCommand>();
        dispatched.Targets.Should().ContainSingle();
        dispatched.Targets[0].AllocationWeight.Should().Be(55);
        dispatched.Targets[0].ServingState.Should().Be(ServiceServingState.Paused);
        dispatched.Targets[0].EnabledEndpointIds.Should().Equal("chat");
    }

    [Fact]
    public async Task StartServiceRolloutAsync_ShouldResolvePlanAndBaselineTargets()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "rev-base",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-base",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "run")));
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
                        new ServiceDeploymentSnapshot("dep-base", "rev-base", "actor-base", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
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

    [Fact]
    public async Task StartServiceRolloutAsync_ShouldUseProvidedBaselineTargets_AndPreserveExplicitStageValues()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var artifactStore = new InMemoryServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "rev-base",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-base",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "run")));
        await artifactStore.SaveAsync(
            ServiceKeys.Build(identity),
            "rev-2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-2",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "run"),
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var dispatchPort = new RecordingActorDispatchPort();
        var servingSetQueryReader = new RecordingServingSetQueryReader();
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
                        new ServiceDeploymentSnapshot("dep-base", "rev-base", "actor-base", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                        new ServiceDeploymentSnapshot("dep-2", "rev-2", "actor-2", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    ],
                    DateTimeOffset.UtcNow),
            },
            servingSetQueryReader: servingSetQueryReader,
            artifactStore: artifactStore);

        await service.StartServiceRolloutAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            BaselineTargets =
            {
                new ServiceServingTargetSpec
                {
                    DeploymentId = "dep-base",
                    RevisionId = "rev-base",
                    PrimaryActorId = "actor-base",
                    AllocationWeight = 100,
                    ServingState = ServiceServingState.Active,
                    EnabledEndpointIds = { "run" },
                },
            },
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = "rollout-explicit",
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
                                AllocationWeight = 35,
                                ServingState = ServiceServingState.Draining,
                                EnabledEndpointIds = { "chat" },
                            },
                        },
                    },
                },
            },
        });

        servingSetQueryReader.Identities.Should().BeEmpty();
        var dispatched = dispatchPort.Calls.Should().ContainSingle().Subject.envelope.Payload.Unpack<StartServiceRolloutCommand>();
        dispatched.BaselineTargets.Should().ContainSingle(x => x.DeploymentId == "dep-base");
        dispatched.Plan.Stages.Should().ContainSingle();
        dispatched.Plan.Stages[0].Targets.Should().ContainSingle();
        dispatched.Plan.Stages[0].Targets[0].DeploymentId.Should().Be("dep-2");
        dispatched.Plan.Stages[0].Targets[0].AllocationWeight.Should().Be(35);
        dispatched.Plan.Stages[0].Targets[0].ServingState.Should().Be(ServiceServingState.Draining);
        dispatched.Plan.Stages[0].Targets[0].EnabledEndpointIds.Should().Equal("chat");
    }

    [Fact]
    public async Task StartServiceRolloutAsync_ShouldUseEmptyBaseline_WhenServingSetSnapshotIsMissing()
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
        var dispatchPort = new RecordingActorDispatchPort();
        var servingSetQueryReader = new RecordingServingSetQueryReader();
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
                        new ServiceDeploymentSnapshot("dep-2", "rev-2", "actor-2", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    ],
                    DateTimeOffset.UtcNow),
            },
            servingSetQueryReader: servingSetQueryReader,
            artifactStore: artifactStore);

        await service.StartServiceRolloutAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = "rollout-empty-baseline",
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

        servingSetQueryReader.Identities.Should().ContainSingle(x => x.ServiceId == identity.ServiceId);
        var dispatched = dispatchPort.Calls.Should().ContainSingle().Subject.envelope.Payload.Unpack<StartServiceRolloutCommand>();
        dispatched.BaselineTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task StartServiceRolloutAsync_ShouldRejectMissingPlan()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var service = CreateService(
            new RecordingCommandTargetProvisioner(),
            new RecordingActorDispatchPort(),
            new RecordingCatalogQueryReader
            {
                GetResult = CreateCatalogSnapshot(identity),
            },
            new RecordingCatalogProjectionPort(),
            new RecordingRevisionProjectionPort(),
            deploymentQueryReader: new RecordingDeploymentQueryReader
            {
                GetResult = new ServiceDeploymentCatalogSnapshot(ServiceKeys.Build(identity), [], DateTimeOffset.UtcNow),
            });

        var act = () => service.StartServiceRolloutAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
        });

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StartServiceRolloutAsync_ShouldTranslateUnknownBaselineServingStateToUnspecified()
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
        var dispatchPort = new RecordingActorDispatchPort();
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
                        new ServiceDeploymentSnapshot("dep-2", "rev-2", "actor-2", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    ],
                    DateTimeOffset.UtcNow),
            },
            servingSetQueryReader: new RecordingServingSetQueryReader
            {
                GetResult = new ServiceServingSetSnapshot(
                    ServiceKeys.Build(identity),
                    3,
                    string.Empty,
                    [
                        new ServiceServingTargetSnapshot("dep-base", "rev-base", "actor-base", 100, "not-a-state", ["run"]),
                    ],
                    DateTimeOffset.UtcNow),
            },
            artifactStore: artifactStore);

        await service.StartServiceRolloutAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = "rollout-state-parse",
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

        var dispatched = dispatchPort.Calls.Should().ContainSingle().Subject.envelope.Payload.Unpack<StartServiceRolloutCommand>();
        dispatched.BaselineTargets.Should().ContainSingle();
        dispatched.BaselineTargets[0].ServingState.Should().Be(ServiceServingState.Unspecified);
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

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
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

        public List<ServiceIdentity> Identities { get; } = [];

        public Task<ServiceServingSetSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            Identities.Add(identity.Clone());
            return Task.FromResult(GetResult);
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
