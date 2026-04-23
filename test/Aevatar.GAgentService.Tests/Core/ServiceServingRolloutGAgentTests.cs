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
using Aevatar.GAgentService.Infrastructure.Artifacts;
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
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
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
            new DefaultServiceServingTargetResolver(deploymentQueryReader, artifactStore));
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
        var agent = CreateServingSetAgent(new InMemoryEventStore(), ServiceActorIds.ServingSet(identity));
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
    }

    [Fact]
    public async Task ServiceServingSetManager_ShouldRejectMissingResolutionFacts()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var serviceKey = ServiceKeys.Build(identity);
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();

        var missingRevisionAgent = CreateServingSetAgent(
            new InMemoryEventStore(),
            ServiceActorIds.ServingSet(identity),
            new DefaultServiceServingTargetResolver(new RecordingDeploymentQueryReader(), artifactStore));
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
            new DefaultServiceServingTargetResolver(new RecordingDeploymentQueryReader(), artifactStore));
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
                artifactStore));
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
                artifactStore));
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
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
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
                artifactStore));
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
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
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
            artifactStore);
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
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
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
                artifactStore),
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
        var artifactStore = new ConfiguredServiceRevisionArtifactStore();
        await artifactStore.SaveAsync(
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
                artifactStore),
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
        IServiceServingTargetResolver? targetResolver = null)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceServingSetManagerGAgent, ServiceServingSetState>(
            eventStore,
            actorId,
            () => new ServiceServingSetManagerGAgent(targetResolver ?? new PassthroughServingTargetResolver()));
    }

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

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            var callIndex = ++_attemptCount;
            if (ThrowOnCallIndex == callIndex && ExceptionToThrow != null)
                throw ExceptionToThrow;

            Commands.Add((actorId, envelope.Payload.Unpack<ReplaceServiceServingTargetsCommand>()));
            return Task.CompletedTask;
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
