using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
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
    private const string Sentinel = "audit-secret-sentinel";

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
    public void TryBuild_ShouldTruncateOversizedToolDetail()
    {
        // Long but NOT secret-shaped: space-separated short words, so masking is a no-op and the
        // truncation path is what's exercised. (A single 5000-char unbroken token would read as a
        // high-entropy secret and be masked to the marker — covered by the masking tests below.)
        var longArgs = string.Join(' ', Enumerable.Repeat("word", 2000));
        var completed = new RoleChatSessionCompletedEvent { SessionId = "session-2", ContentEmitted = true };
        completed.ToolCalls.Add(new ToolCallEvent { ToolName = "search", CallId = "call-1", ArgumentsJson = longArgs });

        WorkflowArtifactFactBuilder.TryBuild(BuildCommittedEnvelope(completed), "workflow-run", "run-1", out var artifactFact)
            .Should().BeTrue();
        var fact = artifactFact.Should().BeOfType<WorkflowRoleReplyRecordedEvent>().Subject;
        var toolCall = fact.ToolCalls.Should().ContainSingle().Subject;
        toolCall.ArgumentsJson.Length.Should().BeLessThan(longArgs.Length);
        toolCall.ArgumentsJson.Should().NotContain(SecretScrubber.Marker);
        toolCall.ArgumentsJson.Should().EndWith("...");
    }

    [Fact]
    public void TryBuild_ShouldSanitizeToolPayloadsBeforePersistingArtifactFact()
    {
        var completed = new RoleChatSessionCompletedEvent { SessionId = "session-secret", ContentEmitted = true };
        completed.ToolCalls.Add(new ToolCallEvent
        {
            ToolName = "search",
            CallId = "call-1",
            ArgumentsJson = $$"""{"query":"weather","api_key":"{{Sentinel}}","authorization":"Bearer {{Sentinel}}"}""",
        });
        completed.ToolReceipts.Add(new AgentToolReceipt
        {
            CallId = "call-1",
            Status = AgentToolReceiptStatus.Error,
            ResultJson = $$"""{"result":"ok","access_token":"{{Sentinel}}"}""",
            ErrorMessage = $"failed with token={Sentinel}",
        });

        WorkflowArtifactFactBuilder.TryBuild(BuildCommittedEnvelope(completed), "workflow-run", "run-1", out var artifactFact)
            .Should().BeTrue();

        var toolCall = artifactFact.Should().BeOfType<WorkflowRoleReplyRecordedEvent>()
            .Subject.ToolCalls.Should().ContainSingle().Subject;
        toolCall.ArgumentsJson.Should().NotContain(Sentinel);
        toolCall.ResultJson.Should().NotContain(Sentinel);
        toolCall.Error.Should().NotContain(Sentinel);
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

    [Fact]
    public void TryBuild_ShouldMaskSecretsInToolArgumentsAndResults()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiJ9.s5r-Xr7mYh0jRk3Qw9pLzQeVbN2cTfUaOiPkLmNoPqR";
        var completed = new RoleChatSessionCompletedEvent { SessionId = "session-secret", ContentEmitted = true };
        completed.ToolCalls.Add(new ToolCallEvent
        {
            ToolName = "call_api",
            CallId = "call-1",
            ArgumentsJson = "{\"api_key\":\"sk-supersecretkeyvalue000111222\",\"q\":\"weather\"}",
        });
        completed.ToolReceipts.Add(new AgentToolReceipt
        {
            CallId = "call-1",
            Status = AgentToolReceiptStatus.Success,
            ResultJson = $"{{\"token\":\"{jwt}\",\"ok\":true}}",
        });

        WorkflowArtifactFactBuilder.TryBuild(BuildCommittedEnvelope(completed), "workflow-run", "run-1", out var artifactFact)
            .Should().BeTrue();
        var fact = artifactFact.Should().BeOfType<WorkflowRoleReplyRecordedEvent>().Subject;
        var toolCall = fact.ToolCalls.Should().ContainSingle().Subject;

        // Secret VALUE masked, key + non-secret content preserved.
        toolCall.ArgumentsJson.Should().NotContain("sk-supersecretkeyvalue000111222");
        toolCall.ArgumentsJson.Should().Contain(SecretScrubber.Marker);
        toolCall.ArgumentsJson.Should().Contain("\"q\":\"weather\"");

        toolCall.ResultJson.Should().NotContain(jwt);
        toolCall.ResultJson.Should().Contain(SecretScrubber.Marker);
        toolCall.ResultJson.Should().Contain("\"ok\":true");
    }

    [Fact]
    public void TryBuild_ShouldMaskSecretsInContentAndReasoningContent()
    {
        const string jwt = "eyJ0eXAiOiJKV1QifQ.eyJ1c2VyIjoiYm9iIn0.AbCdEfGhIjKlMnOpQrStUvWxYz0123456789AbCdEf";
        var completed = new RoleChatSessionCompletedEvent
        {
            SessionId = "session-content",
            Content = $"Here is your token: {jwt} — use it wisely.",
            ReasoningContent = "{\"access_token\":\"tok-abc123secretreasoningvalue999\"}",
            ContentEmitted = true,
        };

        WorkflowArtifactFactBuilder.TryBuild(BuildCommittedEnvelope(completed), "workflow-run", "run-1", out var artifactFact)
            .Should().BeTrue();
        var fact = artifactFact.Should().BeOfType<WorkflowRoleReplyRecordedEvent>().Subject;

        fact.Content.Should().NotContain(jwt);
        fact.Content.Should().Contain(SecretScrubber.Marker);
        fact.Content.Should().Contain("use it wisely");

        fact.ReasoningContent.Should().NotContain("tok-abc123secretreasoningvalue999");
        fact.ReasoningContent.Should().Contain(SecretScrubber.Marker);
        fact.ReasoningContent.Should().Contain("\"access_token\"");
    }

    [Fact]
    public void TryBuild_ShouldPreserveNonSecretContent()
    {
        var completed = new RoleChatSessionCompletedEvent
        {
            SessionId = "session-plain",
            Content = "The weather in Paris is sunny, 22C.",
            ReasoningContent = "User asked about weather; no tools needed.",
            ContentEmitted = true,
        };

        WorkflowArtifactFactBuilder.TryBuild(BuildCommittedEnvelope(completed), "workflow-run", "run-1", out var artifactFact)
            .Should().BeTrue();
        var fact = artifactFact.Should().BeOfType<WorkflowRoleReplyRecordedEvent>().Subject;

        fact.Content.Should().Be("The weather in Paris is sunny, 22C.");
        fact.Content.Should().NotContain(SecretScrubber.Marker);
        fact.ReasoningContent.Should().Be("User asked about weather; no tools needed.");
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
