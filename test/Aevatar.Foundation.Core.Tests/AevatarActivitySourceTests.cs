using System.Diagnostics;
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
    public void RecordSensitiveData_ShouldRespectGlobalSwitchAndNullValue()
    {
        using var listener = CreateListener();
        using var chat = AevatarActivitySource.StartChat("model-x");
        chat.Should().NotBeNull();

        const string key = "gen_ai.input";
        var originalFlag = AevatarActivitySource.EnableSensitiveData;
        try
        {
            AevatarActivitySource.EnableSensitiveData = false;
            AevatarActivitySource.RecordSensitiveData(chat, key, "hidden");
            chat!.GetTagItem(key).Should().BeNull();

            AevatarActivitySource.EnableSensitiveData = true;
            AevatarActivitySource.RecordSensitiveData(chat, key, null);
            chat.GetTagItem(key).Should().BeNull();

            AevatarActivitySource.RecordSensitiveData(chat, key, "visible");
            chat.GetTagItem(key).Should().Be("visible");

            var act = () => AevatarActivitySource.RecordSensitiveData(null, key, "ignored");
            act.Should().NotThrow();
        }
        finally
        {
            AevatarActivitySource.EnableSensitiveData = originalFlag;
        }
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
