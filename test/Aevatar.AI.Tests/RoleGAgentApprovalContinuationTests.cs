using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Ports;
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

    [Fact]
    public async Task HandleToolApprovalDecision_WhenRunningAuditIsRetryable_ShouldKeepExactContinuationForRetry()
    {
        var auditTrail = new ScriptedRunningAuditTrail(
            AuditTrailAppendStatus.StoreUnavailable,
            AuditTrailAppendStatus.Appended);
        using var provider = BuildServiceProvider(auditTrail);
        var terminalCalls = 0;
        var tool = new DelegateTool("dangerous_tool", argumentsJson =>
        {
            terminalCalls++;
            return $"RESULT:{argumentsJson}";
        });
        var actorId = "role-approval-running-audit-retry";
        var agent = CreateRoleAgent(
            provider,
            actorId,
            toolSources: [new StaticToolSource([tool])]);
        var publisher = new RecordingEventPublisher();
        agent.EventPublisher = publisher;
        await agent.ActivateAsync();
        agent.State.PendingApproval = await CreatePendingApprovalAsync(
            provider,
            tool,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-retry", "call-retry"),
                Caller = new AgentToolCallerContext("scope-retry", "owner-retry", "response-retry"),
            },
            "{\"value\":1}");
        var exactPending = agent.State.PendingApproval.Clone();
        var decision = new ToolApprovalDecisionEvent
        {
            RequestId = exactPending.RequestId,
            ContinuationTurnId = "turn-approval-retry",
            Approved = true,
        };

        await FluentActions.Invoking(() => agent.HandleToolApprovalDecision(decision))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The durable tool audit store is unavailable.");

        terminalCalls.Should().Be(0);
        auditTrail.RunningAttempts.Should().Be(1);
        agent.State.PendingApproval.Should().BeEquivalentTo(exactPending);
        agent.State.Sessions.Should().NotContainKey("turn-approval-retry");
        var store = provider.GetRequiredService<IEventStore>();
        (await store.GetEventsAsync(actorId)).Should()
            .NotContain(x => x.EventData.Is(ClearPendingApprovalEvent.Descriptor));

        await agent.HandleToolApprovalDecision(decision);

        terminalCalls.Should().Be(1);
        auditTrail.RunningAttempts.Should().Be(2);
        agent.State.PendingApproval.Should().BeNull();
        publisher.Published.OfType<ChatRequestEvent>().Should().ContainSingle(x =>
            x.SessionId == "turn-approval-retry" &&
            x.Prompt.Contains("RESULT:{\"value\":1}"));
        (await store.GetEventsAsync(actorId)).Count(x =>
            x.EventData.Is(ClearPendingApprovalEvent.Descriptor)).Should().Be(1);
    }

    [Theory]
    [InlineData(AuditTrailAppendStatus.Duplicate)]
    [InlineData(AuditTrailAppendStatus.Conflict)]
    public async Task HandleToolApprovalDecision_WhenRunningAuditForbidsReplay_ShouldConsumeContinuation(
        AuditTrailAppendStatus runningStatus)
    {
        var auditTrail = new ScriptedRunningAuditTrail(runningStatus);
        using var provider = BuildServiceProvider(auditTrail);
        var terminalCalls = 0;
        var tool = new DelegateTool("dangerous_tool", _ =>
        {
            terminalCalls++;
            return "{\"ok\":true}";
        });
        var agent = CreateRoleAgent(
            provider,
            $"role-approval-running-audit-{runningStatus}",
            toolSources: [new StaticToolSource([tool])]);
        await agent.ActivateAsync();
        agent.State.PendingApproval = await CreatePendingApprovalAsync(
            provider,
            tool,
            AgentToolExecutionContext.Empty with
            {
                Request = new AgentToolRequestIdentity("request-no-replay", "call-no-replay"),
            });
        var decision = new ToolApprovalDecisionEvent
        {
            RequestId = agent.State.PendingApproval.RequestId,
            ContinuationTurnId = $"turn-no-replay-{runningStatus}",
            Approved = true,
        };

        await FluentActions.Invoking(() => agent.HandleToolApprovalDecision(decision))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        agent.State.PendingApproval.Should().BeNull();
        terminalCalls.Should().Be(0);
        auditTrail.RunningAttempts.Should().Be(1);
        await agent.HandleToolApprovalDecision(decision);
        terminalCalls.Should().Be(0);
        auditTrail.RunningAttempts.Should().Be(1);
    }

    private sealed class ScriptedRunningAuditTrail(params AuditTrailAppendStatus[] runningStatuses)
        : IAuditTrailAppender
    {
        private int _runningAttempts;

        public int RunningAttempts => _runningAttempts;

        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            if (!record.Annotations.TryGetValue("execution_phase", out var phase) || phase != "running")
                return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));

            var index = _runningAttempts++;
            var status = index < runningStatuses.Length
                ? runningStatuses[index]
                : runningStatuses[^1];
            var result = status switch
            {
                AuditTrailAppendStatus.Appended => AuditTrailAppendResult.Appended(record.AuditId),
                AuditTrailAppendStatus.Duplicate => AuditTrailAppendResult.Duplicate(record.AuditId),
                AuditTrailAppendStatus.Conflict => AuditTrailAppendResult.Conflict(record.AuditId, "conflict"),
                _ => AuditTrailAppendResult.StoreUnavailable(record.AuditId, "offline"),
            };
            return Task.FromResult(result);
        }
    }
}
