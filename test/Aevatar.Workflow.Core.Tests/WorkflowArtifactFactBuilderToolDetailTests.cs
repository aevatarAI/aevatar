using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.Workflow.Core.Tests;

// 06-19-workflow-run-observatory (C2 / O1): the run-artifact fact builder enriches the workflow run
// timeline with tool detail sourced from the committed RoleChatSessionCompletedEvent (the only committed
// fact carrying both tool_calls arguments and tool_receipts results). Behavior-focused, separate from the
// generic coverage bucket.
public sealed class WorkflowArtifactFactBuilderToolDetailTests
{
    [Fact]
    public void TryBuild_ShouldJoinToolCallsAndReceiptsByCallId_FromCommittedRoleChatSession()
    {
        var completed = new RoleChatSessionCompletedEvent
        {
            RoleId = "planner",
            SessionId = "session-1",
            Content = "done",
            ReasoningContent = "thinking",
            ContentEmitted = true,
        };
        completed.ToolCalls.Add(new ToolCallEvent { ToolName = "search", CallId = "call-1", ArgumentsJson = "{\"q\":\"x\"}" });
        completed.ToolCalls.Add(new ToolCallEvent { ToolName = "fetch", CallId = "call-2", ArgumentsJson = "{\"url\":\"y\"}" });
        completed.ToolReceipts.Add(new AgentToolReceipt
        {
            CallId = "call-1",
            Status = AgentToolReceiptStatus.Success,
            ResultJson = "{\"hits\":3}",
        });
        completed.ToolReceipts.Add(new AgentToolReceipt
        {
            CallId = "call-2",
            Status = AgentToolReceiptStatus.Error,
            ErrorMessage = "boom",
        });

        var ok = WorkflowArtifactFactBuilder.TryBuild(
            BuildCommittedEnvelope(completed),
            "workflow-run",
            "run-1",
            out var artifactFact);

        ok.Should().BeTrue();
        var fact = artifactFact.Should().BeOfType<WorkflowRoleReplyRecordedEvent>().Subject;
        fact.RunId.Should().Be("run-1");
        fact.RoleActorId.Should().Be("workflow-run:role_a");
        fact.RoleId.Should().Be("planner");
        fact.Content.Should().Be("done");
        fact.ContentEmitted.Should().BeTrue();
        fact.ToolCalls.Should().HaveCount(2);

        var first = fact.ToolCalls.Single(x => x.CallId == "call-1");
        first.ToolName.Should().Be("search");
        first.ArgumentsJson.Should().Be("{\"q\":\"x\"}");
        first.ResultJson.Should().Be("{\"hits\":3}");
        first.Success.Should().BeTrue();
        first.Error.Should().BeEmpty();

        var second = fact.ToolCalls.Single(x => x.CallId == "call-2");
        second.ToolName.Should().Be("fetch");
        second.Success.Should().BeFalse();
        second.Error.Should().Be("boom");
        second.ResultJson.Should().BeEmpty();
    }

    [Fact]
    public void TryBuild_ShouldRedactOversizedToolDetail()
    {
        var longArgs = new string('a', 5000);
        var completed = new RoleChatSessionCompletedEvent { SessionId = "session-2", ContentEmitted = true };
        completed.ToolCalls.Add(new ToolCallEvent { ToolName = "search", CallId = "call-1", ArgumentsJson = longArgs });

        WorkflowArtifactFactBuilder.TryBuild(BuildCommittedEnvelope(completed), "workflow-run", "run-1", out var artifactFact)
            .Should().BeTrue();
        var fact = artifactFact.Should().BeOfType<WorkflowRoleReplyRecordedEvent>().Subject;
        var toolCall = fact.ToolCalls.Should().ContainSingle().Subject;
        toolCall.ArgumentsJson.Length.Should().BeLessThan(longArgs.Length);
        toolCall.ArgumentsJson.Should().EndWith("...");
    }

    [Fact]
    public void TryBuild_ShouldYieldNoToolCalls_WhenRoleChatSessionHasNone()
    {
        var completed = new RoleChatSessionCompletedEvent
        {
            SessionId = "session-3",
            Content = "plain reply",
            ContentEmitted = true,
        };

        WorkflowArtifactFactBuilder.TryBuild(BuildCommittedEnvelope(completed), "workflow-run", "run-1", out var artifactFact)
            .Should().BeTrue();
        artifactFact.Should().BeOfType<WorkflowRoleReplyRecordedEvent>()
            .Which.ToolCalls.Should().BeEmpty();
    }

    private static EventEnvelope BuildCommittedEnvelope(RoleChatSessionCompletedEvent completed) =>
        new()
        {
            Id = "env-chat-session",
            Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-run:role_a"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-chat-session",
                    EventData = Any.Pack(completed),
                },
            }),
        };
}
