using System.Reflection;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkCardOperationSignalTests
{
    [Theory]
    [InlineData(nameof(AgentRunGAgent.HandleReplyOperationStepAsync))]
    [InlineData(nameof(AgentRunGAgent.HandleLarkCardOperationCompletedAsync))]
    [InlineData(nameof(AgentRunGAgent.HandleLarkCardOperationTimeoutFiredAsync))]
    public void AgentRunLarkCardContinuationHandlers_MustOptInToSelfHandling(string handlerName)
    {
        var method = typeof(AgentRunGAgent).GetMethod(
            handlerName,
            BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull();

        var attr = method!.GetCustomAttribute<EventHandlerAttribute>();
        attr.Should().NotBeNull($"{handlerName} must be decorated with [EventHandler].");
        attr!.AllowSelfHandling.Should().BeTrue(
            $"{handlerName} consumes run-owned card operation continuations from the run actor inbox.");
    }

    [Fact]
    public void ConversationGAgent_NoLongerOwnsCardOperationContinuations()
    {
        typeof(ConversationGAgent)
            .GetMethod("HandleLlmReplyCardStreamChunkAsync", BindingFlags.Instance | BindingFlags.Public)
            .Should().BeNull();
        typeof(ConversationGAgent)
            .GetMethod("HandleLarkCardOperationCompletedAsync", BindingFlags.Instance | BindingFlags.Public)
            .Should().BeNull();
        typeof(ConversationGAgent)
            .GetMethod("HandleLarkCardOperationTimeoutFiredAsync", BindingFlags.Instance | BindingFlags.Public)
            .Should().BeNull();
    }
}
