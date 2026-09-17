using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Core.Services;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServiceServingRolloutGAgentTests
{
    [Fact]
    public async Task ServiceRolloutManager_ShouldDriveServingTargetsAndLifecycle()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateRolloutAgent(new InMemoryEventStore(), dispatchPort, identity);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-a",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 70, "run")),
                CreateStage("stage-b", CreateTarget("dep-b", "r2", "actor-b", 100, "chat"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        await agent.HandlePauseAsync(new PauseServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
            Reason = "hold",
        });
        await agent.HandleResumeAsync(new ResumeServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
        });
        await agent.HandleAdvanceAsync(new AdvanceServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
        });

        dispatchPort.Commands.Should().HaveCount(2);
        dispatchPort.Commands[0].actorId.Should().Be(ServiceActorIds.ServingSet(identity));
        dispatchPort.Commands[0].command.RolloutId.Should().Be("rollout-a");
        dispatchPort.Commands[0].command.Reason.Should().Be("stage:stage-a");
        dispatchPort.Commands[0].command.Targets.Select(x => x.DeploymentId).Should().Equal("dep-a");
        dispatchPort.Commands[1].command.Reason.Should().Be("stage:stage-b");
        dispatchPort.Commands[1].command.Targets.Select(x => x.DeploymentId).Should().Equal("dep-b");

        agent.State.RolloutId.Should().Be("rollout-a");
        agent.State.Status.Should().Be(ServiceRolloutStatus.Completed);
        agent.State.CurrentStageIndex.Should().Be(1);
        agent.State.FailureReason.Should().BeEmpty();
        agent.State.BaselineTargets.Select(x => x.DeploymentId).Should().Equal("dep-base");
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldRollbackToBaselineTargets()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateRolloutAgent(new InMemoryEventStore(), dispatchPort, identity);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-b",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 100, "run")),
                CreateStage("stage-b", CreateTarget("dep-b", "r2", "actor-b", 100, "chat"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "chat") },
        });
        await agent.HandleRollbackAsync(new RollbackServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-b",
            Reason = "manual-rollback",
        });

        dispatchPort.Commands.Should().HaveCount(2);
        dispatchPort.Commands[1].command.Reason.Should().Be("manual-rollback");
        dispatchPort.Commands[1].command.Targets.Select(x => x.DeploymentId).Should().Equal("dep-base");
        agent.State.Status.Should().Be(ServiceRolloutStatus.RolledBack);
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldFailWhenServingUpdateThrows()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingDispatchPort
        {
            ThrowOnCallIndex = 2,
            ExceptionToThrow = new InvalidOperationException("serving unavailable"),
        };
        var agent = CreateRolloutAgent(new InMemoryEventStore(), dispatchPort, identity);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-c",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 60, "run")),
                CreateStage("stage-b", CreateTarget("dep-b", "r2", "actor-b", 100, "chat"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });
        await agent.HandleAdvanceAsync(new AdvanceServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-c",
        });

        agent.State.Status.Should().Be(ServiceRolloutStatus.Failed);
        agent.State.FailureReason.Should().Contain("serving unavailable");
        dispatchPort.Commands.Should().HaveCount(1);
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldRejectInvalidPlanAndDuplicateStart()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateRolloutAgent(new InMemoryEventStore(), new RecordingDispatchPort(), identity);
        await agent.ActivateAsync();

        var invalidPlan = new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = "bad",
            },
        };

        await FluentActions.Invoking(() => agent.HandleStartAsync(invalidPlan))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one rollout stage is required*");

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-d",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 100, "run")),
                CreateStage("stage-b", CreateTarget("dep-b", "r2", "actor-b", 100, "chat"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        await FluentActions.Invoking(() => agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-e",
                CreateStage("stage-b", CreateTarget("dep-b", "r2", "actor-b", 100, "chat"))),
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*active rollout already exists*");
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldAllowRestartAfterCompletedAndRolledBackRollouts()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateRolloutAgent(new InMemoryEventStore(), dispatchPort, identity);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-complete",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 100, "run"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        agent.State.Status.Should().Be(ServiceRolloutStatus.Completed);

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-rollback",
                CreateStage("stage-a", CreateTarget("dep-b", "r2", "actor-b", 100, "run")),
                CreateStage("stage-b", CreateTarget("dep-c", "r3", "actor-c", 100, "chat"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });
        await agent.HandleRollbackAsync(new RollbackServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-rollback",
            Reason = "rollback",
        });

        agent.State.Status.Should().Be(ServiceRolloutStatus.RolledBack);

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-after-rollback",
                CreateStage("stage-a", CreateTarget("dep-d", "r4", "actor-d", 100, "run"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        dispatchPort.Commands.Select(x => x.command.RolloutId).Should().Equal(
            "rollout-complete",
            "rollout-rollback",
            "rollout-rollback",
            "rollout-after-rollback");
        agent.State.RolloutId.Should().Be("rollout-after-rollback");
        agent.State.Status.Should().Be(ServiceRolloutStatus.Completed);
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldAllowRestartAfterFailedRollout()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingDispatchPort
        {
            ThrowOnCallIndex = 1,
            ExceptionToThrow = new InvalidOperationException("serving unavailable"),
        };
        var agent = CreateRolloutAgent(new InMemoryEventStore(), dispatchPort, identity);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-failed",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 100, "run"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        agent.State.Status.Should().Be(ServiceRolloutStatus.Failed);

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-retry",
                CreateStage("stage-a", CreateTarget("dep-b", "r2", "actor-b", 100, "run"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        dispatchPort.Commands.Should().ContainSingle();
        dispatchPort.Commands[0].command.RolloutId.Should().Be("rollout-retry");
        agent.State.RolloutId.Should().Be("rollout-retry");
        agent.State.Status.Should().Be(ServiceRolloutStatus.Completed);
        agent.State.FailureReason.Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldRejectInvalidStageAndRolloutTransitions()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateRolloutAgent(new InMemoryEventStore(), new RecordingDispatchPort(), identity);
        await agent.ActivateAsync();

        await FluentActions.Invoking(() => agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = "bad-plan",
                Stages =
                {
                    new ServiceRolloutStageSpec
                    {
                        Targets = { CreateTarget("dep-a", "r1", "actor-a", 100, "run") },
                    },
                },
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*stage_id is required*");

        await FluentActions.Invoking(() => agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = "bad-plan-2",
                Stages =
                {
                    new ServiceRolloutStageSpec
                    {
                        StageId = "stage-a",
                    },
                },
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rollout stage targets are required*");

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-f",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 100, "run")),
                CreateStage("stage-b", CreateTarget("dep-b", "r2", "actor-b", 100, "chat"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        await agent.HandlePauseAsync(new PauseServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-f",
            Reason = "hold",
        });

        await FluentActions.Invoking(() => agent.HandleAdvanceAsync(new AdvanceServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-f",
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*paused*");

        await FluentActions.Invoking(() => agent.HandleAdvanceAsync(new AdvanceServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "other",
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match active rollout*");

        await FluentActions.Invoking(() => agent.HandleAdvanceAsync(new AdvanceServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = " ",
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rollout_id is required*");
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldTreatCompletedPauseResumeAndRollbackAsNoOp()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingDispatchPort();
        var agent = CreateRolloutAgent(new InMemoryEventStore(), dispatchPort, identity);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-g",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 100, "run"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        var versionBeforeNoOps = agent.State.LastAppliedEventVersion;

        await agent.HandlePauseAsync(new PauseServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-g",
            Reason = "ignored",
        });
        await agent.HandleResumeAsync(new ResumeServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-g",
        });
        await agent.HandleRollbackAsync(new RollbackServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-g",
            Reason = "ignored",
        });

        dispatchPort.Commands.Should().HaveCount(1);
        agent.State.Status.Should().Be(ServiceRolloutStatus.Completed);
        agent.State.LastAppliedEventVersion.Should().Be(versionBeforeNoOps);

        await FluentActions.Invoking(() => agent.HandleAdvanceAsync(new AdvanceServiceRolloutCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-g",
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already finalized*");
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldPersistCommandObservation_AfterHandledPause()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var eventStore = new InMemoryEventStore();
        var agent = CreateRolloutAgent(eventStore, new RecordingDispatchPort(), identity);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-observed",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 100, "run")),
                CreateStage("stage-b", CreateTarget("dep-b", "r2", "actor-b", 100, "run"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        await agent.HandleEventAsync(new EventEnvelope
        {
            Id = "cmd-pause-rollout",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new PauseServiceRolloutCommand
            {
                Identity = identity.Clone(),
                RolloutId = "rollout-observed",
                Reason = "hold",
            }),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-pause-rollout",
            },
        });

        var persisted = await eventStore.GetEventsAsync(ServiceActorIds.Rollout(identity));
        persisted.Should().Contain(x => x.EventData.Is(ServiceRolloutPausedEvent.Descriptor));
        var observation = persisted
            .Where(x => x.EventData.Is(ServiceRolloutCommandObservedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<ServiceRolloutCommandObservedEvent>())
            .Single();
        observation.CommandId.Should().Be("cmd-pause-rollout");
        observation.CorrelationId.Should().Be("corr-pause-rollout");
        observation.Status.Should().Be(ServiceRolloutStatus.Paused);
        observation.WasNoOp.Should().BeFalse();
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldPersistNoOpObservation_ForCompletedPause()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var eventStore = new InMemoryEventStore();
        var agent = CreateRolloutAgent(eventStore, new RecordingDispatchPort(), identity);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-complete-observed",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 100, "run"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        await agent.HandleEventAsync(new EventEnvelope
        {
            Id = "cmd-pause-noop",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new PauseServiceRolloutCommand
            {
                Identity = identity.Clone(),
                RolloutId = "rollout-complete-observed",
                Reason = "ignored",
            }),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "corr-pause-noop",
            },
        });

        var persisted = await eventStore.GetEventsAsync(ServiceActorIds.Rollout(identity));
        var pausedEvents = persisted.Where(x => x.EventData.Is(ServiceRolloutPausedEvent.Descriptor)).ToList();
        pausedEvents.Should().BeEmpty();
        var observation = persisted
            .Where(x => x.EventData.Is(ServiceRolloutCommandObservedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<ServiceRolloutCommandObservedEvent>())
            .Single();
        observation.CommandId.Should().Be("cmd-pause-noop");
        observation.Status.Should().Be(ServiceRolloutStatus.Completed);
        observation.WasNoOp.Should().BeTrue();
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldFailImmediatelyWhenInitialServingUpdateThrows()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingDispatchPort
        {
            ThrowOnCallIndex = 1,
            ExceptionToThrow = new InvalidOperationException("initial serving unavailable"),
        };
        var agent = CreateRolloutAgent(new InMemoryEventStore(), dispatchPort, identity);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = CreateRolloutPlan(
                "rollout-h",
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 100, "run"))),
            BaselineTargets = { CreateTarget("dep-base", "r0", "actor-base", 100, "run") },
        });

        dispatchPort.Commands.Should().BeEmpty();
        agent.State.Status.Should().Be(ServiceRolloutStatus.Failed);
        agent.State.CurrentStageIndex.Should().Be(-1);
        agent.State.FailureReason.Should().Contain("initial serving unavailable");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldResolveTargetsFromDeploymentAndArtifact()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "rev-1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var deploymentQueryReader = new RecordingDeploymentQueryReader
        {
            GetResult = new ServiceDeploymentCatalogSnapshot(
                ServiceKeys.Build(identity),
                [
                    new ServiceDeploymentSnapshot("dep-1", "rev-1", "actor-1", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                ],
                DateTimeOffset.UtcNow),
        };
        var agent = CreateServingSetAgent(
            new InMemoryEventStore(),
            ServiceActorIds.ServingSet(identity),
            new DefaultServiceServingTargetResolver(deploymentQueryReader, revisionCatalog));
        await agent.ActivateAsync();

        await agent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
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

        agent.State.Generation.Should().Be(1);
        agent.State.Targets.Should().ContainSingle();
        agent.State.Targets[0].DeploymentId.Should().Be("dep-1");
        agent.State.Targets[0].PrimaryActorId.Should().Be("actor-1");
        agent.State.Targets[0].AllocationWeight.Should().Be(100);
        agent.State.Targets[0].ServingState.Should().Be(ServiceServingState.Active);
        agent.State.Targets[0].EnabledEndpointIds.Should().ContainSingle("chat");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldAcceptResolvedServingTargetsWithoutResolverLookup()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = CreateServingSetAgent(
            new InMemoryEventStore(),
            ServiceActorIds.ServingSet(identity),
            dispatchPort: dispatchPort);
        await agent.ActivateAsync();

        await agent.HandleReplaceResolvedAsync(new ReplaceResolvedServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Reason = "deployment activation",
            Targets =
            {
                new ServiceServingTargetSpec
                {
                    DeploymentId = "dep-1",
                    RevisionId = "rev-1",
                    PrimaryActorId = "actor-1",
                    AllocationWeight = 100,
                    ServingState = ServiceServingState.Active,
                    EnabledEndpointIds = { "chat" },
                },
            },
        });

        agent.State.Generation.Should().Be(1);
        agent.State.Targets.Should().ContainSingle();
        agent.State.Targets[0].DeploymentId.Should().Be("dep-1");
        agent.State.Targets[0].RevisionId.Should().Be("rev-1");
        agent.State.Targets[0].PrimaryActorId.Should().Be("actor-1");
        agent.State.Targets[0].AllocationWeight.Should().Be(100);
        agent.State.Targets[0].EnabledEndpointIds.Should().Equal("chat");
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].ActorId.Should().Be(ServiceActorIds.InvocationCatalog(identity));
        var observation = dispatchPort.Calls[0].Envelope.Payload.Unpack<ObserveServiceInvocationServingCommand>();
        observation.Identity.Should().BeEquivalentTo(identity);
        observation.SourceServingVersion.Should().Be(1);
        observation.ServingTargets.Should().ContainSingle();
        observation.ServingTargets[0].DeploymentId.Should().Be("dep-1");
        observation.ServingTargets[0].RevisionId.Should().Be("rev-1");
        observation.ServingTargets[0].PrimaryActorId.Should().Be("actor-1");
        observation.ServingTargets[0].EnabledEndpointIds.Should().Equal("chat");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldRemoveDeploymentFromActorStateIdempotently()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingActorDispatchPort();
        var actorId = ServiceActorIds.ServingSet(identity);
        var agent = CreateServingSetAgent(eventStore, actorId, dispatchPort: dispatchPort);
        await agent.ActivateAsync();
        await agent.HandleReplaceResolvedAsync(new ReplaceResolvedServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Reason = "initial serving",
            Targets =
            {
                CreateTarget("dep-keep", "rev-keep", "actor-keep", 40, "chat"),
                CreateTarget("dep-remove", "rev-remove", "actor-remove", 60, "run"),
            },
        });

        await agent.HandleRemoveDeploymentAsync(new RemoveDeploymentFromServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-remove",
            Reason = "deactivate:dep-remove",
            ReplyActorId = "deployment-manager",
        });
        await agent.HandleRemoveDeploymentAsync(new RemoveDeploymentFromServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-remove",
            Reason = "deactivate:dep-remove",
            ReplyActorId = "deployment-manager",
        });

        agent.State.Generation.Should().Be(2);
        agent.State.Targets.Should().ContainSingle();
        agent.State.Targets[0].DeploymentId.Should().Be("dep-keep");
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(2);
        dispatchPort.Calls.Should().HaveCount(4);
        var observation = dispatchPort.Calls[1].Envelope.Payload.Unpack<ObserveServiceInvocationServingCommand>();
        observation.SourceServingVersion.Should().Be(2);
        observation.ServingTargets.Should().ContainSingle();
        observation.ServingTargets[0].DeploymentId.Should().Be("dep-keep");
        dispatchPort.Calls[2].Envelope.Payload.Unpack<ServiceServingTargetsRemovedAck>()
            .DeploymentId.Should().Be("dep-remove");
        dispatchPort.Calls[3].Envelope.Payload.Unpack<ServiceServingTargetsRemovedAck>()
            .DeploymentId.Should().Be("dep-remove");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldRejectStaleDeploymentRemovalAfterNewServingOperation()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingActorDispatchPort();
        var actorId = ServiceActorIds.ServingSet(identity);
        var agent = CreateServingSetAgent(eventStore, actorId, dispatchPort: dispatchPort);
        await agent.ActivateAsync();
        await agent.HandleReplaceResolvedAsync(new ReplaceResolvedServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            ActivationAttemptId = "attempt-old",
            OperationId = "operation-old",
            OperationSequence = 1,
            ReplyActorId = "deployment-manager",
            Targets = { CreateTarget("dep-1", "rev-1", "actor-1", 100, "chat") },
        });
        await agent.HandleReplaceResolvedAsync(new ReplaceResolvedServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            ActivationAttemptId = "attempt-new",
            OperationId = "operation-new",
            OperationSequence = 2,
            ReplyActorId = "deployment-manager",
            Targets = { CreateTarget("dep-1", "rev-1", "actor-1", 100, "chat") },
        });

        await agent.HandleRemoveDeploymentAsync(new RemoveDeploymentFromServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-1",
            RevisionId = "rev-1",
            PrimaryActorId = "actor-1",
            ActivationAttemptId = "attempt-old",
            ServingTargetOperationId = "operation-old",
            DeactivationOperationId = "deactivation-old",
            ReplyActorId = "deployment-manager",
        });

        agent.State.Targets.Should().ContainSingle();
        agent.State.Targets[0].ServingTargetOperationId.Should().Be("operation-new");
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(2);
        dispatchPort.Calls.Should().HaveCount(5);
        var superseded = dispatchPort.Calls[^1].Envelope.Payload
            .Unpack<ServiceServingTargetsRemovedAck>();
        superseded.Disposition.Should().Be(
            ServiceServingTargetRemovalDisposition.Superseded);
        superseded.DeactivationOperationId.Should().Be("deactivation-old");
        superseded.ActualActivationAttemptId.Should().Be("attempt-new");
        superseded.ActualServingTargetOperationId.Should().Be("operation-new");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldNotTreatBlankRemovalFencesAsWildcards()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = CreateServingSetAgent(
            new InMemoryEventStore(),
            ServiceActorIds.ServingSet(identity),
            dispatchPort: dispatchPort);
        await agent.ActivateAsync();
        await agent.HandleReplaceResolvedAsync(new ReplaceResolvedServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            ActivationAttemptId = "attempt-current",
            OperationId = "operation-current",
            OperationSequence = 1,
            ReplyActorId = "deployment-manager",
            Targets = { CreateTarget("dep-1", "rev-1", "actor-1", 100, "chat") },
        });

        await agent.HandleRemoveDeploymentAsync(new RemoveDeploymentFromServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            DeploymentId = "dep-1",
            RevisionId = "rev-1",
            PrimaryActorId = "actor-1",
            DeactivationOperationId = "deactivation-legacy",
            ReplyActorId = "deployment-manager",
        });

        agent.State.Targets.Should().ContainSingle();
        var ack = dispatchPort.Calls[^1].Envelope.Payload
            .Unpack<ServiceServingTargetsRemovedAck>();
        ack.Disposition.Should().Be(ServiceServingTargetRemovalDisposition.Superseded);
        ack.ActualActivationAttemptId.Should().Be("attempt-current");
        ack.ActualServingTargetOperationId.Should().Be("operation-current");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldCommitResolvedOperationBeforeAckAndReAckExactDuplicate()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingActorDispatchPort();
        var actorId = ServiceActorIds.ServingSet(identity);
        var agent = CreateServingSetAgent(eventStore, actorId, dispatchPort: dispatchPort);
        await agent.ActivateAsync();
        var command = new ReplaceResolvedServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Reason = "deployment activation",
            ActivationAttemptId = "attempt-1",
            OperationId = "operation-1",
            OperationSequence = 1,
            ReplyActorId = "deployment-manager-1",
            Targets =
            {
                CreateTarget("dep-1", "rev-1", "actor-1", 100, "chat"),
            },
        };

        await agent.HandleReplaceResolvedAsync(command);

        agent.State.Generation.Should().Be(1);
        agent.State.LastAppliedEventVersion.Should().Be(1);
        agent.State.LastResolvedOperationId.Should().Be("operation-1");
        dispatchPort.Calls.Should().HaveCount(2);
        dispatchPort.Calls.Select(x => x.Envelope.Route.PublisherActorId)
            .Should().OnlyContain(x => x == actorId);
        dispatchPort.Calls[0].ActorId.Should().Be(ServiceActorIds.InvocationCatalog(identity));
        dispatchPort.Calls[1].ActorId.Should().Be("deployment-manager-1");
        var firstAck = dispatchPort.Calls[1].Envelope.Payload.Unpack<ServiceServingTargetsAppliedAck>();
        firstAck.Identity.Should().BeEquivalentTo(identity);
        firstAck.RevisionId.Should().Be("rev-1");
        firstAck.DeploymentId.Should().Be("dep-1");
        firstAck.ActivationAttemptId.Should().Be("attempt-1");
        firstAck.OperationId.Should().Be("operation-1");
        firstAck.ServingGeneration.Should().Be(agent.State.Generation);
        firstAck.AppliedAt.Should().Be(agent.State.LastResolvedAppliedAt);
        (await eventStore.GetEventsAsync(actorId)).Should().ContainSingle();

        await agent.HandleReplaceResolvedAsync(command.Clone());

        agent.State.Generation.Should().Be(1);
        agent.State.LastAppliedEventVersion.Should().Be(1);
        (await eventStore.GetEventsAsync(actorId)).Should().ContainSingle();
        dispatchPort.Calls.Should().HaveCount(4);
        dispatchPort.Calls[2].ActorId.Should().Be(ServiceActorIds.InvocationCatalog(identity));
        dispatchPort.Calls[2].Envelope.Id.Should().Be(dispatchPort.Calls[0].Envelope.Id);
        dispatchPort.Calls[3].ActorId.Should().Be("deployment-manager-1");
        var duplicateAck = dispatchPort.Calls[3].Envelope.Payload.Unpack<ServiceServingTargetsAppliedAck>();
        duplicateAck.Should().BeEquivalentTo(firstAck);
        dispatchPort.Calls[3].Envelope.Id.Should().Be(dispatchPort.Calls[1].Envelope.Id);
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldWithholdAckUntilObservationAdmissionAndConvergeOnDuplicate()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var invocationCatalog = GAgentServiceTestKit.CreateStatefulAgent<
            ServiceInvocationCatalogGAgent,
            ServiceInvocationCatalogState>(
            new InMemoryEventStore(),
            ServiceActorIds.InvocationCatalog(identity),
            static () => new ServiceInvocationCatalogGAgent(new ServiceInvokeReadinessEvaluator()));
        await invocationCatalog.ActivateAsync();
        var dispatchPort = new RejectFirstObservationDispatchPort(invocationCatalog);
        var actorId = ServiceActorIds.ServingSet(identity);
        var agent = CreateServingSetAgent(eventStore, actorId, dispatchPort: dispatchPort);
        await agent.ActivateAsync();
        var command = CreateResolvedActivationCommand(
            identity,
            "attempt-1",
            "operation-1",
            "dep-1",
            "rev-1",
            "actor-1");

        await FluentActions.Invoking(() => agent.HandleReplaceResolvedAsync(command))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*observation was not admitted*");

        agent.State.Generation.Should().Be(1);
        agent.State.ResolvedOperations.Should().ContainKey("operation-1");
        (await eventStore.GetEventsAsync(actorId)).Should().ContainSingle();
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls.Should().NotContain(x =>
            x.Envelope.Payload.Is(ServiceServingTargetsAppliedAck.Descriptor));

        await agent.HandleReplaceResolvedAsync(command.Clone());

        agent.State.Generation.Should().Be(1);
        (await eventStore.GetEventsAsync(actorId)).Should().ContainSingle();
        dispatchPort.Calls.Should().HaveCount(3);
        dispatchPort.Calls[1].Envelope.Id.Should().Be(dispatchPort.Calls[0].Envelope.Id);
        var observation = dispatchPort.Calls[1].Envelope.Payload.Unpack<ObserveServiceInvocationServingCommand>();
        observation.SourceServingVersion.Should().Be(1);
        invocationCatalog.State.SourceServingVersion.Should().Be(1);
        invocationCatalog.State.ServingTargets.Should().ContainSingle(x =>
            x.DeploymentId == "dep-1" && x.RevisionId == "rev-1");
        var ack = dispatchPort.Calls[2].Envelope.Payload.Unpack<ServiceServingTargetsAppliedAck>();
        ack.OperationId.Should().Be("operation-1");
        ack.ServingGeneration.Should().Be(1);
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldRejectOperationReusedByDifferentAttempt()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = CreateServingSetAgent(
            new InMemoryEventStore(),
            ServiceActorIds.ServingSet(identity),
            dispatchPort: dispatchPort);
        await agent.ActivateAsync();
        var command = new ReplaceResolvedServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            ActivationAttemptId = "attempt-1",
            OperationId = "operation-1",
            OperationSequence = 1,
            ReplyActorId = "deployment-manager-1",
            Targets =
            {
                CreateTarget("dep-1", "rev-1", "actor-1", 100, "chat"),
            },
        };
        await agent.HandleReplaceResolvedAsync(command);
        var version = agent.State.LastAppliedEventVersion;

        var conflicting = command.Clone();
        conflicting.ActivationAttemptId = "attempt-2";
        await FluentActions.Invoking(() => agent.HandleReplaceResolvedAsync(conflicting))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already bound to different serving update facts*");

        agent.State.Generation.Should().Be(1);
        agent.State.LastAppliedEventVersion.Should().Be(version);
        dispatchPort.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldReAckOlderOperationWithoutRollingBackNewerTargets()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingActorDispatchPort();
        var actorId = ServiceActorIds.ServingSet(identity);
        var agent = CreateServingSetAgent(eventStore, actorId, dispatchPort: dispatchPort);
        await agent.ActivateAsync();
        var operation1 = CreateResolvedActivationCommand(
            identity,
            "attempt-1",
            "operation-1",
            "dep-1",
            "rev-1",
            "actor-1");
        var operation2 = CreateResolvedActivationCommand(
            identity,
            "attempt-2",
            "operation-2",
            "dep-2",
            "rev-2",
            "actor-2");
        operation2.OperationSequence = 2;

        await agent.HandleReplaceResolvedAsync(operation1);
        await agent.HandleReplaceResolvedAsync(operation2);
        await agent.HandleReplaceResolvedAsync(operation1.Clone());

        agent.State.Generation.Should().Be(2);
        agent.State.LastAppliedEventVersion.Should().Be(2);
        agent.State.Targets.Single().DeploymentId.Should().Be("dep-2");
        agent.State.ResolvedOperations.Keys.Should().BeEquivalentTo("operation-1", "operation-2");
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(2);
        dispatchPort.Calls.Should().HaveCount(6);
        var delayedAck = dispatchPort.Calls[^1].Envelope.Payload.Unpack<ServiceServingTargetsAppliedAck>();
        delayedAck.OperationId.Should().Be("operation-1");
        delayedAck.ServingGeneration.Should().Be(1);
        delayedAck.DeploymentId.Should().Be("dep-1");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldSupersedeUnseenOlderOperationWithoutRollbackAcrossReplay()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.ServingSet(identity);
        var dispatchPort = new RecordingActorDispatchPort();
        var agent = CreateServingSetAgent(eventStore, actorId, dispatchPort: dispatchPort);
        await agent.ActivateAsync();
        var newest = CreateResolvedActivationCommand(
            identity,
            "attempt-new",
            "operation-new",
            "dep-new",
            "rev-new",
            "actor-new",
            operationSequence: 2);
        var unseenOlder = CreateResolvedActivationCommand(
            identity,
            "attempt-old",
            "operation-old",
            "dep-old",
            "rev-old",
            "actor-old",
            operationSequence: 1);

        await agent.HandleReplaceResolvedAsync(newest);
        var committedVersion = agent.State.LastAppliedEventVersion;
        await agent.HandleReplaceResolvedAsync(unseenOlder);

        agent.State.LastAppliedEventVersion.Should().Be(committedVersion);
        agent.State.LastResolvedOperationSequence.Should().Be(2);
        agent.State.Targets.Should().ContainSingle(target =>
            target.DeploymentId == "dep-new" &&
            target.ServingTargetOperationId == "operation-new");
        agent.State.ResolvedOperations.Should().ContainKey("operation-new");
        agent.State.ResolvedOperations.Should().NotContainKey("operation-old");
        var ack = dispatchPort.Calls[^1].Envelope.Payload.Unpack<ServiceServingTargetsAppliedAck>();
        ack.OperationId.Should().Be("operation-old");
        ack.Disposition.Should().Be(ServiceServingTargetsApplyDisposition.Superseded);
        ack.SupersededByOperationSequence.Should().Be(2);

        await agent.DeactivateAsync();
        var replayed = CreateServingSetAgent(eventStore, actorId);
        await replayed.ActivateAsync();

        replayed.State.LastResolvedOperationSequence.Should().Be(2);
        replayed.State.Targets.Should().ContainSingle(target =>
            target.DeploymentId == "dep-new" &&
            target.ServingTargetOperationId == "operation-new");
        replayed.State.ResolvedOperations.Should().ContainKey("operation-new");
        replayed.State.ResolvedOperations.Should().NotContainKey("operation-old");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldBoundResolvedOperationHistoryAndFencePrunedReplay()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.ServingSet(identity);
        var agent = CreateServingSetAgent(eventStore, actorId);
        await agent.ActivateAsync();

        for (var sequence = 1; sequence <= 65; sequence++)
        {
            await agent.HandleReplaceResolvedAsync(CreateResolvedActivationCommand(
                identity,
                $"attempt-{sequence}",
                $"operation-{sequence}",
                $"dep-{sequence}",
                $"rev-{sequence}",
                $"actor-{sequence}",
                operationSequence: sequence));
        }

        agent.State.ResolvedOperations.Should().HaveCount(64);
        agent.State.ResolvedOperations.Should().NotContainKey("operation-1");
        agent.State.LastResolvedOperationSequence.Should().Be(65);
        agent.State.Targets.Should().ContainSingle(target => target.DeploymentId == "dep-65");
        await agent.DeactivateAsync();

        var replayDispatch = new RecordingActorDispatchPort();
        var replayed = CreateServingSetAgent(eventStore, actorId, dispatchPort: replayDispatch);
        await replayed.ActivateAsync();
        var replayedVersion = replayed.State.LastAppliedEventVersion;
        await replayed.HandleReplaceResolvedAsync(CreateResolvedActivationCommand(
            identity,
            "attempt-1",
            "operation-1",
            "dep-1",
            "rev-1",
            "actor-1",
            operationSequence: 1));

        replayed.State.LastAppliedEventVersion.Should().Be(replayedVersion);
        replayed.State.ResolvedOperations.Should().HaveCount(64);
        replayed.State.Targets.Should().ContainSingle(target => target.DeploymentId == "dep-65");
        var ack = replayDispatch.Calls.Should().ContainSingle().Subject.Envelope.Payload
            .Unpack<ServiceServingTargetsAppliedAck>();
        ack.Disposition.Should().Be(ServiceServingTargetsApplyDisposition.Superseded);
        ack.SupersededByOperationSequence.Should().Be(65);
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldReAckOperationAcrossLegacyNoOperationUpdate()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var dispatchPort = new RecordingActorDispatchPort();
        var actorId = ServiceActorIds.ServingSet(identity);
        var agent = CreateServingSetAgent(eventStore, actorId, dispatchPort: dispatchPort);
        await agent.ActivateAsync();
        var operation = CreateResolvedActivationCommand(
            identity,
            "attempt-1",
            "operation-1",
            "dep-1",
            "rev-1",
            "actor-1");
        await agent.HandleReplaceResolvedAsync(operation);
        await agent.HandleReplaceResolvedAsync(new ReplaceResolvedServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Reason = "legacy resolved update",
            Targets =
            {
                CreateTarget("dep-legacy", "rev-legacy", "actor-legacy", 100, "run"),
            },
        });

        await agent.HandleReplaceResolvedAsync(operation.Clone());

        agent.State.Generation.Should().Be(2);
        agent.State.LastAppliedEventVersion.Should().Be(2);
        agent.State.Targets.Single().DeploymentId.Should().Be("dep-legacy");
        (await eventStore.GetEventsAsync(actorId)).Should().HaveCount(2);
        dispatchPort.Calls.Should().HaveCount(5);
        var delayedAck = dispatchPort.Calls[^1].Envelope.Payload.Unpack<ServiceServingTargetsAppliedAck>();
        delayedAck.OperationId.Should().Be("operation-1");
        delayedAck.ServingGeneration.Should().Be(1);
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldRejectMissingResolutionFacts()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var serviceKey = ServiceKeys.Build(identity);
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();

        var missingRevisionAgent = CreateServingSetAgent(
            new InMemoryEventStore(),
            ServiceActorIds.ServingSet(identity),
            new DefaultServiceServingTargetResolver(new RecordingDeploymentQueryReader(), revisionCatalog));
        await missingRevisionAgent.ActivateAsync();

        await FluentActions.Invoking(() => missingRevisionAgent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec(),
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("revision_id is required for serving targets.");

        var missingDeploymentAgent = CreateServingSetAgent(
            new InMemoryEventStore(),
            ServiceActorIds.ServingSet(identity),
            new DefaultServiceServingTargetResolver(new RecordingDeploymentQueryReader(), revisionCatalog));
        await missingDeploymentAgent.ActivateAsync();

        await FluentActions.Invoking(() => missingDeploymentAgent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec
                {
                    RevisionId = "rev-1",
                },
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Deployments for '{serviceKey}' were not found.");

        var inactiveDeploymentAgent = CreateServingSetAgent(
            new InMemoryEventStore(),
            ServiceActorIds.ServingSet(identity),
            new DefaultServiceServingTargetResolver(
                new RecordingDeploymentQueryReader
                {
                    GetResult = new ServiceDeploymentCatalogSnapshot(
                        serviceKey,
                        [
                            new ServiceDeploymentSnapshot("dep-x", "rev-x", "actor-x", ServiceDeploymentStatus.Deactivated.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                        ],
                        DateTimeOffset.UtcNow),
                },
                revisionCatalog));
        await inactiveDeploymentAgent.ActivateAsync();

        await FluentActions.Invoking(() => inactiveDeploymentAgent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec
                {
                    RevisionId = "rev-1",
                },
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Active deployment for '{serviceKey}' revision 'rev-1' was not found.");

        var missingArtifactAgent = CreateServingSetAgent(
            new InMemoryEventStore(),
            ServiceActorIds.ServingSet(identity),
            new DefaultServiceServingTargetResolver(
                new RecordingDeploymentQueryReader
                {
                    GetResult = new ServiceDeploymentCatalogSnapshot(
                        serviceKey,
                        [
                            new ServiceDeploymentSnapshot("dep-1", "rev-1", "actor-1", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                        ],
                        DateTimeOffset.UtcNow),
                },
                revisionCatalog));
        await missingArtifactAgent.ActivateAsync();

        await FluentActions.Invoking(() => missingArtifactAgent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec
                {
                    RevisionId = "rev-1",
                },
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Prepared artifact for '{serviceKey}' revision 'rev-1' was not found.");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldPreserveExplicitServingFieldsDuringResolution()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "rev-1",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-1",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "run"),
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var agent = CreateServingSetAgent(
            new InMemoryEventStore(),
            ServiceActorIds.ServingSet(identity),
            new DefaultServiceServingTargetResolver(
                new RecordingDeploymentQueryReader
                {
                    GetResult = new ServiceDeploymentCatalogSnapshot(
                        ServiceKeys.Build(identity),
                        [
                            new ServiceDeploymentSnapshot("dep-1", "rev-1", "actor-1", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                        ],
                        DateTimeOffset.UtcNow),
                },
                revisionCatalog));
        await agent.ActivateAsync();

        await agent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
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

        agent.State.Targets.Should().ContainSingle();
        agent.State.Targets[0].DeploymentId.Should().Be("dep-1");
        agent.State.Targets[0].AllocationWeight.Should().Be(55);
        agent.State.Targets[0].ServingState.Should().Be(ServiceServingState.Paused);
        agent.State.Targets[0].EnabledEndpointIds.Should().Equal("chat");
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldResolvePlanAndExplicitBaselineTargets()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "rev-base",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-base",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "run")));
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "rev-2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-2",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "run"),
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "chat")));
        var dispatchPort = new RecordingDispatchPort();
        var resolver = new DefaultServiceServingTargetResolver(
            new RecordingDeploymentQueryReader
            {
                GetResult = new ServiceDeploymentCatalogSnapshot(
                    ServiceKeys.Build(identity),
                    [
                        new ServiceDeploymentSnapshot("dep-base", "rev-base", "actor-base", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                        new ServiceDeploymentSnapshot("dep-2", "rev-2", "actor-2", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    ],
                    DateTimeOffset.UtcNow),
            },
            revisionCatalog);
        var agent = CreateRolloutAgent(new InMemoryEventStore(), dispatchPort, identity, resolver);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            BaselineTargets =
            {
                new ServiceServingTargetSpec
                {
                    RevisionId = "rev-base",
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

        dispatchPort.Commands.Should().ContainSingle();
        dispatchPort.Commands[0].command.Targets.Should().ContainSingle();
        dispatchPort.Commands[0].command.Targets[0].DeploymentId.Should().Be("dep-2");
        dispatchPort.Commands[0].command.Targets[0].PrimaryActorId.Should().Be("actor-2");
        dispatchPort.Commands[0].command.Targets[0].AllocationWeight.Should().Be(35);
        dispatchPort.Commands[0].command.Targets[0].ServingState.Should().Be(ServiceServingState.Draining);
        dispatchPort.Commands[0].command.Targets[0].EnabledEndpointIds.Should().Equal("chat");
        agent.State.BaselineTargets.Should().ContainSingle();
        agent.State.BaselineTargets[0].DeploymentId.Should().Be("dep-base");
        agent.State.BaselineTargets[0].PrimaryActorId.Should().Be("actor-base");
        agent.State.BaselineTargets[0].EnabledEndpointIds.Should().ContainSingle("run");
        agent.State.Plan.Stages[0].Targets[0].DeploymentId.Should().Be("dep-2");
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldUseServingSnapshotBaselineWhenExplicitBaselineMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "rev-2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-2",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "run")));
        var servingSetQueryReader = new RecordingServingSetQueryReader
        {
            GetResult = new ServiceServingSetSnapshot(
                ServiceKeys.Build(identity),
                3,
                string.Empty,
                [
                    new ServiceServingTargetSnapshot("dep-base", "rev-base", "actor-base", 100, "not-a-state", ["run"]),
                ],
                DateTimeOffset.UtcNow),
        };
        var agent = CreateRolloutAgent(
            new InMemoryEventStore(),
            new RecordingDispatchPort(),
            identity,
            new DefaultServiceServingTargetResolver(
                new RecordingDeploymentQueryReader
                {
                    GetResult = new ServiceDeploymentCatalogSnapshot(
                        ServiceKeys.Build(identity),
                        [
                            new ServiceDeploymentSnapshot("dep-2", "rev-2", "actor-2", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                        ],
                        DateTimeOffset.UtcNow),
                },
                revisionCatalog),
            servingSetQueryReader);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
        {
            Identity = identity.Clone(),
            Plan = new ServiceRolloutPlanSpec
            {
                RolloutId = "rollout-baseline",
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
        agent.State.BaselineTargets.Should().ContainSingle();
        agent.State.BaselineTargets[0].DeploymentId.Should().Be("dep-base");
        agent.State.BaselineTargets[0].ServingState.Should().Be(ServiceServingState.Unspecified);
        agent.State.Plan.Stages[0].Targets[0].DeploymentId.Should().Be("dep-2");
        agent.State.Plan.Stages[0].Targets[0].EnabledEndpointIds.Should().ContainSingle("run");
    }

    [Fact]
    public async Task ServiceRolloutManager_ShouldUseEmptyBaselineWhenServingSnapshotMissing()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(
            ServiceKeys.Build(identity),
            "rev-2",
            GAgentServiceTestKit.CreatePreparedStaticArtifact(
                identity,
                "rev-2",
                GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: "run")));
        var servingSetQueryReader = new RecordingServingSetQueryReader();
        var agent = CreateRolloutAgent(
            new InMemoryEventStore(),
            new RecordingDispatchPort(),
            identity,
            new DefaultServiceServingTargetResolver(
                new RecordingDeploymentQueryReader
                {
                    GetResult = new ServiceDeploymentCatalogSnapshot(
                        ServiceKeys.Build(identity),
                        [
                            new ServiceDeploymentSnapshot("dep-2", "rev-2", "actor-2", ServiceDeploymentStatus.Active.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                        ],
                        DateTimeOffset.UtcNow),
                },
                revisionCatalog),
            servingSetQueryReader);
        await agent.ActivateAsync();

        await agent.HandleStartAsync(new StartServiceRolloutCommand
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
        agent.State.BaselineTargets.Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldPersistGenerationAndReplay()
    {
        var eventStore = new InMemoryEventStore();
        var identity = GAgentServiceTestKit.CreateIdentity();
        var actorId = ServiceActorIds.ServingSet(identity);
        var agent = CreateServingSetAgent(eventStore, actorId);
        await agent.ActivateAsync();

        await agent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-a",
            Reason = "initial",
            Targets =
            {
                CreateTarget("dep-a", "r1", "actor-a", 40, "run"),
            },
        });
        await agent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            RolloutId = "rollout-b",
            Reason = "update",
            Targets =
            {
                CreateTarget("dep-b", "r2", "actor-b", 90, "chat"),
                CreateTarget("dep-c", "r3", "actor-c", 10, "run"),
            },
        });

        agent.State.Generation.Should().Be(2);
        agent.State.ActiveRolloutId.Should().Be("rollout-b");
        agent.State.Targets.Select(x => x.DeploymentId).Should().Equal("dep-b", "dep-c");

        await agent.DeactivateAsync();

        var replayed = CreateServingSetAgent(eventStore, actorId);
        await replayed.ActivateAsync();
        replayed.State.Generation.Should().Be(2);
        replayed.State.Targets.Select(x => x.DeploymentId).Should().Equal("dep-b", "dep-c");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldRejectInvalidTargets()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateServingSetAgent(new InMemoryEventStore(), ServiceActorIds.ServingSet(identity));
        await agent.ActivateAsync();

        await FluentActions.Invoking(() => agent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec
                {
                    DeploymentId = "dep-a",
                    RevisionId = "r1",
                    PrimaryActorId = "actor-a",
                    AllocationWeight = -1,
                },
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*allocation_weight must be non-negative*");
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldRejectMissingFieldsAndMismatchedIdentity()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var agent = CreateServingSetAgent(new InMemoryEventStore(), ServiceActorIds.ServingSet(identity));
        await agent.ActivateAsync();

        await agent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                CreateTarget("dep-a", "r1", "actor-a", 100, "run"),
            },
        });

        await FluentActions.Invoking(() => agent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec
                {
                    RevisionId = "r1",
                    PrimaryActorId = "actor-a",
                },
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*deployment_id is required*");

        await FluentActions.Invoking(() => agent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec
                {
                    DeploymentId = "dep-a",
                    PrimaryActorId = "actor-a",
                },
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revision_id is required*");

        await FluentActions.Invoking(() => agent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = identity.Clone(),
            Targets =
            {
                new ServiceServingTargetSpec
                {
                    DeploymentId = "dep-a",
                    RevisionId = "r1",
                },
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*primary_actor_id is required*");

        await FluentActions.Invoking(() => agent.HandleReplaceAsync(new ReplaceServiceServingTargetsCommand
        {
            Identity = GAgentServiceTestKit.CreateIdentity(serviceId: "other").Clone(),
            Targets =
            {
                CreateTarget("dep-b", "r2", "actor-b", 100, "run"),
            },
        }))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*is bound to*");
    }

    private static ServiceRolloutManagerGAgent CreateRolloutAgent(
        InMemoryEventStore eventStore,
        RecordingDispatchPort dispatchPort,
        ServiceIdentity identity,
        IServiceServingTargetResolver? targetResolver = null,
        RecordingServingSetQueryReader? servingSetQueryReader = null)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceRolloutManagerGAgent, ServiceRolloutExecutionState>(
            eventStore,
            ServiceActorIds.Rollout(identity),
            () => new ServiceRolloutManagerGAgent(
                dispatchPort,
                targetResolver ?? new PassthroughServingTargetResolver(),
                servingSetQueryReader ?? new RecordingServingSetQueryReader()));
    }

    private static ServiceServingSetManagerGAgent CreateServingSetAgent(
        InMemoryEventStore eventStore,
        string actorId,
        IServiceServingTargetResolver? targetResolver = null,
        IActorDispatchPort? dispatchPort = null)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceServingSetManagerGAgent, ServiceServingSetState>(
            eventStore,
            actorId,
            () => new ServiceServingSetManagerGAgent(
                dispatchPort ?? GAgentServiceTestKit.NoOpDispatchPort,
                targetResolver ?? new PassthroughServingTargetResolver()));
    }

    private static ReplaceResolvedServiceServingTargetsCommand CreateResolvedActivationCommand(
        ServiceIdentity identity,
        string activationAttemptId,
        string operationId,
        string deploymentId,
        string revisionId,
        string primaryActorId,
        long operationSequence = 1) =>
        new()
        {
            Identity = identity.Clone(),
            Reason = "deployment activation",
            ActivationAttemptId = activationAttemptId,
            OperationId = operationId,
            OperationSequence = operationSequence,
            ReplyActorId = "deployment-manager-1",
            Targets =
            {
                CreateTarget(deploymentId, revisionId, primaryActorId, 100, "chat"),
            },
        };

    private static ServiceRolloutPlanSpec CreateRolloutPlan(string rolloutId, params ServiceRolloutStageSpec[] stages)
    {
        var plan = new ServiceRolloutPlanSpec
        {
            RolloutId = rolloutId,
            DisplayName = rolloutId,
        };
        plan.Stages.Add(stages.Select(x => x.Clone()));
        return plan;
    }

    private static ServiceRolloutStageSpec CreateStage(string stageId, params ServiceServingTargetSpec[] targets)
    {
        var stage = new ServiceRolloutStageSpec
        {
            StageId = stageId,
        };
        stage.Targets.Add(targets.Select(x => x.Clone()));
        return stage;
    }

    private static ServiceServingTargetSpec CreateTarget(
        string deploymentId,
        string revisionId,
        string actorId,
        int allocationWeight,
        params string[] enabledEndpointIds)
    {
        return new ServiceServingTargetSpec
        {
            DeploymentId = deploymentId,
            RevisionId = revisionId,
            PrimaryActorId = actorId,
            AllocationWeight = allocationWeight,
            ServingState = ServiceServingState.Active,
            EnabledEndpointIds = { enabledEndpointIds },
        };
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        private int _attemptCount;

        public List<(string actorId, ReplaceServiceServingTargetsCommand command)> Commands { get; } = [];

        public int? ThrowOnCallIndex { get; init; }

        public Exception? ExceptionToThrow { get; init; }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            var callIndex = ++_attemptCount;
            if (ThrowOnCallIndex == callIndex && ExceptionToThrow != null)
                throw ExceptionToThrow;

            Commands.Add((actorId, envelope.Payload.Unpack<ReplaceServiceServingTargetsCommand>()));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RejectFirstObservationDispatchPort(
        ServiceInvocationCatalogGAgent invocationCatalog) : IActorDispatchPort
    {
        private bool _observationRejected;

        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope.Clone()));
            var admission = DispatchAdmissionFactory.Create(actorId, envelope);
            if (!_observationRejected &&
                envelope.Payload.Is(ObserveServiceInvocationServingCommand.Descriptor))
            {
                _observationRejected = true;
                return admission with { Accepted = false };
            }

            if (envelope.Payload.Is(ObserveServiceInvocationServingCommand.Descriptor))
            {
                await invocationCatalog.HandleServingObservationAsync(
                    envelope.Payload.Unpack<ObserveServiceInvocationServingCommand>());
            }

            return admission;
        }
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

    private sealed class PassthroughServingTargetResolver : IServiceServingTargetResolver
    {
        public Task<IReadOnlyList<ServiceServingTargetSpec>> ResolveTargetsAsync(
            ServiceIdentity identity,
            IEnumerable<ServiceServingTargetSpec> targets,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<ServiceServingTargetSpec>>(targets.Select(CloneTarget).ToList());
        }

        private static ServiceServingTargetSpec CloneTarget(ServiceServingTargetSpec source) =>
            new()
            {
                DeploymentId = source.DeploymentId ?? string.Empty,
                RevisionId = source.RevisionId ?? string.Empty,
                PrimaryActorId = source.PrimaryActorId ?? string.Empty,
                AllocationWeight = source.AllocationWeight,
                ServingState = source.ServingState,
                EnabledEndpointIds = { source.EnabledEndpointIds },
            };
    }
}
