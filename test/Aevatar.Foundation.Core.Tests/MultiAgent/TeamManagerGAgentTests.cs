using Aevatar.Foundation.Abstractions.MultiAgent;
using Aevatar.Foundation.Core.MultiAgent;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Foundation.Core.Tests.MultiAgent;

public class TeamManagerGAgentTests
{
    private static TeamManagerState Apply(TeamManagerState state, IMessage evt)
    {
        // Use reflection to call protected TransitionState — or just replicate the matcher.
        // Since TransitionState is protected, we test the state transition logic directly.
        var agent = new TestTeamManagerGAgent();
        return agent.TestTransitionState(state, evt);
    }

    [Fact]
    public void RegisterMember_ShouldAddToState()
    {
        var state = new TeamManagerState();
        var evt = new MemberRegisteredEvent { AgentId = "a1", AgentName = "worker-1", AgentType = "general" };

        var next = Apply(state, evt);

        next.Members.Should().ContainKey("a1");
        next.Members["a1"].AgentName.Should().Be("worker-1");
        next.Members["a1"].AgentType.Should().Be("general");
        next.Members["a1"].Status.Should().Be("idle");
    }

    [Fact]
    public void RegisterMember_ShouldNotMutateOriginalState()
    {
        var state = new TeamManagerState();
        var evt = new MemberRegisteredEvent { AgentId = "a1", AgentName = "worker-1" };

        var next = Apply(state, evt);

        state.Members.Should().BeEmpty();
        next.Members.Should().ContainKey("a1");
    }

    [Fact]
    public void UnregisterMember_ShouldRemove()
    {
        var state = new TeamManagerState();
        state.Members["a1"] = new TeamMember { AgentId = "a1", AgentName = "worker-1", Status = "idle" };

        var next = Apply(state, new MemberUnregisteredEvent { AgentId = "a1" });

        next.Members.Should().BeEmpty();
    }

    [Fact]
    public void UpdateStatus_ShouldUpdate()
    {
        var state = new TeamManagerState();
        state.Members["a1"] = new TeamMember { AgentId = "a1", Status = "idle" };

        var next = Apply(state, new MemberStatusUpdatedEvent { AgentId = "a1", Status = "busy" });

        next.Members["a1"].Status.Should().Be("busy");
    }

    [Fact]
    public void UpdateStatus_ShouldNotMutateOriginalMember()
    {
        var state = new TeamManagerState();
        state.Members["a1"] = new TeamMember { AgentId = "a1", Status = "idle" };

        var next = Apply(state, new MemberStatusUpdatedEvent { AgentId = "a1", Status = "busy" });

        state.Members["a1"].Status.Should().Be("idle");
        next.Members["a1"].Status.Should().Be("busy");
    }

    [Fact]
    public void UnknownEvent_ShouldReturnCurrentState()
    {
        var state = new TeamManagerState { TeamName = "team-1" };
        var evt = new AgentMessage { Content = "hello" };

        var next = Apply(state, evt);

        next.Should().BeSameAs(state);
    }
}

/// <summary>Test subclass to expose protected TransitionState.</summary>
public class TestTeamManagerGAgent : TeamManagerGAgent
{
    public TestTeamManagerGAgent()
    {
        Services = TestRuntimeServices.BuildProvider();
    }

    public TeamManagerState TestTransitionState(TeamManagerState current, IMessage evt) =>
        TransitionState(current, evt);
}
