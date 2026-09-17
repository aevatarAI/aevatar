using System.Reflection;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentRunLarkCardDeliveryTests
{
    private const string ConversationActorId = "conversation-1";

    [Fact]
    public async Task CardChunkWithBlockingMutationReceipt_ShouldReplaceModelNarrativeBeforeCreate()
    {
        var runner = new RecordingCardRunner();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);
        agent.State.GenerationStep.ToolReceipts.Add(new Aevatar.AI.Abstractions.AgentToolReceipt
        {
            CallId = "call-submit",
            ToolName = "submit_record",
            Status = Aevatar.AI.Abstractions.AgentToolReceiptStatus.Error,
            Effect = Aevatar.AI.Abstractions.AgentToolReceiptEffect.Mutating,
            ErrorMessage = "The record was not submitted.",
        });

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("Submission confirmed")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        var visible = runner.CreateCalls.Should().ContainSingle().Subject.AccumulatedText;
        visible.Should().Contain("[tool receipt] Failed: submit_record");
        visible.Should().NotContain("Submission confirmed");
        agent.State.LarkCardDelivery.LastFlushedText.Should().Be(visible);
    }

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
        agent.State.GenerationStep.AgentProfileSnapshot = new AgentProfileSnapshot
        {
            ProfileId = "profile-channel-alpha",
            ProfileVersion = "profile-v1",
            PublishedRevision = 1,
        };

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
        completed.AgentProfile.Should().Be(agent.State.GenerationStep.AgentProfileSnapshot);
        var delivery = agent.State.RecentDeliveries.Should().ContainSingle().Subject;
        delivery.DeliveryKind.Should().Be(DeliveryKind.StreamingCard);
        delivery.Status.Should().Be(DeliveryStatus.Succeeded);
        delivery.ProviderMessageId.Should().Be("om-card-ok");
        delivery.RequestId.Should().Be("llm:corr-card");
        agent.State.LastSuccessfulDelivery.Should().NotBeNull();
        agent.State.LastSuccessfulDelivery!.ProviderMessageId.Should().Be("om-card-ok");
        scheduler.Timeouts.Should().Contain(timeout => timeout.CallbackId.StartsWith(
            "agent-run-terminal-cleanup:run-1",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinalizeAfterVisibleCard_PreservesRunStateToolReceipts()
    {
        var runner = new RecordingCardRunner();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);
        agent.State.GenerationStep.ToolReceipts.Add(new Aevatar.AI.Abstractions.AgentToolReceipt
        {
            CallId = "call-1",
            ToolName = "ornn_publish_skill",
            Status = Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success,
            SideEffectKind = "ornn.publish.skill",
            SubjectKind = "ornn.skill",
            SubjectId = "skill-1",
            SubjectHash = "hash-1",
        });

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("partial")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("final"));

        agent.State.Status.Should().Be(AgentRunStatus.ReplyProduced);
        agent.State.ToolReceipts.Should().ContainSingle(receipt => receipt.CallId == "call-1");

        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Completed);
        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        agent.State.ToolReceipts.Should().ContainSingle(
            receipt => receipt.CallId == "call-1" &&
                       receipt.Status == Aevatar.AI.Abstractions.AgentToolReceiptStatus.Success &&
                       receipt.SubjectId == "skill-1",
            "the card-completion AgentRunReplyProducedEvent must carry the committed tool receipts forward instead of wiping them");
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
        delivery.ProviderMessageId.Should().Be("lark-card-stream:om-card-ok");
        delivery.CardId.Should().BeEmpty();
        delivery.Target.Channel.Value.Should().Be("lark");
        delivery.Target.ConversationKey.Should().Be("lark:group:oc-group-1");
        delivery.Target.Platform.Should().Be("lark");
        delivery.Target.AddressId.Should().Be("oc-group-1");
        delivery.Target.AddressType.Should().Be("chat_id");
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
    public async Task CreateFailure_ForwardsStatusTextOnce()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("partial llm text")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.CreationFailed);
        agent.State.LarkCardDelivery.TextFallbackPhase.Should()
            .Be(AgentRunLarkCardTextFallbackPhase.StatusForwarded);

        var fallback = publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Single();
        fallback.CorrelationId.Should().Be("corr-card");
        fallback.AccumulatedText.Should().Be("Processing your request. Please wait...");
        publisher.Sent.Where(e => e.Event is LlmReplyStreamChunkEvent)
            .Select(e => e.TargetActorId)
            .Should().ContainSingle("conversation-1");
    }

    [Fact]
    public async Task CreateFailure_ForwardsPendingFinalTextWhenLlmCompletesDuringCreate()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("partial llm text")));
        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("the complete final reply"));

        agent.State.LarkCardDelivery.PendingFinalizeText.Should().Be("the complete final reply");

        await DispatchPendingSelfEventsAsync(agent, publisher);

        var dispatched = publisher.Sent
            .Select(e => e.Event)
            .OfType<LlmReplyStreamChunkEvent>()
            .ToList();
        dispatched.Select(chunk => chunk.AccumulatedText)
            .Should().Equal(
                "Processing your request. Please wait...",
                "the complete final reply");
        dispatched.Select(chunk => chunk.ReplyToken)
            .Should().Equal("runtime-reply-token", "runtime-reply-token");
        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.CreationFailed);
        agent.State.LarkCardDelivery.TextFallbackPhase.Should()
            .Be(AgentRunLarkCardTextFallbackPhase.FinalForwarded);
    }

    [Fact]
    public async Task CreateFailure_IgnoresInterimChunksAfterStatusForwarded()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            relayOptions: new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingCardKitEnabled = true,
                StreamingRepliesEnabled = true,
                StreamingMaxInterimChunks = 3,
                StreamingFlushIntervalMs = 0,
            });

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 1")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 2 ignored")));
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 3 ignored")));
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 4 ignored")));

        var dispatched = publisher.Sent
            .Select(e => e.Event)
            .OfType<LlmReplyStreamChunkEvent>()
            .ToList();
        dispatched.Should().ContainSingle("only the fixed status text is forwarded before final");
        dispatched[0].AccumulatedText.Should().Be("Processing your request. Please wait...");
        agent.State.LarkCardDelivery.TextFallbackPhase.Should()
            .Be(AgentRunLarkCardTextFallbackPhase.StatusForwarded);
    }

    [Fact]
    public async Task CreateFailure_ForwardsFinalSnapshotOnceAfterIgnoringInterims()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            relayOptions: new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions
            {
                StreamingCardKitEnabled = true,
                StreamingRepliesEnabled = true,
                StreamingMaxInterimChunks = 1,
                StreamingFlushIntervalMs = 0,
            });

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 1")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 2 ignored")));
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim 3 ignored")));
        await agent.HandleEventAsync(Envelope(
            agent.Id,
            CreateCardChunk("the complete final reply", isFinal: true)));

        var dispatched = publisher.Sent
            .Select(e => e.Event)
            .OfType<LlmReplyStreamChunkEvent>()
            .ToList();
        dispatched.Select(chunk => chunk.AccumulatedText)
            .Should().Equal(
                "Processing your request. Please wait...",
                "the complete final reply");
        agent.State.LarkCardDelivery.TextFallbackPhase.Should()
            .Be(AgentRunLarkCardTextFallbackPhase.FinalForwarded);
    }

    [Fact]
    public async Task CreateFailure_DoesNotForwardDuplicateFinalSnapshots()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.Failed(
                "card_create_failed",
                "create rejected",
                isRateLimited: true),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("interim")));
        await DispatchPendingSelfEventsAsync(agent, publisher);
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("final", isFinal: true)));
        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("final duplicate", isFinal: true)));

        var dispatched = publisher.Sent
            .Select(e => e.Event)
            .OfType<LlmReplyStreamChunkEvent>()
            .ToList();
        dispatched.Select(chunk => chunk.AccumulatedText)
            .Should().Equal("Processing your request. Please wait...", "final");
        agent.State.LarkCardDelivery.TextFallbackPhase.Should()
            .Be(AgentRunLarkCardTextFallbackPhase.FinalForwarded);
    }

    [Fact]
    public async Task ConversationTextFallbackStatusAndFinal_CompletionDoesNotEditAgain()
    {
        var store = new InMemoryEventStore();
        var publisher = new SelfHandlingConversationPublisher();
        var runner = new RecordingTurnRunner();
        var conversation = await CreateConversationAgentAsync(
            ConversationActorId,
            store,
            publisher,
            runner: runner);

        conversation.State.PendingLlmReplyRequests.Add(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-card",
            RunId = "run-1",
            TargetActorId = ConversationActorId,
            RegistrationId = "reg-1",
            Activity = CreateActivity("runtime-user-access-token"),
            RequestedAtUnixMs = 10,
        });

        await conversation.HandleLlmReplyStreamChunkAsync(CreateTextStreamChunk("Processing your request. Please wait..."));
        await conversation.HandleLlmReplyStreamChunkAsync(CreateTextStreamChunk("the complete final reply"));
        await conversation.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "corr-card",
            RegistrationId = "reg-1",
            RunId = "run-1",
            Activity = CreateActivity("runtime-user-access-token"),
            Outbound = new MessageContent { Text = "the complete final reply" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
        });

        runner.StreamCalls.Select(call => call.Kind)
            .Should().Equal(
                NyxRelayTextOperationKind.Interim,
                NyxRelayTextOperationKind.Interim);
        runner.StreamCalls.Select(call => call.Text)
            .Should().Equal(
                "Processing your request. Please wait...",
                "the complete final reply");
        runner.StreamCalls[0].CurrentPlatformMessageId.Should().BeNull();
        runner.StreamCalls[1].CurrentPlatformMessageId.Should().Be("om");

        var completed = (await store.GetEventsAsync(ConversationActorId))
            .Single(e => e.EventData.Is(ConversationTurnCompletedEvent.Descriptor))
            .EventData.Unpack<ConversationTurnCompletedEvent>();
        completed.Outbound.Text.Should().Be("the complete final reply");
        conversation.State.LastReplyDelivery.Delivered.Should().NotBeNull();
        conversation.State.ActiveReplyLifecycles.Should().BeEmpty();
    }

    [Fact]
    public async Task ConversationTextFallbackStatusThenReady_FinalFlushCarriesReplyToken()
    {
        var store = new InMemoryEventStore();
        var publisher = new SelfHandlingConversationPublisher();
        var runner = new RecordingTurnRunner();
        var conversation = await CreateConversationAgentAsync(
            ConversationActorId,
            store,
            publisher,
            runner: runner);

        conversation.State.PendingLlmReplyRequests.Add(new NeedsLlmReplyEvent
        {
            CorrelationId = "corr-card",
            RunId = "run-1",
            TargetActorId = ConversationActorId,
            RegistrationId = "reg-1",
            Activity = CreateActivity("runtime-user-access-token"),
            ReplyToken = "runtime-ready-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            RequestedAtUnixMs = 10,
        });

        await conversation.HandleLlmReplyStreamChunkAsync(CreateTextStreamChunk("Processing your request. Please wait..."));
        await conversation.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "corr-card",
            RegistrationId = "reg-1",
            RunId = "run-1",
            Activity = CreateActivity("runtime-user-access-token"),
            ReplyToken = "runtime-ready-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            Outbound = new MessageContent { Text = "the complete final reply" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
        });

        runner.StreamCalls.Select(call => call.Kind)
            .Should().Equal(
                NyxRelayTextOperationKind.Interim,
                NyxRelayTextOperationKind.Final);
        runner.StreamCalls[1].Text.Should().Be("the complete final reply");
        runner.StreamCalls[1].CurrentPlatformMessageId.Should().Be("om");
        runner.StreamCalls[1].ReplyToken.Should().Be("runtime-ready-token");
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
        var secretStore = new InMemoryRuntimeSecretStore();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            scheduler: scheduler,
            runtimeSecretStore: secretStore);
        agent.State.RelayUserAccessTokenRef = await StoreRelayUserAccessTokenAsync(
            secretStore,
            "abort-user-access-token");
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
        runner.AbortCalls.Should().ContainSingle(call =>
            call.CardId == "card-ok" &&
            call.Sequence > 1 &&
            call.ActivityUserAccessToken == "abort-user-access-token" &&
            call.RuntimeUserAccessToken == "abort-user-access-token");
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.OutboundText.Should().Be("partial");
        completed.CardMessageId.Should().Be("om-card-ok");
        completed.Activity.Conversation.CanonicalKey.Should().Be("lark:group:oc-group-1");
        completed.Activity.OutboundDelivery.ReplyMessageId.Should().Be("relay-msg-1");
        completed.Activity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("finalize_timeout");
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();

        var sentCount = publisher.Sent.Count;
        await agent.HandleEventAsync(Envelope(agent.Id, timeout));
        publisher.Sent.Should().HaveCount(sentCount);
    }

    [Fact]
    public async Task GenerationTimeout_WithVisibleCard_AbortsBeforeFailingAndIsIdempotent()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var runner = new RecordingCardRunner();
        var secretStore = new InMemoryRuntimeSecretStore();
        var agent = CreateAgent(
            runner,
            publisher: publisher,
            scheduler: scheduler,
            runtimeSecretStore: secretStore);
        agent.State.RelayUserAccessTokenRef = await StoreRelayUserAccessTokenAsync(
            secretStore,
            "generation-timeout-user-token");
        await StartVisibleCardAsync(agent, publisher, "partial");
        scheduler.Timeouts.Clear();
        var timeout = GenerationTimeout();

        await agent.HandleReplyGenerationTimedOutAsync(timeout);

        agent.State.Status.Should().Be(AgentRunStatus.ReplyGenerationRequested);
        agent.State.LarkCardDelivery.InFlightOperation.Should().Be(LarkCardOperationPhase.Abort);
        agent.State.LarkCardDelivery.PendingAbortReason.Should().Be(LarkCardAbortReason.GenerationTimeout);
        runner.AbortCalls.Should().BeEmpty("the typed self-continuation owns CardKit IO");
        var abortStep = publisher.Sent
            .Select(e => e.Event)
            .OfType<ReplyOperationStepEvent>()
            .Single(step => step.LarkCard.Operation == LarkCardOperationPhase.Abort);
        abortStep.LarkCard.Activity.TransportExtras.NyxUserAccessToken.Should()
            .Be("generation-timeout-user-token");
        abortStep.LarkCard.Activity.TransportExtras.NyxProviderSlug.Should().Be("api-lark-bot-4");

        var sentBeforeDuplicate = publisher.Sent.Count;
        var scheduledBeforeDuplicate = scheduler.Timeouts.Count;
        await agent.HandleReplyGenerationTimedOutAsync(timeout.Clone());
        publisher.Sent.Should().HaveCount(sentBeforeDuplicate);
        scheduler.Timeouts.Should().HaveCount(scheduledBeforeDuplicate);

        await DispatchPendingSelfEventsAsync(agent, publisher);

        runner.AbortCalls.Should().ContainSingle(call =>
            call.CardId == "card-ok" &&
            call.Sequence > 1 &&
            call.ActivityUserAccessToken == "generation-timeout-user-token" &&
            call.RuntimeUserAccessToken == "generation-timeout-user-token");
        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Aborted);
        agent.State.LarkCardDelivery.TerminalReason.Should().Be("llm_reply_timeout");
        agent.State.Status.Should().Be(AgentRunStatus.Failed);
        publisher.Sent.Select(e => e.Event).OfType<DeferredLlmReplyDroppedEvent>().Should().ContainSingle();

        var sentAfterTerminal = publisher.Sent.Count;
        await agent.HandleReplyGenerationTimedOutAsync(timeout.Clone());
        publisher.Sent.Should().HaveCount(sentAfterTerminal);
    }

    [Fact]
    public async Task GenerationTimeout_AfterProtobufRehydration_ResolvesTokenReferenceForAbort()
    {
        var secretStore = new InMemoryRuntimeSecretStore();
        var firstPublisher = new RecordingEventPublisher();
        var first = CreateAgent(
            new RecordingCardRunner(),
            publisher: firstPublisher,
            runtimeSecretStore: secretStore);
        first.State.RelayUserAccessTokenRef = await StoreRelayUserAccessTokenAsync(
            secretStore,
            "rehydrated-user-access-token");
        await StartVisibleCardAsync(first, firstPublisher, "partial");

        var rehydratedState = AgentRunGAgentState.Parser.ParseFrom(first.State.ToByteArray());
        var runner = new RecordingCardRunner();
        var publisher = new RecordingEventPublisher();
        var scheduler = new RecordingCallbackScheduler();
        var rehydrated = CreateAgent(
            runner,
            publisher: publisher,
            scheduler: scheduler,
            runtimeSecretStore: secretStore);
        SetState(rehydrated, rehydratedState);

        await rehydrated.HandleReplyGenerationTimedOutAsync(GenerationTimeout());
        await DispatchPendingSelfEventsAsync(rehydrated, publisher);

        rehydrated.State.RelayUserAccessTokenRef.Should().Be(rehydratedState.RelayUserAccessTokenRef);
        rehydrated.State.LarkCardDelivery.NyxProviderSlug.Should().Be("api-lark-bot-4");
        runner.AbortCalls.Should().ContainSingle(call =>
            call.ActivityUserAccessToken == "rehydrated-user-access-token" &&
            call.RuntimeUserAccessToken == "rehydrated-user-access-token");
        rehydrated.State.Status.Should().Be(AgentRunStatus.Failed);
    }

    [Fact]
    public async Task AbortTimeout_TerminatesFinalizeWithoutExecutingStaleAbortOrDuplicatingCompletion()
    {
        var scheduler = new RecordingCallbackScheduler();
        var publisher = new RecordingEventPublisher();
        var runner = new RecordingCardRunner();
        var agent = CreateAgent(runner, publisher: publisher, scheduler: scheduler);
        await StartVisibleCardAsync(agent, publisher, "partial");
        scheduler.Timeouts.Clear();
        await agent.HandleNextLlmStepAsync(CreateFinalReplyStep("final"));
        var finalizeTimeout = ScheduledTimeout(scheduler, LarkCardOperationPhase.Finalize);

        await agent.HandleEventAsync(Envelope(agent.Id, finalizeTimeout));
        var abortTimeout = ScheduledTimeout(scheduler, LarkCardOperationPhase.Abort);
        await agent.HandleEventAsync(Envelope(agent.Id, abortTimeout));

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Terminated);
        agent.State.LarkCardDelivery.TerminalReason.Should().Be("finalize_timeout:card_abort_failed:abort_timeout");
        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        runner.FinalizeCalls.Should().BeEmpty();
        runner.AbortCalls.Should().BeEmpty();
        publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Should().ContainSingle();

        var sentCount = publisher.Sent.Count;
        await DispatchPendingSelfEventsAsync(agent, publisher);
        await agent.HandleEventAsync(Envelope(agent.Id, abortTimeout.Clone()));

        runner.AbortCalls.Should().BeEmpty("the queued abort step is stale after the abort timeout commits");
        publisher.Sent.Should().HaveCount(sentCount);
        publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Should().ContainSingle();
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
    public async Task RelayReplyAmbiguousFailure_CompletesAsFailedWithoutReusingReplyAuthority()
    {
        var runner = new RecordingCardRunner
        {
            CreateResult = ConversationCardCreateResult.ReplyAuthoritySpentDeliveryUnknown(
                "card-relay-ambiguous",
                string.Empty,
                "card_relay_reply_threw",
                "reply outcome unknown"),
        };
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(runner, publisher: publisher);

        await agent.HandleEventAsync(Envelope(agent.Id, CreateCardChunk("possibly delivered")));
        await DispatchPendingSelfEventsAsync(agent, publisher);

        agent.State.LarkCardDelivery.Phase.Should().Be(AgentRunLarkCardDeliveryPhase.Terminated);
        agent.State.Status.Should().Be(AgentRunStatus.ReplyHandedOff);
        publisher.Sent.Select(e => e.Event).OfType<LlmReplyStreamChunkEvent>().Should().BeEmpty();
        var completed = publisher.Sent.Select(e => e.Event).OfType<LarkCardDeliveryCompletedEvent>().Single();
        completed.DeliveryFailure.Should().NotBeNull();
        completed.DeliveryFailure.ErrorCode.Should().Be("card_relay_reply_threw");
        agent.State.RecentDeliveries.Should().ContainSingle().Subject.Status
            .Should().Be(DeliveryStatus.FailedPostSend);
        agent.State.LastSuccessfulDelivery.Should().BeNull();
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
        var secretStore = new InMemoryRuntimeSecretStore();
        var agent = CreateAgent(
            new RecordingCardRunner(),
            publisher: publisher,
            scheduler: scheduler,
            runtimeSecretStore: secretStore);
        agent.State.RelayUserAccessTokenRef = await StoreRelayUserAccessTokenAsync(
            secretStore,
            "abort-timeout-user-access-token");

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

        await agent.HandleEventAsync(Envelope(agent.Id, timeout));

        var abortStep = publisher.Sent
            .Select(e => e.Event)
            .OfType<ReplyOperationStepEvent>()
            .Single(step => step.LarkCard.Operation == LarkCardOperationPhase.Abort);
        abortStep.LarkCard.Activity.TransportExtras.NyxUserAccessToken.Should()
            .Be("abort-timeout-user-access-token");
        var abortTimeout = scheduler.Timeouts
            .Last(scheduled => scheduled.TriggerEnvelope.Payload
                .Unpack<LarkCardOperationTimeoutFiredEvent>().Operation == LarkCardOperationPhase.Abort);
        var abortPayload = abortTimeout.TriggerEnvelope.Payload.Unpack<LarkCardOperationTimeoutFiredEvent>();
        abortPayload.AbortReason.Should().Be(LarkCardAbortReason.FinalizeTimeout);
        abortPayload.Activity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();
        var abortTimeoutText = Encoding.UTF8.GetString(abortTimeout.TriggerEnvelope.ToByteArray());
        abortTimeoutText.Should().NotContain("abort-timeout-user-access-token");
        abortTimeoutText.Should().NotContain("nyx_user_access_token");
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
                TextFallbackPhase = AgentRunLarkCardTextFallbackPhase.FinalForwarded,
                NyxProviderSlug = "api-lark-bot-4",
                PendingAbortReason = LarkCardAbortReason.FinalizeTimeout,
                PendingAbortTriggeredAtUnixMs = 41,
            },
            RelayUserAccessTokenRef = new RuntimeSecretReference
            {
                Ref = "rsec_0000000000000001",
                Purpose = ChannelRelayRuntimeSecretPurposes.UserAccessToken,
                Fingerprint = "sha256:fingerprint",
                ExpiresAtUnixMs = 9000,
                OwnerRunId = "run-1",
                OwnerStepId = "corr-card",
            },
            PendingCardDeliveryCompletion = BuildPendingCompletion(),
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
            TextFallbackPhase = AgentRunLarkCardTextFallbackPhase.FinalForwarded,
            NyxProviderSlug = "api-lark-bot-4",
            PendingAbortReason = LarkCardAbortReason.FinalizeTimeout,
            PendingAbortTriggeredAtUnixMs = 41,
        };

        AgentRunGAgentState.Parser.ParseFrom(state.ToByteArray()).Should().Be(state);
        AgentRunLarkCardDeliveryCompletion.Parser.ParseFrom(
                state.PendingCardDeliveryCompletion.ToByteArray())
            .Should().Be(state.PendingCardDeliveryCompletion);
        AgentRunLarkCardDeliveryChangedEvent.Parser.ParseFrom(evt.ToByteArray()).Should().Be(evt);
    }

    [Fact]
    public void ReplyGenerationRequestedReducer_PersistsOnlyRuntimeSecretReference()
    {
        var agent = CreateAgent(new RecordingCardRunner());
        var reference = new RuntimeSecretReference
        {
            Ref = "rsec_0000000000000002",
            Purpose = ChannelRelayRuntimeSecretPurposes.UserAccessToken,
            Fingerprint = "sha256:user-token-fingerprint",
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            OwnerRunId = "run-1",
            OwnerStepId = "corr-card",
        };

        var reduced = InvokeTransition(
            agent,
            new AgentRunGAgentState(),
            new AgentRunReplyGenerationRequestedEvent
            {
                RunId = "run-1",
                CorrelationId = "corr-card",
                TargetActorId = "conversation-1",
                RequestedAtUnixMs = 42,
                Attempt = 1,
                RelayUserAccessTokenRef = reference.Clone(),
            });

        reduced.RelayUserAccessTokenRef.Should().Be(reference);
        Encoding.UTF8.GetString(reduced.ToByteArray()).Should().NotContain("raw-user-access-token");
    }

    private static AgentRunGAgent CreateAgent(
        RecordingCardRunner runner,
        RecordingEventPublisher? publisher = null,
        RecordingCallbackScheduler? scheduler = null,
        Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? relayOptions = null,
        IRuntimeSecretStore? runtimeSecretStore = null)
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
            callbackScheduler);
        SetId(agent, "agent-run-actor-1");
        var services = new ServiceCollection()
            .AddSingleton<IConversationCardTurnRunner>(runner)
            .AddSingleton<IActorRuntimeCallbackScheduler>(callbackScheduler)
            .AddSingleton(runtimeSecretStore ?? new InMemoryRuntimeSecretStore())
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
        LlmReplyDeliveryFailedEvent? deliveryFailure = null) =>
        new()
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
                Phase = deliveryFailure is null
                    ? AgentRunLarkCardDeliveryPhase.Completed
                    : AgentRunLarkCardDeliveryPhase.Terminated,
                CardId = "card-ok",
                CardMessageId = "om-card-ok",
                LastFlushedText = "partial",
                Sequence = 2,
                StreamingElementId = "streaming_main",
                TerminalReason = deliveryFailure is null ? "completed" : "finalize_failed:card_finalize_failed",
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
        SelfHandlingConversationPublisher publisher,
        RecordingTurnRunner? runner = null)
    {
        var dispatchPort = new SelfHandlingConversationDispatchPort();
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddSingleton<IConversationTurnRunner>(runner ?? new RecordingTurnRunner())
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
        dispatchPort.SelfTarget = agent;
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

    private static AgentRunReplyGenerationTimedOut GenerationTimeout() =>
        new()
        {
            RunId = "run-1",
            CorrelationId = "corr-card",
            TargetActorId = "conversation-1",
            TimedOutAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Attempt = 1,
        };

    private static async Task<RuntimeSecretReference> StoreRelayUserAccessTokenAsync(
        IRuntimeSecretStore secretStore,
        string token) =>
        (await secretStore.PutAsync(new StoreRuntimeSecretRequest(
            ChannelRelayRuntimeSecretPurposes.UserAccessToken,
            "run-1",
            "corr-card",
            token,
            TimeSpan.FromMinutes(5),
            ConsumeOnce: false,
            AuditReason: "Test Lark card timeout compensation."))).Reference;

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

    private static LlmReplyStreamChunkEvent CreateTextStreamChunk(string text) =>
        new()
        {
            CorrelationId = "corr-card",
            RegistrationId = "reg-1",
            Activity = CreateActivity("runtime-user-access-token"),
            AccumulatedText = text,
            ChunkAtUnixMs = 42,
            ReplyToken = "runtime-reply-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
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
                NyxConversationId = "oc-group-1",
                NyxPlatformMessageId = "om-source",
                NyxLarkUnionId = "on-user",
                NyxLarkChatId = "oc-group-1",
                NyxRegistrationScopeId = "reg-scope-1",
                NyxSenderUserId = "nyx-user-1",
                NyxProviderSlug = "api-lark-bot-4",
            },
        };

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

        public ConversationCardAbortResult AbortResult { get; init; } =
            ConversationCardAbortResult.Succeeded();

        public List<LlmReplyCardStreamChunkEvent> CreateCalls { get; } = [];

        public List<(string Text, long Sequence)> StreamCalls { get; } = [];

        public List<(string FinalText, long Sequence, string ActivityUserAccessToken, string RuntimeUserAccessToken)> FinalizeCalls { get; } = [];

        public List<(string CardId, long Sequence, string ActivityUserAccessToken, string RuntimeUserAccessToken)> AbortCalls { get; } = [];

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

        public Task<ConversationCardAbortResult> RunCardAbortAsync(
            ChatActivity referenceActivity,
            string cardId,
            long sequence,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            AbortCalls.Add((
                cardId,
                sequence,
                referenceActivity.TransportExtras?.NyxUserAccessToken ?? string.Empty,
                runtimeContext.NyxUserAccessToken ?? string.Empty));
            return Task.FromResult(AbortResult);
        }
    }

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

    private sealed class SelfHandlingConversationDispatchPort : IActorDispatchPort
    {
        public ConversationGAgent? SelfTarget { get; set; }

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            if (SelfTarget is not null &&
                string.Equals(actorId, SelfTarget.Id, StringComparison.Ordinal))
            {
                await SelfTarget.HandleEventAsync(envelope, ct);
            }

            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class RecordingTurnRunner : IConversationTurnRunner
    {
        public List<StreamCall> StreamCalls { get; } = [];

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
            NyxRelayTextOperationKind operation,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            StreamCalls.Add(new StreamCall(
                operation,
                chunk.AccumulatedText,
                currentPlatformMessageId,
                runtimeContext.NyxRelayReplyToken?.ReplyToken));
            return Task.FromResult(ConversationStreamChunkResult.Succeeded(currentPlatformMessageId ?? "om"));
        }

        public Task OnReplyDeliveredAsync(ChatActivity activity, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record StreamCall(
        NyxRelayTextOperationKind Kind,
        string Text,
        string? CurrentPlatformMessageId,
        string? ReplyToken);

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

        public Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
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
            var result = new EventStoreCommitResult();
            foreach (var evt in _pending)
            {
                CurrentVersion++;
                result.CommittedEvents.Add(new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = CurrentVersion,
                    EventType = evt.Descriptor.FullName,
                    EventData = Any.Pack(evt),
                });
            }

            result.LatestVersion = CurrentVersion;
            _pending.Clear();
            return Task.FromResult(result);
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
