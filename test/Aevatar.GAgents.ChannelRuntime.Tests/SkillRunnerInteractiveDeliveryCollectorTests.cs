using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class SkillRunnerInteractiveDeliveryCollectorTests
{
    [Fact]
    public async Task Hook_ShouldRecordSuccessfulInteractiveToolDeliverySignal()
    {
        var previous = AgentToolRequestContext.Current;
        try
        {
            AgentToolRequestContext.Current = global::TestAgentToolContexts.FromMetadata(
                new Dictionary<string, string>
                {
                    [LLMRequestMetadataKeys.RequestId] = "request-1",
                    [LLMRequestMetadataKeys.CallId] = "fallback-call",
                });
            var collector = new SkillRunnerInteractiveDeliverySignalCollector();
            var hook = new SkillRunnerInteractiveDeliveryTrackingMiddleware(collector);
            var context = BuildContext(
                "reply_with_interaction",
                """{"success":true,"message_id":"om_interactive","card_id":"card_1"}""");

            await hook.OnToolExecuteEndAsync(context, CancellationToken.None);

            collector.HasSuccessfulInteractiveDelivery.Should().BeTrue();
            var signal = collector.Signals.Should().ContainSingle().Subject;
            signal.DeliveryKind.Should().Be(DeliveryKind.InteractiveCard);
            signal.Status.Should().Be(DeliveryStatus.Succeeded);
            signal.RequestId.Should().Be("request-1");
            signal.SourceEventId.Should().Be("call-1");
            signal.LarkMessageId.Should().Be("om_interactive");
            signal.CardId.Should().Be("card_1");
        }
        finally
        {
            AgentToolRequestContext.Current = previous;
        }
    }

    [Fact]
    public async Task Hook_ShouldIgnoreFailedOrNonInteractiveToolResults()
    {
        var collector = new SkillRunnerInteractiveDeliverySignalCollector();
        var hook = new SkillRunnerInteractiveDeliveryTrackingMiddleware(collector);

        await hook.OnToolExecuteEndAsync(
            BuildContext("lark_messages_send", """{"code":230002}""", """{"message_type":"interactive"}"""),
            CancellationToken.None);
        await hook.OnToolExecuteEndAsync(
            BuildContext("lark_messages_send", """{"code":0,"data":{"message_id":"om_text"}}""", """{"message_type":"text"}"""),
            CancellationToken.None);

        collector.Signals.Should().BeEmpty();
        collector.HasSuccessfulInteractiveDelivery.Should().BeFalse();
    }

    private static AIGAgentExecutionHookContext BuildContext(
        string toolName,
        string? result,
        string argumentsJson = "{}") => new()
    {
        ToolName = toolName,
        ToolCallId = "call-1",
        ToolArguments = argumentsJson,
        ToolResult = result,
    };
}
