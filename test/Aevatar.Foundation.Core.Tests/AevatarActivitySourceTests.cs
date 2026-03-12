using System.Diagnostics;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Observability;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public sealed class AevatarActivitySourceTests
{
    [Fact]
    public void StartHandleEvent_AndStartInvokeAgent_ShouldSetCoreTags()
    {
        using var listener = CreateListener();

        using var handleEvent = AevatarActivitySource.StartHandleEvent("agent-1", "evt-1");
        handleEvent.Should().NotBeNull();
        handleEvent!.GetTagItem("aevatar.agent.id").Should().Be("agent-1");
        handleEvent.GetTagItem("aevatar.event.id").Should().Be("evt-1");

        using var invoke = AevatarActivitySource.StartInvokeAgent("agent-2", "assistant", "test-system");
        invoke.Should().NotBeNull();
        invoke!.GetTagItem("gen_ai.operation.name").Should().Be("invoke_agent");
        invoke.GetTagItem("gen_ai.agent.id").Should().Be("agent-2");
        invoke.GetTagItem("gen_ai.agent.name").Should().Be("assistant");
        invoke.GetTagItem("gen_ai.system").Should().Be("test-system");
    }

    [Fact]
    public void StartChat_AndStartExecuteTool_ShouldHandleOptionalTags()
    {
        using var listener = CreateListener();

        using var chat = AevatarActivitySource.StartChat();
        chat.Should().NotBeNull();
        chat!.DisplayName.Should().Contain("unknown");
        chat.GetTagItem("gen_ai.operation.name").Should().Be("chat");
        chat.GetTagItem("gen_ai.request.model").Should().BeNull();
        chat.GetTagItem("gen_ai.system").Should().BeNull();

        using var executeTool = AevatarActivitySource.StartExecuteTool("search");
        executeTool.Should().NotBeNull();
        executeTool!.GetTagItem("gen_ai.operation.name").Should().Be("execute_tool");
        executeTool.GetTagItem("gen_ai.tool.name").Should().Be("search");
        executeTool.GetTagItem("gen_ai.tool.call_id").Should().BeNull();
    }

    [Fact]
    public void RecordTokenUsage_ShouldSetAvailableTokenTags()
    {
        using var listener = CreateListener();
        using var chat = AevatarActivitySource.StartChat("gpt-4o", "openai");
        chat.Should().NotBeNull();

        AevatarActivitySource.RecordTokenUsage(chat, inputTokens: 12, outputTokens: null);
        chat!.GetTagItem("gen_ai.usage.input_tokens").Should().Be(12);
        chat.GetTagItem("gen_ai.usage.output_tokens").Should().BeNull();

        AevatarActivitySource.RecordTokenUsage(chat, inputTokens: null, outputTokens: 34);
        chat.GetTagItem("gen_ai.usage.output_tokens").Should().Be(34);

        var act = () => AevatarActivitySource.RecordTokenUsage(null, 1, 1);
        act.Should().NotThrow();
    }

    [Fact]
    public void StartHandleEvent_ShouldUseEventTypeFirstDisplayName()
    {
        using var listener = CreateListener();
        using var activity = AevatarActivitySource.StartHandleEvent(
            "Workflow:run-1:assistant",
            "evt-1",
            "type.googleapis.com/aevatar.ai.TextMessageContentEvent");

        activity.Should().NotBeNull();
        activity!.DisplayName.Should().Be("HandleEvent:TextMessageContentEvent");
        activity.GetTagItem("aevatar.event.type").Should().Be("type.googleapis.com/aevatar.ai.TextMessageContentEvent");
    }

    [Fact]
    public void StartHandleEvent_WithEnvelope_ShouldSetDirectionAndPublisherTags()
    {
        using var listener = CreateListener();
        var envelope = new EventEnvelope
        {
            Id = "evt-2",
            Route = EnvelopeRouteSemantics.CreateBroadcast("publisher-1", BroadcastDirection.Both),
        };

        using var activity = AevatarActivitySource.StartHandleEvent("agent-1", envelope);
        activity.Should().NotBeNull();
        activity!.GetTagItem("aevatar.event.direction").Should().Be(BroadcastDirection.Both.ToString());
        activity.GetTagItem("aevatar.event.publisher").Should().Be("publisher-1");
    }

    private static ActivityListener CreateListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aevatar.Agents",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

}
