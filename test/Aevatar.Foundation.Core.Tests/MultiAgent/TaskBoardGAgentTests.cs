using Aevatar.Foundation.Abstractions.MultiAgent;
using Aevatar.Foundation.Core.MultiAgent;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using TaskStatus = Aevatar.Foundation.Core.MultiAgent.TaskStatus;

namespace Aevatar.Foundation.Core.Tests.MultiAgent;

public class TaskBoardGAgentTests
{
    private static TaskBoardState Apply(TaskBoardState state, IMessage evt) =>
        new TestTaskBoardGAgent().TestTransitionState(state, evt);

    private static TaskBoardState ApplyAll(TaskBoardState state, params IMessage[] events)
    {
        var agent = new TestTaskBoardGAgent();
        foreach (var evt in events)
            state = agent.TestTransitionState(state, evt);
        return state;
    }

    [Fact]
    public void TaskCreated_ShouldAddEntry()
    {
        var state = new TaskBoardState();
        var evt = new TaskCreatedEvent { TaskId = "t1", Content = "do stuff", ActiveForm = "Doing stuff", Sequence = 0 };

        var next = Apply(state, evt);

        next.Tasks.Should().ContainKey("t1");
        next.Tasks["t1"].Status.Should().Be(TaskStatus.Pending);
        next.Tasks["t1"].Content.Should().Be("do stuff");
        next.NextTaskSequence.Should().Be(1);
    }

    [Fact]
    public void TaskCreated_ShouldNotMutateOriginal()
    {
        var state = new TaskBoardState();
        var next = Apply(state, new TaskCreatedEvent { TaskId = "t1", Sequence = 0 });

        state.Tasks.Should().BeEmpty();
        next.Tasks.Should().ContainKey("t1");
    }

    [Fact]
    public void TaskCreated_WithBlockedBy_ShouldPreserveDependencies()
    {
        var state = new TaskBoardState();
        var evt = new TaskCreatedEvent { TaskId = "t2", Sequence = 1, BlockedBy = { "t1" } };

        var next = Apply(state, evt);

        next.Tasks["t2"].BlockedBy.Should().Contain("t1");
    }

    [Fact]
    public void TaskClaimed_ShouldSetInProgress()
    {
        var state = new TaskBoardState();
        state = Apply(state, new TaskCreatedEvent { TaskId = "t1", Sequence = 0 });

        var next = Apply(state, new TaskClaimedEvent { TaskId = "t1", AgentId = "agent-1" });

        next.Tasks["t1"].Status.Should().Be(TaskStatus.InProgress);
        next.Tasks["t1"].OwnerAgentId.Should().Be("agent-1");
        next.AgentCurrentTask.Should().ContainKey("agent-1").WhoseValue.Should().Be("t1");
    }

    [Fact]
    public void TaskCompleted_ShouldSetCompletedAndClearAgent()
    {
        var state = ApplyAll(new TaskBoardState(),
            new TaskCreatedEvent { TaskId = "t1", Sequence = 0 },
            new TaskClaimedEvent { TaskId = "t1", AgentId = "agent-1" });

        var next = Apply(state, new TaskCompletedEvent { TaskId = "t1", AgentId = "agent-1", Output = "result" });

        next.Tasks["t1"].Status.Should().Be(TaskStatus.Completed);
        next.Tasks["t1"].Output.Should().Be("result");
        next.AgentCurrentTask.Should().NotContainKey("agent-1");
    }

    [Fact]
    public void TaskFailed_ShouldSetFailedAndClearAgent()
    {
        var state = ApplyAll(new TaskBoardState(),
            new TaskCreatedEvent { TaskId = "t1", Sequence = 0 },
            new TaskClaimedEvent { TaskId = "t1", AgentId = "agent-1" });

        var next = Apply(state, new TaskFailedEvent { TaskId = "t1", AgentId = "agent-1", Error = "boom" });

        next.Tasks["t1"].Status.Should().Be(TaskStatus.Failed);
        next.Tasks["t1"].Error.Should().Be("boom");
        next.AgentCurrentTask.Should().NotContainKey("agent-1");
    }

