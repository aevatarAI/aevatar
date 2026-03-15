using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ServicePhase3GAgentTests
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
                CreateStage("stage-a", CreateTarget("dep-a", "r1", "actor-a", 100, "run"))),
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

    private static ServiceRolloutManagerGAgent CreateRolloutAgent(
        InMemoryEventStore eventStore,
        RecordingDispatchPort dispatchPort,
        ServiceIdentity identity)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceRolloutManagerGAgent, ServiceRolloutExecutionState>(
            eventStore,
            ServiceActorIds.Rollout(identity),
            () => new ServiceRolloutManagerGAgent(dispatchPort));
    }

    private static ServiceServingSetManagerGAgent CreateServingSetAgent(InMemoryEventStore eventStore, string actorId)
    {
        return GAgentServiceTestKit.CreateStatefulAgent<ServiceServingSetManagerGAgent, ServiceServingSetState>(
            eventStore,
            actorId,
            () => new ServiceServingSetManagerGAgent());
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
        public List<(string actorId, ReplaceServiceServingTargetsCommand command)> Commands { get; } = [];

        public int? ThrowOnCallIndex { get; init; }

        public Exception? ExceptionToThrow { get; init; }

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            var callIndex = Commands.Count + 1;
            if (ThrowOnCallIndex == callIndex && ExceptionToThrow != null)
                throw ExceptionToThrow;

            Commands.Add((actorId, envelope.Payload.Unpack<ReplaceServiceServingTargetsCommand>()));
            return Task.CompletedTask;
        }
    }
}
