using System.Reflection;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Lark.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentRunLarkCardDeliveryTests
{
    private const string ConversationActorId = "conversation-1";

    [Fact]
    public async Task CardChunkEnvelope_StartsCreateOnRunActorState()
    {
        var runner = new RecordingCardRunner();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("hello")));

        agent.State.LarkCardDelivery.Should().NotBeNull();
        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Creating);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Create);
        agent.State.LarkCardDelivery.InFlightSequence.Should().Be(1);
        agent.State.LarkCardDelivery.OperationGeneration.Should().Be(1);
        agent.State.LarkCardDelivery.PendingAccumulatedText.Should().Be("hello");
        publisher.Sent.Should().ContainSingle(e => e.Event is ReplyOperationStepEvent);
        publisher.Sent.OfType<(string TargetActorId, IMessage Event)>()
            .Where(e => e.Event is ReplyOperationStepEvent)
            .Select(e => e.TargetActorId)
            .Should().ContainSingle(agent.Id);
        runner.CreateCalls.Should().BeEmpty("publisher dispatch records the self-message before IO runs");
    }

    [Fact]
    public async Task CreateAndStreamCompletions_MaintainMonotonicCardSequenceAndLatestCoalescedText()
    {
        var runner = new RecordingCardRunner();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("first")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Streaming);
        agent.State.LarkCardDelivery.CardId.Should().Be("card-ok");
        agent.State.LarkCardDelivery.CardMessageId.Should().Be("om-card-ok");
        agent.State.LarkCardDelivery.Sequence.Should().Be(1);
        agent.State.LarkCardDelivery.LastFlushedText.Should().Be("first");

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("second")));
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("third")));

        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Stream);
        agent.State.LarkCardDelivery.InFlightSequence.Should().Be(2);
        agent.State.LarkCardDelivery.PendingAccumulatedText.Should().Be("third");
        await DispatchSelfEventCountAsync(agent, publisher, 2);

        agent.State.LarkCardDelivery.LastFlushedText.Should().Be("second");
        agent.State.LarkCardDelivery.Sequence.Should().Be(2);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Stream);
        agent.State.LarkCardDelivery.InFlightSequence.Should().Be(3);
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.LastFlushedText.Should().Be("third");
        agent.State.LarkCardDelivery.Sequence.Should().Be(3);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Unspecified);
        agent.State.LarkCardDelivery.PendingAccumulatedText.Should().BeEmpty();
        runner.StreamCalls.Select(call => call.Sequence).Should().Equal(2, 3);
    }

    [Fact]
    public async Task FinalizeAfterVisibleCard_DispatchesCompletionToConversationAndHandsOffRun()
    {
        var runner = new RecordingCardRunner();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var agent = CreateAgent(runner, publisher: publisher, scheduler: scheduler);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("partial")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        var ready = CreateReady("final", activityAccessToken: "ready-user-access-token");
        await agent.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            Attempt = 1,
            StepIndex = 2,
            Request = ready,
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = "final",
                Content = "final",
                FinishReason = "stop",
                HasStreamedTextContent = true,
            },
        });

        agent.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Finalize);
        agent.State.LarkCardDelivery.InFlightSequence.Should().Be(2);
        var finalizeStep = publisher.Sent
            .Select(e => e.Event)
            .OfType<ReplyOperationStepEvent>()
            .Last(step => step.LarkCard.Operation == LarkCardOperationPhase.Finalize);
        finalizeStep.LarkCard.Activity.TransportExtras.NyxUserAccessToken.Should()
            .Be("ready-user-access-token");

        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Completed);
        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runner.FinalizeCalls.Should().ContainSingle();
        runner.FinalizeCalls[0].RuntimeUserAccessToken.Should().Be("ready-user-access-token");
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.CorrelationId.Should().Be("corr-card");
        completed.RunId.Should().Be("run-1");
        completed.CommandId.Should().Be("llm:corr-card");
        completed.CardMessageId.Should().Be("om-card-ok");
        completed.OutboundText.Should().Be("final");
        completed.DeliveryFailure.Should().BeNull();
        var delivery = agent.State.RecentDeliveries.Should().ContainSingle().Subject;
        delivery.DeliveryKind.Should().Be(DeliveryKind.StreamingCard);
        delivery.Status.Should().Be(DeliveryStatus.Succeeded);
        delivery.LarkMessageId.Should().Be("om-card-ok");
        delivery.RequestId.Should().Be("llm:corr-card");
        agent.State.LastSuccessfulDelivery.Should().NotBeNull();
        agent.State.LastSuccessfulDelivery!.LarkMessageId.Should().Be("om-card-ok");
        scheduler.Timeouts.Should().Contain(timeout => timeout.CallbackId.StartsWith(
            "agent-run-terminal-cleanup:run-1",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletionSendFailure_PersistsOutbox_AndRetryDeliversConversationFactWithoutReplyToken()
    {
        var runner = new RecordingCardRunner();
        var scheduler = new RecordingCallbackScheduler();
        var conversationStore = new InMemoryEventStore();
        var conversationPublisher = new SelfHandlingConversationPublisher();
        var conversation = await CreateConversationAgentAsync(
            ConversationActorId,
            conversationStore,
            conversationPublisher);
        SeedReplyLifecycle(conversation, "corr-card");
        var publisher = new RecordingEventPublisher
        {
            ConversationTarget = conversation,
            FailNextConversationCompletion = true,
        };
        var agent = CreateAgent(runner, publisher: publisher, scheduler: scheduler);

        await StartVisibleCardAsync(agent, publisher, "partial");
        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("final"));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        agent.State.PendingCardDeliveryCompletion.Should().NotBeNull();
        agent.State.PendingCardDeliveryCompletion.TargetActorId.Should().Be(ConversationActorId);
        agent.State.PendingCardDeliveryCompletion.CommandId.Should().Be("llm:corr-card");
        agent.State.PendingCardDeliveryCompletion.CardMessageId.Should().Be("om-card-ok");
        agent.State.PendingCardDeliveryCompletion.OutboundText.Should().Be("final");
        agent.State.PendingCardDeliveryCompletion.Outcome.Should()
            .Be(AgentRunLarkCardDeliveryCompletionOutcome.Completed);
        scheduler.Timeouts
            .Where(timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunOutputDispatchRetryRequested.Descriptor))
            .Select(timeout => timeout.TriggerEnvelope.Payload.Unpack<AgentRunOutputDispatchRetryRequested>())
            .Should().ContainSingle(retry => !retry.RequiresRuntimeReplyToken);

        var retry = scheduler.Timeouts
            .Where(timeout => timeout.TriggerEnvelope.Payload.Is(AgentRunOutputDispatchRetryRequested.Descriptor))
            .Select(timeout => timeout.TriggerEnvelope.Payload.Unpack<AgentRunOutputDispatchRetryRequested>())
            .Single();
        retry.RequiresRuntimeReplyToken.Should().BeFalse();
        await agent.HandleEventAsync(Envelope(agent.Id, retry));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.PendingCardDeliveryCompletion.Should().BeNull();
        var conversationEvents = await conversationStore.GetEventsAsync(ConversationActorId);
        conversationEvents.Should().ContainSingle(e => e.EventData.Is(LlmReplyDeliveredEvent.Descriptor));
        conversationEvents.Should().ContainSingle(e => e.EventData.Is(ConversationTurnCompletedEvent.Descriptor));
        var completed = conversationEvents
            .Single(e => e.EventData.Is(ConversationTurnCompletedEvent.Descriptor))
            .EventData.Unpack<ConversationTurnCompletedEvent>();
        completed.Outbound.Text.Should().Be("final");
        completed.SentActivityId.Should().Be("lark-card-stream:om-card-ok");
    }

    [Fact]
    public async Task PendingCompletionRetry_WithFailurePayload_PersistsConversationFailureAndIsIdempotent()
    {
        var conversationStore = new InMemoryEventStore();
        var conversationPublisher = new SelfHandlingConversationPublisher();
        var conversation = await CreateConversationAgentAsync(
            ConversationActorId,
            conversationStore,
            conversationPublisher);
        SeedReplyLifecycle(conversation, "corr-card");
        var publisher = new RecordingEventPublisher
        {
            ConversationTarget = conversation,
        };
        var agent = CreateAgent(new RecordingCardRunner(), publisher: publisher);
        SetState(agent, BuildPendingCompletionState(deliveryFailure: new LlmReplyDeliveryFailedEvent
        {
            ErrorCode = "card_finalize_failed",
            ErrorMessage = "finalize rejected",
        }));

        var retry = new AgentRunOutputDispatchRetryRequested
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = ConversationActorId,
            Attempt = 1,
            Generation = 1,
            RequiresRuntimeReplyToken = true,
        };
        await agent.HandleEventAsync(Envelope(agent.Id, retry));
        await agent.HandleEventAsync(Envelope(agent.Id, retry.Clone()));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.PendingCardDeliveryCompletion.Should().BeNull();
        var conversationEvents = await conversationStore.GetEventsAsync(ConversationActorId);
        conversationEvents.Should().ContainSingle(e => e.EventData.Is(LlmReplyDeliveryFailedEvent.Descriptor));
        conversationEvents.Count(e => e.EventData.Is(DeliveryProducedEvent.Descriptor)).Should().Be(1);
        var delivery = conversationEvents
            .Single(e => e.EventData.Is(DeliveryProducedEvent.Descriptor))
            .EventData.Unpack<DeliveryProducedEvent>();
        delivery.DeliveryKind.Should().Be(DeliveryKind.StreamingCard);
        delivery.Status.Should().Be(DeliveryStatus.FailedPostSend);
        delivery.RequestId.Should().Be("llm:corr-card");
        delivery.SourceEventId.Should().Be("corr-card");
        delivery.LarkMessageId.Should().Be("lark-card-stream:om-card-ok");
        delivery.CardId.Should().BeEmpty();
        delivery.Target.Channel.Value.Should().Be("lark");
        delivery.Target.ConversationKey.Should().Be("lark:group:oc-group-1");
        delivery.Target.Platform.Should().Be("lark");
        delivery.Target.ReceiveId.Should().Be("oc-group-1");
        delivery.Target.ReceiveIdType.Should().Be("chat_id");
        delivery.Target.ConversationId.Should().Be("oc-group-1");
        delivery.Target.ReplyMessageId.Should().Be("relay-msg-1");
        conversationEvents.Should().ContainSingle(e => e.EventData.Is(ConversationTurnCompletedEvent.Descriptor));
        var failed = conversationEvents
            .Single(e => e.EventData.Is(LlmReplyDeliveryFailedEvent.Descriptor))
            .EventData.Unpack<LlmReplyDeliveryFailedEvent>();
        failed.ErrorCode.Should().Be("card_finalize_failed");
        failed.RunId.Should().Be("run-1");
    }

    [Fact]
    public async Task DuplicateTerminalPhase_WithPendingCompletion_ResendsFromOutboxAfterReactivation()
    {
        var conversationStore = new InMemoryEventStore();
        var conversationPublisher = new SelfHandlingConversationPublisher();
        var conversation = await CreateConversationAgentAsync(
            ConversationActorId,
            conversationStore,
            conversationPublisher);
        SeedReplyLifecycle(conversation, "corr-card");
        var publisher = new RecordingEventPublisher
        {
            ConversationTarget = conversation,
        };
        var agent = CreateAgent(new RecordingCardRunner(), publisher: publisher);
        SetState(agent, BuildPendingCompletionState());

        await agent.HandleStartAsync(CreateReady("final"));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.PendingCardDeliveryCompletion.Should().BeNull();
        var conversationEvents = await conversationStore.GetEventsAsync(ConversationActorId);
        conversationEvents.Should().ContainSingle(e => e.EventData.Is(LlmReplyDeliveredEvent.Descriptor));
        conversationEvents.Should().ContainSingle(e => e.EventData.Is(ConversationTurnCompletedEvent.Descriptor));
    }

    [Fact]
    public async Task FinalizeFailure_PreservesLastVisibleTextAndSendsDeliveryFailure()
    {
        var runner = new RecordingCardRunner
        {
            FinalizeResult = ConversationCardFinalizeResult.Failed(
                "card_close_failed",
                "close rejected",
                finalTextWritten: false),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("partial")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        await agent.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            Attempt = 1,
            StepIndex = 2,
            Request = CreateReady("final"),
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = "final",
                Content = "final",
                FinishReason = "stop",
                HasStreamedTextContent = true,
            },
        });
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Terminated);
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.OutboundText.Should().Be("partial");
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("card_close_failed");
    }

    [Fact]
    public async Task CreateFailure_SendsOrdinaryLarkStatusMessage_WithoutConversationTextChunk()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher, larkOutboundDispatcher: outbound);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("fallback text")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.CreationFailed);
        agent.State.LarkCardTextFallback.Should().NotBeNull();
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.StatusSent);
        agent.State.LarkCardTextFallback.StatusMessageId.Should().Be("om-status-1");
        agent.State.LarkCardTextFallback.LastDeliveredText.Should().Be("Processing your request. Please wait...");
        outbound.SendRequests.Should().ContainSingle();
        outbound.SendRequests[0].NyxProxyBearerToken.Should().Be("runtime-user-access-token");
        ExtractLarkText(outbound.SendRequests[0].ContentJson).Should().Be("Processing your request. Please wait...");
        outbound.SendRequests[0].PrimaryTarget.ReceiveId.Should().Be("oc-group-1");
        outbound.SendRequests[0].PrimaryTarget.ReceiveIdType.Should().Be("chat_id");
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFailure_StatusSendTimeout_PersistsFailedFallbackWithoutBlockingActorTurn()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-30T10:00:00Z"));
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher
        {
            NeverCompleteAfterRequests = 0,
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            timeProvider: timeProvider);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("fallback text")));
        var dispatch = DispatchPendingSelfEventsAsync(agent, publisher);
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(1));

        outbound.CancellationTokens.Should().ContainSingle(token => token.CanBeCanceled);
        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.CreationFailed);
        agent.State.LarkCardTextFallback.Should().NotBeNull();
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Failed);
        agent.State.LarkCardTextFallback.LastErrorCode.Should().Be("status_send_timeout");
        agent.State.LarkCardTextFallback.StatusMessageId.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFailure_StatusSendTimeout_DoesNotDependOnDispatcherObservingCancellation()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-30T10:00:00Z"));
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher
        {
            NeverCompleteAfterRequests = 0,
            NeverCompleteIgnoresCancellation = true,
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            timeProvider: timeProvider);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("fallback text")));
        var dispatch = DispatchPendingSelfEventsAsync(agent, publisher);
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(1));

        outbound.CancellationTokens.Should().ContainSingle(token => token.CanBeCanceled);
        agent.State.LarkCardTextFallback.Should().NotBeNull();
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Failed);
        agent.State.LarkCardTextFallback.LastErrorCode.Should().Be("status_send_timeout");
    }

    [Fact]
    public async Task CreateFailure_DropsInterimChunksAndEditsStatusToFinalText()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor,
            relayOptions: new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingCardKitEnabled = true,
                StreamingRepliesEnabled = true,
                StreamingMaxInterimChunks = 3,
                StreamingFlushIntervalMs = 0,
            });

        // First chunk drives create -> create-fail -> the create-failure fallback dispatches
        // interim #1 from the original create chunk.
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 1")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.CreationFailed);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 2")));
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 3")));

        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();
        outbound.SendRequests.Should().ContainSingle("only the initial status text is sent before final");
        editor.EditRequests.Should().BeEmpty("intermediate chunks must not edit Lark");

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("the complete final reply", isFinal: true)));

        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();
        editor.EditRequests.Should().ContainSingle();
        editor.EditRequests[0].NyxProxyBearerToken.Should().Be("runtime-user-access-token");
        editor.EditRequests[0].MessageId.Should().Be("om-status-1");
        editor.EditRequests[0].Text.Should().Be("the complete final reply");
        outbound.SendRequests.Should().ContainSingle("short final text should edit the first status message without extra sends");
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Completed);
        agent.State.LarkCardTextFallback.LastDeliveredText.Should().Be("the complete final reply");
        agent.State.LarkCardTextFallback.LastErrorCode.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFailure_FinalStreamChunkOnlyCompletesTextFallbackUntilAuthoritativeReplyArrives()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("transport final", isFinal: true)));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyGenerationRequested);
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Completed);
        agent.State.LarkCardTextFallback.LastDeliveredText.Should().Be("transport final");
        publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Should().BeEmpty();

        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("authoritative final"));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Completed);
        agent.State.LarkCardTextFallback.LastDeliveredText.Should().Be("authoritative final");
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.OutboundText.Should().Be("authoritative final");
        completed.AppendedHistory.Should().ContainSingle(entry =>
            entry.Role == "assistant" &&
            entry.Content == "authoritative final");
        outbound.SendRequests.Should().ContainSingle("authoritative completion must not send another Lark text message");
        editor.EditRequests.Should().HaveCount(2);
        editor.EditRequests[0].Text.Should().Be("transport final");
        editor.EditRequests[1].Text.Should().Be("authoritative final");
        editor.EditRequests[1].MessageId.Should().Be("om-status-1");
    }

    [Fact]
    public async Task CreateFailure_AuthoritativeReplyMatchingCompletedFallback_DoesNotEditAgain()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("authoritative final", isFinal: true)));
        editor.EditRequests.Should().ContainSingle(request => request.Text == "authoritative final");

        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("authoritative final"));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Completed);
        agent.State.LarkCardTextFallback.LastDeliveredText.Should().Be("authoritative final");
        editor.EditRequests.Should().ContainSingle("matching authoritative text is already visible in Lark");
        outbound.SendRequests.Should().ContainSingle("matching authoritative completion must not send another Lark text message");
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.OutboundText.Should().Be("authoritative final");
        completed.DeliveryFailure.Should().BeNull();
    }

    [Fact]
    public async Task CreateFailure_AuthoritativeReplyUpdateFailureAfterCompletedFallback_PersistsTypedDeliveryFailure()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("transport final", isFinal: true)));
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Completed);
        agent.State.LarkCardTextFallback.LastDeliveredText.Should().Be("transport final");
        editor.EditRequests.Should().ContainSingle(request => request.Text == "transport final");

        editor.Results.Enqueue(LarkTextMessageEditResult.Failed(99992361, "authoritative edit rejected"));
        outbound.Results.Enqueue(LarkSendNewMessageResult.Failed(
            new LarkReceiveTarget("oc-group-1", "chat_id", FellBackToPrefixInference: false),
            usedFallback: false,
            larkCode: 99992361,
            detail: "authoritative fresh send rejected"));

        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("authoritative final"));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        editor.EditRequests.Should().HaveCount(2);
        editor.EditRequests[1].Text.Should().Be("authoritative final");
        outbound.SendRequests.Should().HaveCount(2);
        ExtractLarkText(outbound.SendRequests[1].ContentJson).Should().Be("authoritative final");
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Failed);
        agent.State.LarkCardTextFallback.LastErrorCode.Should().Be("final_send_failed_after_edit_failed:99992361");
        agent.State.LarkCardTextFallback.LastErrorMessage.Should().Contain("status_edit_failed=authoritative edit rejected");
        agent.State.LarkCardTextFallback.LastErrorMessage.Should().Contain("send=authoritative fresh send rejected");
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.OutboundText.Should().Be("transport final");
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("final_send_failed_after_edit_failed:99992361");
    }

    [Fact]
    public async Task CreateFailure_LongFinalText_EditsFirstSegmentAndSendsRemainingSegments()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor,
            relayOptions: new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingCardKitEnabled = true,
                StreamingRepliesEnabled = true,
                StreamingMaxInterimChunks = 2,
                StreamingFlushIntervalMs = 0,
            });

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 1")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.CreationFailed);

        var longText = new string('A', LarkCardTextFallbackPolicy.SegmentThreshold + 100);
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk(longText, isFinal: true)));

        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();
        editor.EditRequests.Should().ContainSingle();
        editor.EditRequests[0].MessageId.Should().Be("om-status-1");
        editor.EditRequests[0].Text.Should().StartWith("Result (1/2)\n");
        editor.EditRequests[0].Text.Length.Should().BeLessThanOrEqualTo(LarkCardTextFallbackPolicy.SegmentThreshold);
        outbound.SendRequests.Should().HaveCount(2);
        ExtractLarkText(outbound.SendRequests[1].ContentJson).Should().StartWith("Result (2/2)\n");
        ExtractLarkText(outbound.SendRequests[1].ContentJson).Length.Should()
            .BeLessThanOrEqualTo(LarkCardTextFallbackPolicy.SegmentThreshold);
        agent.State.LarkCardTextFallback.SegmentsDelivered.Should().Be(2);
        agent.State.LarkCardTextFallback.TotalSegments.Should().Be(2);
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Completed);
    }

    [Fact]
    public async Task CreateFailure_UsesInboundNyxProviderSlugForStatusEditAndFinalSegmentSend()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);
        var initial = CreateCardChunk("interim 1");
        initial.Activity.TransportExtras.NyxProviderSlug = "api-lark-bot-4";

        await agent.HandleEventAsync(Envelope(agent.Id, initial));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        var longText = new string('D', LarkCardTextFallbackPolicy.SegmentThreshold + 100);
        var final = CreateCardChunk(longText, isFinal: true);
        final.Activity.TransportExtras.NyxProviderSlug = "api-lark-bot-4";
        await agent.HandleEventAsync(Envelope(agent.Id, final));

        outbound.SendRequests.Should().HaveCount(2);
        outbound.SendRequests[0].NyxProviderSlug.Should().Be("api-lark-bot-4");
        outbound.SendRequests[1].NyxProviderSlug.Should().Be("api-lark-bot-4");
        editor.EditRequests.Should().ContainSingle();
        editor.EditRequests[0].NyxProviderSlug.Should().Be("api-lark-bot-4");
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Completed);
    }

    [Fact]
    public async Task CreateFailure_WithoutNyxUserAccessToken_FailsExplicitlyWithoutRelayFallback()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher, larkOutboundDispatcher: outbound);
        var chunk = CreateCardChunk("fallback text");
        chunk.Activity.TransportExtras.NyxUserAccessToken = string.Empty;

        await agent.HandleEventAsync(Envelope(agent.Id, chunk));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.CreationFailed);
        agent.State.LarkCardTextFallback.Should().NotBeNull();
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Failed);
        agent.State.LarkCardTextFallback.LastErrorCode.Should().Be("nyx_access_token_unavailable");
        agent.State.LarkCardTextFallback.StatusMessageId.Should().BeEmpty();
        outbound.SendRequests.Should().BeEmpty();
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFailure_WithoutNyxProviderSlug_FailsExplicitlyWithoutOrdinaryLarkDispatch()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);
        var chunk = CreateCardChunk("fallback text");
        chunk.Activity.TransportExtras.NyxProviderSlug = string.Empty;

        await agent.HandleEventAsync(Envelope(agent.Id, chunk));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        var finalStep = CreateFinalReplyStep("authoritative final");
        finalStep.Request.Activity.TransportExtras.NyxProviderSlug = string.Empty;
        await agent.HandleNextLlmStepAsync(finalStep);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.CreationFailed);
        agent.State.LarkCardTextFallback.Should().NotBeNull();
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Failed);
        agent.State.LarkCardTextFallback.LastErrorCode.Should().Be("lark_provider_slug_unavailable");
        agent.State.LarkCardTextFallback.StatusMessageId.Should().BeEmpty();
        outbound.SendRequests.Should().BeEmpty();
        editor.EditRequests.Should().BeEmpty();
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("lark_provider_slug_unavailable");
    }

    [Fact]
    public async Task CreationFailedWithCompletedFallbackAndPendingCompletion_DispatchesOutboxWithoutAnotherLarkSend()
    {
        var conversationStore = new InMemoryEventStore();
        var conversationPublisher = new SelfHandlingConversationPublisher();
        var conversation = await CreateConversationAgentAsync(
            ConversationActorId,
            conversationStore,
            conversationPublisher);
        SeedReplyLifecycle(conversation, "corr-card");
        var publisher = new RecordingEventPublisher
        {
            ConversationTarget = conversation,
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        var agent = CreateAgent(
            new RecordingCardRunner(),
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);
        SetState(agent, BuildPendingCompletionState(
            larkPhase: AgentRunLarkCardDeliveryPhase.CreationFailed,
            fallbackPhase: AgentRunLarkCardTextFallbackPhase.Completed));

        await agent.HandleStartAsync(CreateReady("final"));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.PendingCardDeliveryCompletion.Should().BeNull();
        publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Should().ContainSingle();
        outbound.SendRequests.Should().BeEmpty();
        editor.EditRequests.Should().BeEmpty();
        var conversationEvents = await conversationStore.GetEventsAsync(ConversationActorId);
        conversationEvents.Should().ContainSingle(e => e.EventData.Is(LlmReplyDeliveredEvent.Descriptor));
        conversationEvents.Should().ContainSingle(e => e.EventData.Is(ConversationTurnCompletedEvent.Descriptor));
    }

    [Fact]
    public async Task CreateFailure_ShortFallbackCompletion_DispatchesCompletionAndHandsOffRun()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected"),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("short final"));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Completed);
        editor.EditRequests.Should().ContainSingle(request => request.Text == "short final");
        outbound.SendRequests.Should().ContainSingle();
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.OutboundText.Should().Be("short final");
        completed.CardMessageId.Should().Be("om-status-1");
        completed.DeliveryFailure.Should().BeNull();
        completed.AppendedHistory.Should().ContainSingle(entry =>
            entry.Role == "assistant" &&
            entry.Content == "short final");
    }

    [Fact]
    public async Task CreateFailure_SegmentedFallbackCompletion_DispatchesCompletionAndHandsOffRun()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected"),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        var longText = new string('B', LarkCardTextFallbackPolicy.SegmentThreshold + 100);
        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep(longText));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Completed);
        agent.State.LarkCardTextFallback.SegmentsDelivered.Should().Be(2);
        agent.State.LarkCardTextFallback.TotalSegments.Should().Be(2);
        editor.EditRequests.Should().ContainSingle();
        outbound.SendRequests.Should().HaveCount(2);
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.OutboundText.Should().StartWith("Result (1/2)");
        completed.OutboundText.Should().Contain("Result (2/2)");
        completed.DeliveryFailure.Should().BeNull();
    }

    [Fact]
    public async Task CreateFailure_EditException_PersistsFailedFallbackAndDispatchesDeliveryFailure()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected"),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort
        {
            ExceptionToThrow = new InvalidOperationException("edit transport down"),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("final after edit exception"));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Failed);
        agent.State.LarkCardTextFallback.LastErrorCode.Should().Be("status_edit_exception");
        agent.State.LarkCardTextFallback.LastErrorMessage.Should().Contain("edit transport down");
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("status_edit_exception");
        completed.OutboundText.Should().Be("Processing your request. Please wait...");
    }

    [Fact]
    public async Task CreateFailure_EditTimeout_PersistsFailedFallbackAndDispatchesDeliveryFailure()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-30T10:00:00Z"));
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected"),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort
        {
            NeverCompletes = true,
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor,
            timeProvider: timeProvider);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        var completion = agent.HandleNextLlmStepAsync(CreateFinalReplyStep("final after edit timeout"));
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await completion.WaitAsync(TimeSpan.FromSeconds(1));

        editor.CancellationTokens.Should().ContainSingle(token => token.CanBeCanceled);
        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Failed);
        agent.State.LarkCardTextFallback.LastErrorCode.Should().Be("status_edit_timeout");
        agent.State.LarkCardTextFallback.LastErrorMessage.Should().Contain("timed out");
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("status_edit_timeout");
        completed.OutboundText.Should().Be("Processing your request. Please wait...");
    }

    [Fact]
    public async Task CreateFailure_SegmentSendException_PersistsFailedFallbackAndDispatchesDeliveryFailure()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected"),
        };
        var outbound = new RecordingLarkOutboundDispatcher
        {
            ExceptionToThrowAfterRequests = 1,
        };
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        var longText = new string('C', LarkCardTextFallbackPolicy.SegmentThreshold + 100);
        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep(longText));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Failed);
        agent.State.LarkCardTextFallback.LastErrorCode.Should().Be("segment_send_exception");
        agent.State.LarkCardTextFallback.LastErrorMessage.Should().Contain("send transport down");
        agent.State.LarkCardTextFallback.SegmentsDelivered.Should().Be(1);
        agent.State.LarkCardTextFallback.TotalSegments.Should().Be(2);
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("segment_send_exception");
        completed.OutboundText.Should().StartWith("Result (1/2)");
    }

    [Fact]
    public async Task CreateFailure_SegmentSendTimeout_PersistsFailedFallbackAndDispatchesDeliveryFailure()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-30T10:00:00Z"));
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected"),
        };
        var outbound = new RecordingLarkOutboundDispatcher
        {
            NeverCompleteAfterRequests = 1,
        };
        var editor = new RecordingLarkTextMessageEditPort();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor,
            timeProvider: timeProvider);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        var longText = new string('E', LarkCardTextFallbackPolicy.SegmentThreshold + 100);
        var completion = agent.HandleNextLlmStepAsync(CreateFinalReplyStep(longText));
        timeProvider.Advance(TimeSpan.FromSeconds(10));
        await completion.WaitAsync(TimeSpan.FromSeconds(1));

        outbound.CancellationTokens.Should().HaveCount(2);
        outbound.CancellationTokens[1].CanBeCanceled.Should().BeTrue();
        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Failed);
        agent.State.LarkCardTextFallback.LastErrorCode.Should().Be("segment_send_timeout");
        agent.State.LarkCardTextFallback.LastErrorMessage.Should().Contain("timed out");
        agent.State.LarkCardTextFallback.SegmentsDelivered.Should().Be(1);
        agent.State.LarkCardTextFallback.TotalSegments.Should().Be(2);
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("segment_send_timeout");
        completed.OutboundText.Should().StartWith("Result (1/2)");
    }

    [Fact]
    public async Task CreateFailure_EditFailedThenFreshFinalSendSucceeds_CompletesFallback()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected"),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        var editor = new RecordingLarkTextMessageEditPort();
        editor.Results.Enqueue(LarkTextMessageEditResult.Failed(230002, "message edit rejected"));
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("fresh final after edit failure"));

        editor.EditRequests.Should().ContainSingle(request => request.Text == "fresh final after edit failure");
        outbound.SendRequests.Should().HaveCount(2);
        ExtractLarkText(outbound.SendRequests[0].ContentJson).Should().Be("Processing your request. Please wait...");
        ExtractLarkText(outbound.SendRequests[1].ContentJson).Should().Be("fresh final after edit failure");
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Completed);
        agent.State.LarkCardTextFallback.LastDeliveredText.Should().Be("fresh final after edit failure");
        agent.State.LarkCardTextFallback.LastErrorCode.Should().BeEmpty();
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.DeliveryFailure.Should().BeNull();
        completed.OutboundText.Should().Be("fresh final after edit failure");
    }

    [Fact]
    public async Task CreateFailure_EditFailedThenFreshFinalSendFails_PersistsTypedDeliveryFailure()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected"),
        };
        var outbound = new RecordingLarkOutboundDispatcher();
        outbound.Results.Enqueue(LarkSendNewMessageResult.Sent(
            "om-status-1",
            new LarkReceiveTarget("oc-group-1", "chat_id", FellBackToPrefixInference: false),
            usedFallback: false));
        outbound.Results.Enqueue(LarkSendNewMessageResult.Failed(
            new LarkReceiveTarget("oc-group-1", "chat_id", FellBackToPrefixInference: false),
            usedFallback: false,
            larkCode: 99992361,
            detail: "fresh send rejected"));
        var editor = new RecordingLarkTextMessageEditPort();
        editor.Results.Enqueue(LarkTextMessageEditResult.Failed(230002, "message edit rejected"));
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            larkOutboundDispatcher: outbound,
            larkTextMessageEditPort: editor);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("fresh final after edit failure"));

        editor.EditRequests.Should().ContainSingle(request => request.Text == "fresh final after edit failure");
        outbound.SendRequests.Should().HaveCount(2);
        ExtractLarkText(outbound.SendRequests[1].ContentJson).Should().Be("fresh final after edit failure");
        agent.State.LarkCardTextFallback.Phase.Should().Be(AgentRunLarkCardTextFallbackPhase.Failed);
        agent.State.LarkCardTextFallback.LastErrorCode.Should()
            .Be("final_send_failed_after_edit_failed:99992361");
        agent.State.LarkCardTextFallback.LastErrorMessage.Should().Contain("status_edit_failed=message edit rejected");
        agent.State.LarkCardTextFallback.LastErrorMessage.Should().Contain("send=fresh send rejected");
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("final_send_failed_after_edit_failed:99992361");
        completed.OutboundText.Should().Be("Processing your request. Please wait...");
    }

    [Fact]
    public async Task CreateTimeout_MarksCreationFailedWithoutFallback()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(new RecordingCardRunner(), publisher: publisher, scheduler: scheduler);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("pending create")));
        var timeout = ScheduledTimeout(scheduler, LarkCardOperationPhase.Create);

        await agent.HandleEventAsync(Envelope(agent.Id, timeout));

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.CreationFailed);
        agent.State.LarkCardDelivery.TerminalReason.Should().Be("create_timeout");
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Unspecified);
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();
        publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Should().BeEmpty();

        var sentCount = publisher.Sent.Count;
        await agent.HandleEventAsync(Envelope(agent.Id, timeout));
        publisher.Sent.Should().HaveCount(sentCount);
    }

    [Fact]
    public async Task StreamTimeout_RecoversStreamingStateAndDropsPendingFrame()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var runner = new RecordingCardRunner();
        var agent = CreateAgent(runner, publisher: publisher, scheduler: scheduler);
        await StartVisibleCardAsync(agent, publisher, "first visible");

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("timed out frame")));
        var timeout = ScheduledTimeout(scheduler, LarkCardOperationPhase.Stream);

        await agent.HandleEventAsync(Envelope(agent.Id, timeout));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Streaming);
        agent.State.LarkCardDelivery.LastFlushedText.Should().Be("first visible");
        agent.State.LarkCardDelivery.Sequence.Should().Be(1);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Unspecified);
        agent.State.LarkCardDelivery.PendingAccumulatedText.Should().BeEmpty();
        runner.StreamCalls.Should().BeEmpty();
        publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Should().BeEmpty();
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();

        var sentCount = publisher.Sent.Count;
        await agent.HandleEventAsync(Envelope(agent.Id, timeout));
        publisher.Sent.Should().HaveCount(sentCount);
    }

    [Fact]
    public async Task FinalizeTimeout_CompletesDeliveryWithFailureAndIsIdempotent()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var runner = new RecordingCardRunner();
        var agent = CreateAgent(runner, publisher: publisher, scheduler: scheduler);
        await StartVisibleCardAsync(agent, publisher, "partial");

        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("final"));
        var timeout = ScheduledTimeout(scheduler, LarkCardOperationPhase.Finalize);

        await agent.HandleEventAsync(Envelope(agent.Id, timeout));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Terminated);
        agent.State.LarkCardDelivery.TerminalReason.Should().Be("finalize_timeout");
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Unspecified);
        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runner.FinalizeCalls.Should().BeEmpty();
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.OutboundText.Should().Be("partial");
        completed.CardMessageId.Should().Be("om-card-ok");
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("finalize_timeout");
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();

        var sentCount = publisher.Sent.Count;
        await agent.HandleEventAsync(Envelope(agent.Id, timeout));
        publisher.Sent.Should().HaveCount(sentCount);
    }

    [Fact]
    public async Task CreatePostSendFailure_CompletesPartialCardWithoutTextFallback()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.PostSendFailed(
                "card-orphan",
                "om-orphan",
                "first_stream_write_failed",
                "first element write failed"),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("already visible as card")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Terminated);
        agent.State.LarkCardDelivery.TerminalReason.Should().Be("create_post_send_failed:first_stream_write_failed");
        agent.State.LarkCardDelivery.CardId.Should().Be("card-orphan");
        agent.State.LarkCardDelivery.CardMessageId.Should().Be("om-orphan");
        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.CardMessageId.Should().Be("om-orphan");
        completed.OutboundText.Should().BeEmpty();
        completed.DeliveryFailure.Should().BeNull();

        var createCompleted = publisher.Sent
            .Select(e => e.Event)
            .OfType<LarkCardOperationCompletedEvent>()
            .Single(e => e.Operation == LarkCardOperationPhase.Create);
        var sentCount = publisher.Sent.Count;
        await agent.HandleEventAsync(Envelope(agent.Id, createCompleted));
        publisher.Sent.Should().HaveCount(sentCount);
    }

    [Fact]
    public async Task RateLimitedStreamFailure_RecoversWithoutDeliveryOrFallback()
    {
        var runner = new RecordingCardRunner
        {
            StreamResult = ConversationCardStreamResult.Failed(
                "rate_limit",
                "stream rejected",
                isRateLimited: true),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);
        await StartVisibleCardAsync(agent, publisher, "first visible");

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("rate limited frame")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Streaming);
        agent.State.LarkCardDelivery.LastFlushedText.Should().Be("first visible");
        agent.State.LarkCardDelivery.Sequence.Should().Be(1);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Unspecified);
        agent.State.LarkCardDelivery.PendingAccumulatedText.Should().BeEmpty();
        runner.StreamCalls.Should().ContainSingle(call => call.Text == "rate limited frame" && call.Sequence == 2);
        publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Should().BeEmpty();
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();

        var streamCompleted = publisher.Sent
            .Select(e => e.Event)
            .OfType<LarkCardOperationCompletedEvent>()
            .Single(e => e.Operation == LarkCardOperationPhase.Stream);
        var sentCount = publisher.Sent.Count;
        await agent.HandleEventAsync(Envelope(agent.Id, streamCompleted));
        publisher.Sent.Should().HaveCount(sentCount);
    }

    [Fact]
    public async Task TableLimitStreamFailure_CompletesWithLastVisibleCardWithoutFallback()
    {
        var runner = new RecordingCardRunner
        {
            StreamResult = ConversationCardStreamResult.Failed(
                "table_limit",
                "table limit exceeded",
                isTableLimitExceeded: true),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);
        await StartVisibleCardAsync(agent, publisher, "first visible");

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("hidden table-limit frame")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Terminated);
        agent.State.LarkCardDelivery.TerminalReason.Should().Be("stream_failed:table_limit");
        agent.State.LarkCardDelivery.LastFlushedText.Should().Be("first visible");
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Unspecified);
        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.CardMessageId.Should().Be("om-card-ok");
        completed.OutboundText.Should().Be("first visible");
        completed.DeliveryFailure.Should().BeNull();

        var streamCompleted = publisher.Sent
            .Select(e => e.Event)
            .OfType<LarkCardOperationCompletedEvent>()
            .Single(e => e.Operation == LarkCardOperationPhase.Stream);
        var sentCount = publisher.Sent.Count;
        await agent.HandleEventAsync(Envelope(agent.Id, streamCompleted));
        publisher.Sent.Should().HaveCount(sentCount);
    }

    [Fact]
    public async Task TimeoutPayloads_DoNotPersistRuntimeCredentials()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(new RecordingCardRunner(), publisher: publisher, scheduler: scheduler);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("partial")));

        var createTimeout = scheduler.Timeouts.Should().ContainSingle().Subject;
        var createText = Encoding.UTF8.GetString(createTimeout.TriggerEnvelope.ToByteArray());
        createText.Should().NotContain("runtime-reply-token");
        createText.Should().NotContain("runtime-user-access-token");
        createText.Should().NotContain("reply_token");
        createText.Should().NotContain("nyx_user_access_token");

        await DispatchPendingSelfEventsAsync(agent, publisher);
        scheduler.Timeouts.Clear();

        await agent.HandleNextLlmStepAsync(new AgentRunNextLlmStepRequestedEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            Attempt = 1,
            StepIndex = 2,
            Request = CreateReady("final", activityAccessToken: "ready-user-access-token"),
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = "final",
                Content = "final",
                FinishReason = "stop",
                HasStreamedTextContent = true,
            },
        });

        var finalizeTimeout = scheduler.Timeouts.Should().ContainSingle().Subject;
        var timeout = finalizeTimeout.TriggerEnvelope.Payload.Unpack<LarkCardOperationTimeoutFiredEvent>();
        timeout.Operation.Should().Be(LarkCardOperationPhase.Finalize);
        timeout.Activity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();
        var finalizeText = Encoding.UTF8.GetString(finalizeTimeout.TriggerEnvelope.ToByteArray());
        finalizeText.Should().NotContain("ready-user-access-token");
        finalizeText.Should().NotContain("runtime-reply-token");
        finalizeText.Should().NotContain("nyx_user_access_token");
        finalizeText.Should().NotContain("reply_token");
    }

    [Fact]
    public void AgentRunLarkCardDeliveryStateAndEvent_RoundtripTypedContract()
    {
        var state = new AgentRunGAgentState
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            LarkCardDelivery = new AgentRunLarkCardDeliveryState
            {
                Phase = AgentRunLarkCardDeliveryPhase.Streaming,
                CardId = "card-1",
                CardMessageId = "om-1",
                OriginalCardId = "card-1",
                LastFlushedText = "partial",
                Sequence = 4,
                StreamingElementId = "streaming_main",
                InFlightOperation = LarkCardOperationPhase.Finalize,
                InFlightSequence = 5,
                OperationGeneration = 6,
                PendingFinalizeText = "final",
                PendingFinalizeCommandId = "llm:corr-card",
            },
            PendingCardDeliveryCompletion = BuildPendingCompletion(),
            LarkCardTextFallback = new AgentRunLarkCardTextFallbackState
            {
                Phase = AgentRunLarkCardTextFallbackPhase.Completed,
                CorrelationId = "corr-card",
                StatusMessageId = "om-status-1",
                LastDeliveredText = "fallback final",
                SegmentsDelivered = 1,
                TotalSegments = 2,
                LastErrorCode = "segment_send_failed:230001",
                LastErrorMessage = "segment rejected",
                UpdatedAtUnixMs = 43,
            },
        };
        state.LarkCardDelivery.PendingAppendedHistory.Add(new ConversationHistoryEntry
        {
            Role = "assistant",
            Content = "final",
        });
        var evt = new AgentRunLarkCardDeliveryChangedEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            ChangedAtUnixMs = 42,
            PreviousPhase = AgentRunLarkCardDeliveryPhase.Streaming,
            Phase = AgentRunLarkCardDeliveryPhase.Completed,
            InFlightOperation = LarkCardOperationPhase.Unspecified,
            OperationSequence = 0,
            OperationGeneration = 6,
            CardIdAssigned = "card-1",
            CardMessageIdAssigned = "om-1",
            FlushedText = "final",
            Sequence = 5,
            TerminalReason = "completed",
        };
        var fallbackChanged = new AgentRunLarkCardTextFallbackChangedEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            ChangedAtUnixMs = 44,
            Phase = AgentRunLarkCardTextFallbackPhase.Failed,
            StatusMessageId = "om-status-1",
            LastDeliveredText = "fallback partial",
            SegmentsDelivered = 1,
            TotalSegments = 2,
            LastErrorCode = "segment_send_exception",
            LastErrorMessage = "send transport down",
        };

        AgentRunGAgentState.Parser.ParseFrom(state.ToByteArray()).Should().Be(state);
        AgentRunLarkCardTextFallbackState.Parser.ParseFrom(
                state.LarkCardTextFallback.ToByteArray())
            .Should().Be(state.LarkCardTextFallback);
        AgentRunLarkCardDeliveryCompletion.Parser.ParseFrom(
                state.PendingCardDeliveryCompletion.ToByteArray())
            .Should().Be(state.PendingCardDeliveryCompletion);
        AgentRunLarkCardDeliveryChangedEvent.Parser.ParseFrom(evt.ToByteArray()).Should().Be(evt);
        AgentRunLarkCardTextFallbackChangedEvent.Parser.ParseFrom(fallbackChanged.ToByteArray())
            .Should().Be(fallbackChanged);
    }

    private static AgentRunGAgent CreateAgent(
        RecordingCardRunner runner,
        RecordingEventPublisher? publisher = null,
        RecordingCallbackScheduler? scheduler = null,
        RecordingLarkOutboundDispatcher? larkOutboundDispatcher = null,
        RecordingLarkTextMessageEditPort? larkTextMessageEditPort = null,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions = null,
        TimeProvider? timeProvider = null)
    {
        var actorRuntime = new RecordingActorRuntime();
        var executor = new NoopReplyGenerationExecutor();
        var callbackScheduler = scheduler ?? new RecordingCallbackScheduler();
        var agent = new AgentRunGAgent(
            actorRuntime,
            executor,
            relayOptions ?? new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingCardKitEnabled = true,
                StreamingRepliesEnabled = true,
            },
            NullLogger<AgentRunGAgent>.Instance,
            callbackScheduler,
            timeProvider);
        SetId(agent, "agent-run-actor-1");
        var services = new ServiceCollection()
            .AddSingleton<IConversationCardTurnRunner>(runner)
            .AddSingleton<IActorRuntimeCallbackScheduler>(callbackScheduler)
            .AddSingleton<ILarkOutboundDispatcher>(larkOutboundDispatcher ?? new RecordingLarkOutboundDispatcher())
            .AddSingleton<ILarkTextMessageEditPort>(larkTextMessageEditPort ?? new RecordingLarkTextMessageEditPort())
            .BuildServiceProvider();
        agent.Services = services;
        agent.EventSourcing = new StateTransitionEventSourcing<AgentRunGAgentState>((current, evt) =>
            InvokeTransition(agent, current, evt));
        publisher ??= new RecordingEventPublisher();
        publisher.SelfTarget = agent;
        agent.EventPublisher = publisher;
        SetState(agent, new AgentRunGAgentState
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            Status = AgentRunStatus.ReplyGenerationRequested,
            GenerationAttempt = 1,
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-1",
                CorrelationId = "corr-card",
                TargetActorId = "conversation-1",
                Attempt = 1,
                NextStepIndex = 1,
                MaxToolRounds = 4,
            },
        });
        return agent;
    }

    private static AgentRunGAgentState BuildPendingCompletionState(
        LlmReplyDeliveryFailedEvent? deliveryFailure = null,
        AgentRunLarkCardDeliveryPhase? larkPhase = null,
        AgentRunLarkCardTextFallbackPhase? fallbackPhase = null)
    {
        var resolvedLarkPhase = larkPhase ?? (deliveryFailure is null
            ? AgentRunLarkCardDeliveryPhase.Completed
            : AgentRunLarkCardDeliveryPhase.Terminated);
        var state = new AgentRunGAgentState
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = ConversationActorId,
            Status = AgentRunStatus.ReplyProduced,
            GenerationAttempt = 1,
            ProducedReplyText = deliveryFailure is null ? "final" : "partial",
            ProducedOutbound = new MessageContent { Text = deliveryFailure is null ? "final" : "partial" },
            ProducedTerminalState = LlmReplyTerminalState.Completed,
            LarkCardDelivery = new AgentRunLarkCardDeliveryState
            {
                Phase = resolvedLarkPhase,
                CardId = "card-ok",
                CardMessageId = "om-card-ok",
                LastFlushedText = "partial",
                Sequence = 2,
                StreamingElementId = "streaming_main",
                TerminalReason = resolvedLarkPhase switch
                {
                    AgentRunLarkCardDeliveryPhase.Completed => "completed",
                    AgentRunLarkCardDeliveryPhase.CreationFailed => "create_failed:card_create_failed",
                    _ => "finalize_failed:card_finalize_failed",
                },
            },
            PendingCardDeliveryCompletion = BuildPendingCompletion(deliveryFailure),
            GenerationStep = new AgentRunReplyStepState
            {
                RunId = "run-1",
                CorrelationId = "corr-card",
                TargetActorId = ConversationActorId,
                Attempt = 1,
                NextStepIndex = 2,
                MaxToolRounds = 4,
                AccumulatedText = deliveryFailure is null ? "final" : "partial",
                HasStreamedTextContent = true,
            },
        };
        if (fallbackPhase is not null)
        {
            state.LarkCardTextFallback = new AgentRunLarkCardTextFallbackState
            {
                Phase = fallbackPhase.Value,
                CorrelationId = "corr-card",
                StatusMessageId = "om-status-1",
                LastDeliveredText = "final",
                SegmentsDelivered = 1,
                TotalSegments = 1,
                UpdatedAtUnixMs = 123456,
            };
        }

        return state;
    }

    private static AgentRunLarkCardDeliveryCompletion BuildPendingCompletion(
        LlmReplyDeliveryFailedEvent? deliveryFailure = null)
    {
        var completion = new AgentRunLarkCardDeliveryCompletion
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = ConversationActorId,
            CommandId = "llm:corr-card",
            Activity = CreateActivity(string.Empty),
            CardMessageId = "om-card-ok",
            OutboundText = deliveryFailure is null ? "final" : "partial",
            CompletedAtUnixMs = 123456,
            Outcome = deliveryFailure is null
                ? AgentRunLarkCardDeliveryCompletionOutcome.Completed
                : AgentRunLarkCardDeliveryCompletionOutcome.Failed,
        };
        completion.AppendedHistory.Add(new ConversationHistoryEntry
        {
            Role = "assistant",
            Content = completion.OutboundText,
        });
        if (deliveryFailure is not null)
        {
            completion.DeliveryFailure = deliveryFailure.Clone();
            completion.DeliveryFailure.CorrelationId = string.Empty;
            completion.DeliveryFailure.RunId = string.Empty;
        }

        return completion;
    }

    private static async Task<ConversationGAgent> CreateConversationAgentAsync(
        string id,
        IEventStore store,
        SelfHandlingConversationPublisher publisher)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<IActorDispatchPort, NoopActorDispatchPort>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddSingleton<IConversationTurnRunner>(new RecordingTurnRunner())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ConversationGAgent
        {
            Services = services,
            EventPublisher = publisher,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(),
        };
        SetId(agent, id);
        publisher.SelfTarget = agent;
        await agent.ActivateAsync();
        return agent;
    }

    private static void SeedReplyLifecycle(ConversationGAgent agent, string correlationId)
    {
        agent.State.ActiveReplyLifecycles.Add(new ConversationReplyLifecycleState
        {
            CorrelationId = correlationId,
            Mode = ConversationReplyLifecycleMode.NyxRelayText,
            Phase = ConversationReplyLifecyclePhase.TextStreaming,
            PlatformMessageId = "relay-msg-" + correlationId,
            LastFlushedText = "partial",
            UpdatedAtUnixMs = 100,
        });
    }

    private static async Task StartVisibleCardAsync(
        AgentRunGAgent agent,
        RecordingEventPublisher publisher,
        string initialText)
    {
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk(initialText)));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Streaming);
        agent.State.LarkCardDelivery.LastFlushedText.Should().Be(initialText);
    }

    private static async Task DispatchPendingSelfEventsAsync(
        AgentRunGAgent agent,
        RecordingEventPublisher publisher)
    {
        while (publisher.TryDequeueSelfEvent(out var evt))
            await agent.HandleEventAsync(Envelope(agent.Id, evt));
    }

    private static async Task DispatchSelfEventCountAsync(
        AgentRunGAgent agent,
        RecordingEventPublisher publisher,
        int count)
    {
        for (var i = 0; i < count; i++)
        {
            publisher.TryDequeueSelfEvent(out var evt).Should().BeTrue();
            await agent.HandleEventAsync(Envelope(agent.Id, evt));
        }
    }

    private static AgentRunNextLlmStepRequestedEvent CreateFinalReplyStep(string finalText) =>
        new()
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            Attempt = 1,
            StepIndex = 2,
            Request = CreateReady(finalText),
            LlmStepResult = new AgentRunLlmStepResult
            {
                AccumulatedText = finalText,
                Content = finalText,
                FinishReason = "stop",
                HasStreamedTextContent = true,
            },
        };

    private static LarkCardOperationTimeoutFiredEvent ScheduledTimeout(
        RecordingCallbackScheduler scheduler,
        LarkCardOperationPhase operation) =>
        scheduler.Timeouts
            .Select(timeout => timeout.TriggerEnvelope.Payload.Unpack<LarkCardOperationTimeoutFiredEvent>())
            .Last(timeout => timeout.Operation == operation);

    private static NeedsLlmReplyEvent CreateReady(
        string finalText,
        string activityAccessToken = "runtime-user-access-token") =>
        new()
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            RegistrationId = "reg-1",
            Activity = CreateActivity(activityAccessToken),
            ReplyToken = "runtime-ready-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };

    private static LlmReplyCardStreamChunkEvent CreateCardChunk(string text, bool isFinal = false) =>
        new()
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            RegistrationId = "reg-1",
            Activity = CreateActivity("runtime-user-access-token"),
            AccumulatedText = text,
            ChunkAtUnixMs = 42,
            ReplyToken = "runtime-reply-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            IsFinal = isFinal,
        };

    private static ChatActivity CreateActivity(string userAccessToken) =>
        new()
        {
            Id = "msg-1",
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From("reg-1"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.Group,
                "oc-group-1",
                "group",
                "oc-group-1"),
            Content = new MessageContent { Text = "question" },
            OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                CorrelationId = "corr-card",
            },
            TransportExtras = new TransportExtras
            {
                NyxUserAccessToken = userAccessToken,
                NyxAgentApiKeyId = "api-key-1",
                NyxPlatform = "lark",
                NyxProviderSlug = "api-lark-bot",
                NyxConversationId = "oc-group-1",
                NyxPlatformMessageId = "om-source",
                NyxLarkUnionId = "on-user",
                NyxLarkChatId = "oc-group-1",
                NyxRegistrationScopeId = "reg-scope-1",
                NyxSenderUserId = "nyx-user-1",
            },
        };

    private static string ExtractLarkText(string contentJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(contentJson);
        return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }

    private static EventEnvelope Envelope(string actorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = payload switch
                {
                    LlmReplyCardStreamChunkEvent chunk => chunk.CorrelationId,
                    LarkCardOperationCompletedEvent completed => completed.CorrelationId,
                    LarkCardOperationTimeoutFiredEvent timeout => timeout.CorrelationId,
                    ReplyOperationStepEvent step => step.CorrelationId,
                    AgentRunNextLlmStepRequestedEvent nextLlm => nextLlm.CorrelationId,
                    _ => string.Empty,
                },
            },
        };

    private static void SetId(object agent, string id)
    {
        var current = agent.GetType();
        while (current is not null)
        {
            var setIdMethod = current.GetMethod(
                "SetId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (setIdMethod is not null)
            {
                setIdMethod.Invoke(agent, [id]);
                return;
            }

            current = current.BaseType;
        }

        throw new InvalidOperationException("Unable to set agent id via reflection.");
    }

    private static void SetState(AgentRunGAgent agent, AgentRunGAgentState state)
    {
        var stateField = typeof(Aevatar.Foundation.Core.GAgentBase<AgentRunGAgentState>).GetField(
            "_state",
            BindingFlags.Instance | BindingFlags.NonPublic);
        stateField.Should().NotBeNull();
        stateField!.SetValue(agent, state);
    }

    private static AgentRunGAgentState InvokeTransition(
        AgentRunGAgent agent,
        AgentRunGAgentState current,
        IMessage evt)
    {
        var currentType = agent.GetType();
        while (currentType is not null)
        {
            var transitionMethod = currentType.GetMethod(
                "TransitionState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (transitionMethod is not null)
                return (AgentRunGAgentState)transitionMethod.Invoke(agent, [current, evt])!;

            currentType = currentType.BaseType;
        }

        throw new InvalidOperationException("Unable to invoke AgentRunGAgent transition via reflection.");
    }

    private sealed class RecordingCardRunner : IConversationCardTurnRunner
    {
        public ConversationCardCreateResult CreateResult { get; init; } =
            ConversationCardCreateResult.Succeeded("card-ok", "om-card-ok");

        public ConversationCardStreamResult StreamResult { get; init; } =
            ConversationCardStreamResult.Succeeded();

        public ConversationCardFinalizeResult FinalizeResult { get; init; } =
            ConversationCardFinalizeResult.Succeeded();

        public List<LlmReplyCardStreamChunkEvent> CreateCalls { get; } = [];

        public List<(string Text, long Sequence)> StreamCalls { get; } = [];

        public List<(string FinalText, long Sequence, string ActivityUserAccessToken, string RuntimeUserAccessToken)> FinalizeCalls { get; } = [];

        public Task<ConversationCardCreateResult> RunCardCreateAsync(
            LlmReplyCardStreamChunkEvent chunk,
            string streamingElementId,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            CreateCalls.Add(chunk.Clone());
            return Task.FromResult(CreateResult);
        }

        public Task<ConversationCardStreamResult> RunCardStreamAsync(
            LlmReplyCardStreamChunkEvent chunk,
            string cardId,
            string elementId,
            long sequence,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            StreamCalls.Add((chunk.AccumulatedText, sequence));
            return Task.FromResult(StreamResult);
        }

        public Task<ConversationCardFinalizeResult> RunCardFinalizeAsync(
            ChatActivity referenceActivity,
            string cardId,
            string elementId,
            string finalText,
            bool finalTextDiffersFromLastFlushed,
            long sequence,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            FinalizeCalls.Add((
                finalText,
                sequence,
                referenceActivity.TransportExtras?.NyxUserAccessToken ?? string.Empty,
                runtimeContext.NyxUserAccessToken ?? string.Empty));
            return Task.FromResult(FinalizeResult);
        }
    }

    private sealed class RecordingLarkOutboundDispatcher : ILarkOutboundDispatcher
    {
        public List<LarkSendNewMessageRequest> SendRequests { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Queue<LarkSendNewMessageResult> Results { get; } = [];

        public int? ExceptionToThrowAfterRequests { get; init; }

        public int? NeverCompleteAfterRequests { get; init; }

        public bool NeverCompleteIgnoresCancellation { get; init; }

        public Task<LarkSendNewMessageResult> SendNewMessageAsync(
            LarkSendNewMessageRequest request,
            CancellationToken ct)
        {
            SendRequests.Add(request);
            CancellationTokens.Add(ct);
            if (ExceptionToThrowAfterRequests is { } threshold && SendRequests.Count > threshold)
                throw new InvalidOperationException("send transport down");

            if (NeverCompleteAfterRequests is { } neverCompleteThreshold && SendRequests.Count > neverCompleteThreshold)
                return NeverCompleteIgnoresCancellation
                    ? NeverComplete<LarkSendNewMessageResult>()
                    : NeverCompleteUntilCanceled<LarkSendNewMessageResult>(ct);

            if (Results.TryDequeue(out var result))
                return Task.FromResult(result);

            return Task.FromResult(LarkSendNewMessageResult.Sent(
                $"om-status-{SendRequests.Count}",
                request.PrimaryTarget,
                usedFallback: false));
        }
    }

    private sealed class RecordingLarkTextMessageEditPort : ILarkTextMessageEditPort
    {
        public List<LarkTextMessageEditRequest> EditRequests { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Queue<LarkTextMessageEditResult> Results { get; } = [];

        public Exception? ExceptionToThrow { get; init; }

        public bool NeverCompletes { get; init; }

        public Task<LarkTextMessageEditResult> EditAsync(
            LarkTextMessageEditRequest request,
            CancellationToken ct)
        {
            EditRequests.Add(request);
            CancellationTokens.Add(ct);
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;

            if (NeverCompletes)
                return NeverCompleteUntilCanceled<LarkTextMessageEditResult>(ct);

            return Task.FromResult(Results.TryDequeue(out var result)
                ? result
                : LarkTextMessageEditResult.Success());
        }
    }

    private static Task<T> NeverCompleteUntilCanceled<T>(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.CanBeCanceled)
        {
            ct.Register(() => completion.TrySetCanceled(ct));
        }

        return completion.Task;
    }

    private static Task<T> NeverComplete<T>() =>
        new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        private readonly Queue<IMessage> _pendingSelfEvents = new();

        public AgentRunGAgent? SelfTarget { get; set; }

        public ConversationGAgent? ConversationTarget { get; init; }

        public bool FailNextConversationCompletion { get; set; }

        public List<(string TargetActorId, IMessage Event)> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            var clone = (IMessage)evt.Descriptor.Parser.ParseFrom(evt.ToByteArray());
            Sent.Add((targetActorId, clone));
            if (ConversationTarget is not null &&
                string.Equals(targetActorId, ConversationTarget.Id, StringComparison.Ordinal) &&
                clone is LarkCardDeliveryCompletedEvent)
            {
                if (FailNextConversationCompletion)
                {
                    FailNextConversationCompletion = false;
                    throw new InvalidOperationException("Injected completion send failure.");
                }

                return ConversationTarget.HandleEventAsync(Envelope(targetActorId, clone), ct);
            }

            if (SelfTarget is not null &&
                string.Equals(targetActorId, SelfTarget.Id, StringComparison.Ordinal) &&
                clone is ReplyOperationStepEvent or LarkCardOperationCompletedEvent)
            {
                _pendingSelfEvents.Enqueue(clone);
            }

            return Task.CompletedTask;
        }

        public bool TryDequeueSelfEvent(out IMessage evt) =>
            _pendingSelfEvents.TryDequeue(out evt!);
    }

    private sealed class SelfHandlingConversationPublisher : IEventPublisher
    {
        public ConversationGAgent? SelfTarget { get; set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            SelfTarget is not null &&
            string.Equals(targetActorId, SelfTarget.Id, StringComparison.Ordinal)
                ? SelfTarget.HandleEventAsync(Envelope(targetActorId, evt), ct)
                : Task.CompletedTask;
    }

    private sealed class RecordingTurnRunner : IConversationTurnRunner
    {
        public Task<ConversationTurnResult> RunInboundAsync(
            ChatActivity activity,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", activity.Id));

        public Task<ConversationTurnResult> RunLlmReplyAsync(
            LlmReplyReadyEvent reply,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", reply.CorrelationId));

        public Task<ConversationTurnResult> RunContinueAsync(
            ConversationContinueRequestedEvent command,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", command.CommandId));

        public Task<ConversationStreamChunkResult> RunStreamChunkAsync(
            LlmReplyStreamChunkEvent chunk,
            string? currentPlatformMessageId,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationStreamChunkResult.Succeeded(currentPlatformMessageId ?? "om"));

        public Task OnReplyDeliveredAsync(ChatActivity activity, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new EventStoreOptimisticConcurrencyException(agentId, expectedVersion, currentVersion);

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DueTime = request.DueTime,
                DeliveryMode = request.DeliveryMode,
            });
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopReplyGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct) =>
            Task.FromResult(new AgentRunReplyStepState());

        public Task ExecuteLlmStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct) =>
            Task.CompletedTask;

        public Task ExecuteToolStepAsync(AgentRunReplyStepExecutionRequest request, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<AgentRunNextLlmStepRequestedEvent> BuildLlmStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StateTransitionEventSourcing<TState>(Func<TState, IMessage, TState> transition)
        : IEventSourcingBehavior<TState>
        where TState : class, IMessage<TState>, new()
    {
        private readonly List<IMessage> _pending = [];

        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
            _pending.Add(evt);
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            CurrentVersion += _pending.Count;
            _pending.Clear();
            return Task.FromResult(new EventStoreCommitResult
            {
                LatestVersion = CurrentVersion,
            });
        }

        public Task PersistSnapshotAsync(TState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<TState?>(null);

        public void DiscardPendingEvents()
        {
            _pending.Clear();
        }

        public TState TransitionState(TState current, IMessage evt) => transition(current, evt);
    }
}
