using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed partial class RoleGAgentStateCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleToolApprovalDecision_WhenRequestIsNotPending_ShouldCommitContinuationFailure(
        bool hasDifferentPendingRequest)
    {
        using var provider = BuildServiceProvider();
        var actorId = $"role-approval-stale-{hasDifferentPendingRequest}";
        var agent = CreateRoleAgent(provider, actorId);
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "approval-role",
            RoleName = "approval worker",
        });
        if (hasDifferentPendingRequest)
        {
            agent.State.PendingApproval = new PendingToolApprovalState
            {
                RequestId = "req-current",
                SessionId = "turn-original",
                ScopeId = "scope-a",
                ToolName = "dangerous_tool",
                ToolCallId = "call-1",
                ArgumentsJson = "{}",
            };
        }

        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "req-stale",
            ContinuationTurnId = "turn-stale-decision",
            Approved = true,
        });

        if (hasDifferentPendingRequest)
            agent.State.PendingApproval!.RequestId.Should().Be("req-current");
        else
            agent.State.PendingApproval.Should().BeNull();

        var store = provider.GetRequiredService<IEventStore>();
        var completed = (await store.GetEventsAsync(actorId))
            .Where(x => x.EventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            .Select(x => x.EventData.Unpack<RoleChatSessionCompletedEvent>())
            .Should()
            .ContainSingle()
            .Which;
        completed.SessionId.Should().Be("turn-stale-decision");
        completed.Outcome.Should().Be(RoleChatSessionOutcome.Failed);
        completed.FailureCode.Should().Be("APPROVAL_REQUEST_NOT_PENDING");
        completed.SafeMessage.Should().Be("This approval request is no longer pending.");
        completed.ToString().Should().NotContain("req-current").And.NotContain("req-stale");
    }

    [Fact]
    public async Task HandleToolApprovalDecision_ShouldClearPending_WhenDenied()
    {
        using var provider = BuildServiceProvider();
        var agent = CreateRoleAgent(provider, "role-approval-denied");
        await agent.ActivateAsync();
        await agent.HandleInitializeRoleAgent(new InitializeRoleAgentEvent
        {
            RoleId = "approval-role",
            RoleName = "approval worker",
        });
        agent.State.PendingApproval = new PendingToolApprovalState
        {
            RequestId = "req-1",
            SessionId = "turn-original",
            ScopeId = "scope-a",
            ToolName = "dangerous_tool",
            ToolCallId = "call-1",
            ArgumentsJson = "{}",
        };

        await agent.HandleToolApprovalDecision(new ToolApprovalDecisionEvent
        {
            RequestId = "req-1",
            ContinuationTurnId = "turn-denial",
            Approved = false,
            Reason = "user denied",
        });

        agent.State.PendingApproval.Should().BeNull();
        agent.State.Sessions.Should().NotContainKey("turn-original");
        agent.State.Sessions["turn-denial"].Completed.Should().BeTrue();
        agent.State.Sessions["turn-denial"].FinalContent.Should().Contain("approval_denied: user denied");

        var persistedCompletion = provider.GetRequiredService<IEventStore>() as InMemoryEventStoreForTests;
        persistedCompletion.Should().NotBeNull();
        var completed = (await persistedCompletion!.GetEventsAsync("role-approval-denied"))
            .Single(x => x.EventType.Contains(nameof(RoleChatSessionCompletedEvent), StringComparison.Ordinal))
            .EventData
            .Unpack<RoleChatSessionCompletedEvent>();
        completed.RoleId.Should().Be("approval-role");
        completed.Content.Should().Contain("approval_denied: user denied");
    }
}