    [Fact]
    public void TaskUnblocked_ShouldRemoveDependency()
    {
        var state = new TaskBoardState();
        state = Apply(state, new TaskCreatedEvent { TaskId = "t2", Sequence = 1, BlockedBy = { "t1", "t3" } });

        var next = Apply(state, new TaskUnblockedEvent { TaskId = "t2", CompletedDependency = "t1" });

        next.Tasks["t2"].BlockedBy.Should().ContainSingle().Which.Should().Be("t3");
    }

    [Fact]
    public void CascadingUnblock_ShouldClearAllDependencies()
    {
        var state = ApplyAll(new TaskBoardState(),
            new TaskCreatedEvent { TaskId = "t3", Sequence = 2, BlockedBy = { "t1", "t2" } });

        var next = ApplyAll(state,
            new TaskUnblockedEvent { TaskId = "t3", CompletedDependency = "t1" },
            new TaskUnblockedEvent { TaskId = "t3", CompletedDependency = "t2" });

        next.Tasks["t3"].BlockedBy.Should().BeEmpty();
    }

    [Fact]
    public void SequenceNumbers_ShouldIncrement()
    {
        var state = ApplyAll(new TaskBoardState(),
            new TaskCreatedEvent { TaskId = "t1", Sequence = 0 },
            new TaskCreatedEvent { TaskId = "t2", Sequence = 1 },
            new TaskCreatedEvent { TaskId = "t3", Sequence = 2 });

        state.NextTaskSequence.Should().Be(3);
        state.Tasks["t1"].Sequence.Should().Be(0);
        state.Tasks["t2"].Sequence.Should().Be(1);
        state.Tasks["t3"].Sequence.Should().Be(2);
    }

    [Fact]
    public void AgentCurrentTask_ShouldTrackBusyState()
    {
        var state = ApplyAll(new TaskBoardState(),
            new TaskCreatedEvent { TaskId = "t1", Sequence = 0 },
            new TaskCreatedEvent { TaskId = "t2", Sequence = 1 },
            new TaskClaimedEvent { TaskId = "t1", AgentId = "agent-1" });

        state.AgentCurrentTask.Should().ContainKey("agent-1");

        var next = Apply(state, new TaskCompletedEvent { TaskId = "t1", AgentId = "agent-1" });
        next.AgentCurrentTask.Should().NotContainKey("agent-1");
    }

    [Fact]
    public void TaskCompleted_ShouldInlineUnblockDependents()
    {
        var state = ApplyAll(new TaskBoardState(),
            new TaskCreatedEvent { TaskId = "t1", Sequence = 0 },
            new TaskCreatedEvent { TaskId = "t2", Sequence = 1, BlockedBy = { "t1" } },
            new TaskCreatedEvent { TaskId = "t3", Sequence = 2, BlockedBy = { "t1", "t4" } },
            new TaskClaimedEvent { TaskId = "t1", AgentId = "agent-1" });

        // Completing t1 should auto-unblock t2 fully and remove "t1" from t3's BlockedBy
        var next = Apply(state, new TaskCompletedEvent { TaskId = "t1", AgentId = "agent-1" });

        next.Tasks["t2"].BlockedBy.Should().BeEmpty();
        next.Tasks["t3"].BlockedBy.Should().ContainSingle().Which.Should().Be("t4");
    }

    [Fact]
    public void TaskCreated_ShouldUseDeterministicTimestamp()
    {
        var ts = Timestamp.FromDateTime(new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc));
        var state = new TaskBoardState();
        var next = Apply(state, new TaskCreatedEvent { TaskId = "t1", Sequence = 0, OccurredAt = ts });

        next.Tasks["t1"].CreatedAt.Should().Be(ts);
        next.Tasks["t1"].UpdatedAt.Should().Be(ts);
    }

    [Fact]
    public void UnknownEvent_ShouldReturnCurrentState()
    {
        var state = new TaskBoardState { NextTaskSequence = 5 };
        var next = Apply(state, new AgentMessage { Content = "irrelevant" });

        next.Should().BeSameAs(state);
    }
}

/// <summary>Test subclass to expose protected TransitionState.</summary>
public class TestTaskBoardGAgent : TaskBoardGAgent
{
    public TestTaskBoardGAgent()
    {
        Services = TestRuntimeServices.BuildProvider();
    }

    public TaskBoardState TestTransitionState(TaskBoardState current, IMessage evt) =>
        TransitionState(current, evt);
}
