using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using NSubstitute;
using Xunit;
using NyxIdRelayOptions = Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed partial class AgentRunGAgentTests
{
    [Fact]
    public async Task ExternalCallback_ResumesAfterRelayTokenAge_AndRedeliversProducedReplyAfterRecovery()
    {
        var target = Substitute.For<IActor>();
        target.Id.Returns("conversation-callback");
        var delivered = new List<EventEnvelope>();
        target.When(actor => actor.HandleEventAsync(Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>()))
            .Do(call => delivered.Add(call.Arg<EventEnvelope>()));
        var actorRuntime = new DispatchingActorRuntime(("conversation-callback", target));
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new DispatchingEventPublisher(actorRuntime) { FailNextSend = true };
        var generator = new RecordingReplyGenerator(() => false) { ReplyText = "resumed business result" };
        var first = CreateRunAgent(actorRuntime, generator, new AsyncLocalInteractiveReplyCollector(),
            new NyxIdRelayOptions { StreamingRepliesEnabled = false },
            eventPublisher: publisher, callbackScheduler: scheduler);

        await first.HandleStartAsync(new NeedsLlmReplyEvent
        {
            RunId = "resume-run-stable",
            CorrelationId = "resume-activity-stable",
            TargetActorId = "conversation-callback",
            RegistrationId = "registration-original",
            ExternalCallbackId = "callback-operation",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(),
            Activity = BuildRelayActivity(),
        });

        first.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        first.State.ExternalCallbackId.Should().Be("callback-operation");
        generator.CallCount.Should().Be(1);
        delivered.Should().BeEmpty();
        var retry = scheduler.Timeouts.Should().ContainSingle(timeout =>
                timeout.TriggerEnvelope.Payload.Is(AgentRunOutputDispatchRetryRequested.Descriptor))
            .Subject.TriggerEnvelope.Payload.Unpack<AgentRunOutputDispatchRetryRequested>();
        retry.RequiresRuntimeReplyToken.Should().BeFalse();

        var recoveredExecutor = new PausedReplyGenerationExecutor();
        var recovered = CreateRunAgentWithExecutor(actorRuntime, recoveredExecutor, new NyxIdRelayOptions());
        SetState(recovered, RoundTrip(first.State));
        await recovered.HandleOutputDispatchRetryAsync(retry);
        await recovered.HandleOutputDispatchRetryAsync(retry);

        recovered.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        recoveredExecutor.LlmStepExecutions.Should().BeEmpty();
        var ready = delivered.Should().ContainSingle().Subject.Payload.Unpack<LlmReplyReadyEvent>();
        ready.RunId.Should().Be("resume-run-stable");
        ready.CorrelationId.Should().Be("resume-activity-stable");
        ready.Outbound.Text.Should().Be("resumed business result");
        ready.ReplyToken.Should().BeEmpty();
        ready.UseSourceActivityDeliveryContext.Should().BeFalse(
            "the original complete route remains in ConversationGAgent's pending callback request");
    }
}
