using System.Reflection;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ConversationGAgentDedupTests
{
    [Fact]
    public void ConversationRuntimeSource_ShouldNotReintroduceActorTokenRegistryCleanupPath()
    {
        var productionSource = string.Join(
            Environment.NewLine,
            ReadRepositoryText("agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs"),
            ReadRepositoryText("agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.NyxRelayStreaming.cs"),
            ReadRepositoryText("agents/Aevatar.GAgents.Channel.Runtime/protos/conversation_events.proto"));

        File.Exists(Path.Combine(
                GetRepositoryRoot(),
                "agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.LarkCardStreaming.cs"))
            .ShouldBeFalse();
        productionSource.ShouldNotContain("_nyxRelayReplyTokens");
        productionSource.ShouldNotContain("NyxRelayReplyTokenCleanupRequestedEvent");
        productionSource.ShouldNotContain("HandleNyxRelayReplyTokenCleanupRequestedAsync");
        productionSource.ShouldNotContain("RemoveNyxRelayReplyToken");
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenDuplicateActivityId_CollapsesToSingleCommit()
    {
        var runner = new RecordingTurnRunner();
        var (agent, store) = await CreateAgentAsync(runner, "conv-1");

        var activity = CreateActivity("act-1", "conv:slack:C1");
        await agent.HandleInboundActivityAsync(activity);
        await agent.HandleInboundActivityAsync(activity.Clone());

        runner.InboundCount.ShouldBe(1);
        agent.State.ProcessedMessageIds.ShouldBe(new[] { "act-1" });
        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task HandleInboundActivityAsync_SequentialDistinctActivities_CommitAtomicallyInOrder()
    {
        var runner = new RecordingTurnRunner();
        var (agent, store) = await CreateAgentAsync(runner, "conv-2");

        await agent.HandleInboundActivityAsync(CreateActivity("act-1", "conv:slack:C1"));
        await agent.HandleInboundActivityAsync(CreateActivity("act-2", "conv:slack:C1"));
        await agent.HandleInboundActivityAsync(CreateActivity("act-3", "conv:slack:C1"));

        runner.InboundCount.ShouldBe(3);
        agent.State.ProcessedMessageIds.ShouldBe(new[] { "act-1", "act-2", "act-3" });
        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(3);
        events.Select(e => e.Version).ShouldBe(new long[] { 1, 2, 3 });
    }

    [Fact]
    public async Task HandleInboundActivityAsync_ActivityRedeliveredAfterCommit_UsesStateGuardNotRunner()
    {
        // TOCTOU scenario: the pipeline-level fast-path check may have missed the dedup entry
        // because redelivery arrived during a concurrent commit window. The grain's post-commit
        // state must still reject the duplicate without invoking the turn runner a second time.
        var runner = new RecordingTurnRunner();
        var (agent, _) = await CreateAgentAsync(runner, "conv-3");

        await agent.HandleInboundActivityAsync(CreateActivity("act-redeliver", "conv:slack:C1"));
        runner.InboundCount.ShouldBe(1);

        // Simulate a stream provider redelivering the same activity after the first commit landed.
        await agent.HandleInboundActivityAsync(CreateActivity("act-redeliver", "conv:slack:C1"));
        runner.InboundCount.ShouldBe(1);
    }

    [Fact]
    public async Task HandleContinueCommandAsync_WhenDuplicateCommandId_EmitsDuplicateCommandRejection()
    {
        var runner = new RecordingTurnRunner();
        var (agent, store) = await CreateAgentAsync(runner, "conv-4");

        var cmd = CreateContinueCommand("cmd-1");
        await agent.HandleContinueCommandAsync(cmd);
        await agent.HandleContinueCommandAsync(cmd.Clone());

        runner.ContinueCount.ShouldBe(1);
        agent.State.ProcessedCommandIds.ShouldContain("cmd-1");

        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(3);
        events.Count(e => e.EventType.Contains(nameof(DeliveryProducedEvent), StringComparison.Ordinal))
            .ShouldBe(1);

        var rejected = events.Last();
        rejected.EventType.ShouldContain(nameof(ConversationContinueRejectedEvent));
        var parsed = ConversationContinueRejectedEvent.Parser.ParseFrom(rejected.EventData.Value);
        parsed.Reason.ShouldBe(RejectReason.DuplicateCommand);
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenCapExceeded_RemovesOldestDedupEntry()
    {
        var runner = new RecordingTurnRunner();
        var (agent, _) = await CreateAgentAsync(runner, "conv-5");

        // Seed the state with cap - 1 entries, then add two more so the sliding window triggers.
        for (var i = 0; i < ConversationGAgent.ProcessedIdsCap; i++)
            agent.State.ProcessedMessageIds.Add($"seed-{i}");

        await agent.HandleInboundActivityAsync(CreateActivity("new-1", "conv:slack:C1"));
        await agent.HandleInboundActivityAsync(CreateActivity("new-2", "conv:slack:C1"));

        agent.State.ProcessedMessageIds.Count.ShouldBe(ConversationGAgent.ProcessedIdsCap);
        agent.State.ProcessedMessageIds.ShouldNotContain("seed-0");
        agent.State.ProcessedMessageIds.ShouldNotContain("seed-1");
        agent.State.ProcessedMessageIds.ShouldContain("new-1");
        agent.State.ProcessedMessageIds.ShouldContain("new-2");
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenRunnerReportsTransientFailure_SchedulesGrainOwnedRetry()
    {
        // Grain-level retry pattern (issue #399): a transient inbound-turn failure must land as
        // an InboundTurnRetryScheduledEvent with a bounded retry count rather than a leaf
        // ConversationContinueFailedEvent, because the webhook adapter no longer surfaces a
        // retryable 503 back to NyxID and the end-user reply would otherwise be dropped.
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = _ => ConversationTurnResult.TransientFailure("rate_limited", "retry later", TimeSpan.FromMilliseconds(250)),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-6");

        await agent.HandleInboundActivityAsync(CreateActivity("act-fail", "conv:slack:C1"));

        agent.State.ProcessedMessageIds.ShouldBeEmpty();
        agent.State.PendingInboundTurns.ShouldContain(entry => entry.ActivityId == "act-fail");
        var pending = agent.State.PendingInboundTurns.Single(entry => entry.ActivityId == "act-fail");
        pending.RetryCount.ShouldBe(1);
        pending.FirstFailedUnixMs.ShouldBeGreaterThan(0);
        pending.NextRetryUnixMs.ShouldBeGreaterThan(pending.FirstFailedUnixMs);

        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(InboundTurnRetryScheduledEvent));
        var parsed = InboundTurnRetryScheduledEvent.Parser.ParseFrom(events[0].EventData.Value);
        parsed.ActivityId.ShouldBe("act-fail");
        parsed.RetryCount.ShouldBe(1);
        parsed.Activity.Id.ShouldBe("act-fail");
    }

    [Fact]
    public async Task HandleDeferredInboundTurnRetryRequestedAsync_AfterTransientFailure_RerunsTurnAndClearsPendingOnSuccess()
    {
        // Issue #399 success path: once the adapter recovers, the durable reminder fires the
        // retry, the runner returns a proper ConversationTurnResult.Sent, and the pending entry
        // is reaped by ApplyTurnCompleted via ProcessedActivityId.
        var callCount = 0;
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = _ =>
            {
                callCount++;
                if (callCount == 1)
                    return ConversationTurnResult.TransientFailure("rate_limited", "retry later");
                return ConversationTurnResult.Sent(
                    "sent:act-retry-success",
                    new MessageContent { Text = "ok" },
                    "bot");
            },
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-retry-success");

        await agent.HandleInboundActivityAsync(CreateActivity("act-retry-success", "conv:slack:C1"));
        agent.State.PendingInboundTurns.ShouldContain(entry => entry.ActivityId == "act-retry-success");

        await agent.HandleDeferredInboundTurnRetryRequestedAsync(new DeferredInboundTurnRetryRequestedEvent
        {
            ActivityId = "act-retry-success",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        runner.InboundCount.ShouldBe(2);
        agent.State.ProcessedMessageIds.ShouldContain("act-retry-success");
        agent.State.PendingInboundTurns.ShouldNotContain(entry => entry.ActivityId == "act-retry-success");

        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(2);
        events[0].EventType.ShouldContain(nameof(InboundTurnRetryScheduledEvent));
        events[1].EventType.ShouldContain(nameof(ConversationTurnCompletedEvent));
    }

    [Fact]
    public async Task HandleDeferredInboundTurnRetryRequestedAsync_WhenRetriesExhausted_EmitsNotRetryableTerminalFailure()
    {
        // Issue #399 exhaustion path: after MaxInboundTurnRetryCount successive transient
        // failures, the actor persists a terminal NotRetryable ConversationContinueFailedEvent
        // so the pending set does not leak and downstream observers see a final state.
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = _ => ConversationTurnResult.TransientFailure("stuck", "persistent transient error"),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-retry-exhaust");

        await agent.HandleInboundActivityAsync(CreateActivity("act-exhaust", "conv:slack:C1"));
        agent.State.PendingInboundTurns.Single(e => e.ActivityId == "act-exhaust").RetryCount.ShouldBe(1);

        // Fire MaxInboundTurnRetryCount - 1 retries, each bumps the retry count but stays pending.
        for (var i = 0; i < ConversationGAgent.MaxInboundTurnRetryCount - 1; i++)
        {
            await agent.HandleDeferredInboundTurnRetryRequestedAsync(new DeferredInboundTurnRetryRequestedEvent
            {
                ActivityId = "act-exhaust",
                RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
        agent.State.PendingInboundTurns.Single(e => e.ActivityId == "act-exhaust").RetryCount
            .ShouldBe(ConversationGAgent.MaxInboundTurnRetryCount);

        // One more retry pushes retry_count past the cap; the actor emits a terminal failure
        // and reaps the pending entry.
        await agent.HandleDeferredInboundTurnRetryRequestedAsync(new DeferredInboundTurnRetryRequestedEvent
        {
            ActivityId = "act-exhaust",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        runner.InboundCount.ShouldBe(ConversationGAgent.MaxInboundTurnRetryCount + 1);
        agent.State.PendingInboundTurns.ShouldNotContain(entry => entry.ActivityId == "act-exhaust");

        var events = await store.GetEventsAsync(agent.Id);
        events.Last().EventType.ShouldContain(nameof(ConversationContinueFailedEvent));
        var terminal = ConversationContinueFailedEvent.Parser.ParseFrom(events.Last().EventData.Value);
        terminal.CorrelationId.ShouldBe("act-exhaust");
        terminal.Kind.ShouldBe(FailureKind.TransientAdapterError);
        terminal.RetryPolicyCase.ShouldBe(ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task ApplyLlmReplyRequested_AfterTransientFailureRetryPending_ReapsPendingInboundTurn()
    {
        // Codex review on #399 retry: a transient-failed activity that later succeeds via
        // redelivery on the LLM reply path must reap the pending retry entry. Without this,
        // the deferred retry would find the stale pending entry, hit the dedup guard, and
        // silently no-op — but the entry would survive to be re-registered on every
        // activation, growing PendingInboundTurns unboundedly.
        var callCount = 0;
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity =>
            {
                callCount++;
                if (callCount == 1)
                    return ConversationTurnResult.TransientFailure("rate_limited", "retry later");
                return ConversationTurnResult.LlmReplyRequested(CreateNeedsLlmReply(activity, requestedAtUnixMs: 7));
            },
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-llm-supersedes-retry");

        await agent.HandleInboundActivityAsync(CreateActivity("act-llm-supersedes", "conv:slack:C1"));
        agent.State.PendingInboundTurns.ShouldContain(entry => entry.ActivityId == "act-llm-supersedes");

        // Redelivery hits the LLM reply branch; ApplyLlmReplyRequested must reap the pending
        // entry alongside adding the activity id to ProcessedMessageIds.
        await agent.HandleInboundActivityAsync(CreateActivity("act-llm-supersedes", "conv:slack:C1"));

        runner.InboundCount.ShouldBe(2);
        agent.State.ProcessedMessageIds.ShouldContain("act-llm-supersedes");
        agent.State.PendingInboundTurns.ShouldNotContain(entry => entry.ActivityId == "act-llm-supersedes");

        var eventsAfterRedelivery = await store.GetEventsAsync(agent.Id);

        // The deferred retry that was scheduled on the first delivery now fires. With the
        // pending entry already reaped, the handler is a true no-op: no runner invocation,
        // no further events persisted, and PendingInboundTurns stays empty.
        await agent.HandleDeferredInboundTurnRetryRequestedAsync(new DeferredInboundTurnRetryRequestedEvent
        {
            ActivityId = "act-llm-supersedes",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        runner.InboundCount.ShouldBe(2);
        agent.State.PendingInboundTurns.ShouldNotContain(entry => entry.ActivityId == "act-llm-supersedes");
        var eventsAfterRetryFire = await store.GetEventsAsync(agent.Id);
        eventsAfterRetryFire.Count.ShouldBe(eventsAfterRedelivery.Count);
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenRunnerReportsPermanentFailure_EmitsTerminalWithoutScheduling()
    {
        // Issue #399 non-regression: permanent-adapter failures must skip the retry pipeline and
        // land as terminal ConversationContinueFailedEvent with NotRetryable semantics, as before.
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = _ => ConversationTurnResult.PermanentFailure("bad_input", "rejected"),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-permanent-inbound");

        await agent.HandleInboundActivityAsync(CreateActivity("act-permanent", "conv:slack:C1"));

        agent.State.PendingInboundTurns.ShouldBeEmpty();
        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(ConversationContinueFailedEvent));
        var parsed = ConversationContinueFailedEvent.Parser.ParseFrom(events[0].EventData.Value);
        parsed.Kind.ShouldBe(FailureKind.PermanentAdapterError);
        parsed.RetryPolicyCase.ShouldBe(ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task HandleInboundActivityAsync_PersistsOutboundDeliveryReceipt_OnCompletedEvent()
    {
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = _ => ConversationTurnResult.Sent(
                "sent:act-relay",
                new MessageContent { Text = "ack" },
                "bot",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-1",
                    CorrelationId = "corr-relay-1",
                }),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-relay");

        await agent.HandleInboundActivityAsync(CreateActivity("act-relay", "conv:slack:C1"));

        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        var completed = ConversationTurnCompletedEvent.Parser.ParseFrom(events[0].EventData.Value);
        completed.OutboundDelivery.ReplyMessageId.ShouldBe("relay-msg-1");
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenRunnerRequestsDeferredReply_PersistsNeedsLlmReplyEvent()
    {
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                CreateNeedsLlmReply(activity, requestedAtUnixMs: 42)),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-llm-request");

        await agent.HandleInboundActivityAsync(CreateActivity("act-llm", "conv:slack:C1"));

        agent.State.ProcessedMessageIds.ShouldContain("act-llm");
        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(NeedsLlmReplyEvent));
        var parsed = NeedsLlmReplyEvent.Parser.ParseFrom(events[0].EventData.Value);
        parsed.CorrelationId.ShouldBe("act-llm");
        parsed.RunId.ShouldBe("act-llm");
        parsed.Activity.Id.ShouldBe("act-llm");
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenRecentAcceptedActivityHadAttachment_CopiesWindowToRunCommandOnly()
    {
        var requestedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                CreateNeedsLlmReply(activity, requestedAtUnixMs: requestedAt++)),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-recent-attachments", dispatcher);

        await agent.HandleInboundActivityAsync(CreateLarkImageActivity(
            "act-image",
            "image only",
            "lark:scope-a:chat-1",
            "om_image",
            "img_key",
            "runtime-token-1"));
        await agent.HandleInboundActivityAsync(CreateLarkActivity(
            "act-follow-up",
            "what is in the image?",
            "lark:scope-a:chat-1",
            "om_follow_up",
            "runtime-token-2"));

        dispatcher.Dispatched.Count.ShouldBe(2);
        dispatcher.Dispatched[0].RecentAttachmentActivities.ShouldBeEmpty();

        var recent = dispatcher.Dispatched[1].RecentAttachmentActivities.ShouldHaveSingleItem();
        recent.ActivityId.ShouldBe("act-image");
        recent.Activity.Content.Attachments.ShouldHaveSingleItem().AttachmentId.ShouldBe("img_key");
        recent.Activity.TransportExtras.NyxPlatformMessageId.ShouldBe("om_image");
        recent.Activity.TransportExtras.NyxUserAccessToken.ShouldBeEmpty(
            "conversation-owned durable attachment snapshots must not persist relay user credentials");

        agent.State.RecentAttachmentActivities.Select(entry => entry.ActivityId)
            .ShouldContain("act-image");

        var persisted = (await store.GetEventsAsync(agent.Id))
            .Where(record => record.EventType.Contains(nameof(NeedsLlmReplyEvent), StringComparison.Ordinal))
            .Select(record => NeedsLlmReplyEvent.Parser.ParseFrom(record.EventData.Value))
            .Last();
        persisted.RecentAttachmentActivities.ShouldBeEmpty(
            "recent attachment snapshots are transient run input and not duplicated into pending LLM request state");
    }

    [Fact]
    public async Task HandleNyxRelayInboundActivityAsync_WithCallbackJti_PersistsAdmissionBeforeRunner()
    {
        var observedAtMs = DateTimeOffset.UtcNow.AddSeconds(-17).ToUnixTimeMilliseconds();
        const string sentinelUserAccessToken = "sentinel-user-access-token-must-not-persist";
        var publisher = new RecordingEventPublisher();
        var runner = new RecordingTurnRunner();
        var (agent, store) = await CreateAgentAsync(runner, "conv-relay-admit-first", eventPublisher: publisher);

        var relay = CreateRelayInbound(
            "act-admit",
            "conv:slack:C1",
            "api-key-1",
            "jti-1",
            sentinelUserAccessToken,
            observedAtMs);
        await agent.HandleNyxRelayInboundActivityAsync(relay);

        runner.InboundCount.ShouldBe(0);
        agent.State.ProcessedMessageIds.ShouldBeEmpty();
        agent.State.RelayReplayClaims.ShouldContain(claim =>
            claim.RelayApiKeyId == "api-key-1" && claim.CallbackJti == "jti-1");
        var pendingAdmission = agent.State.PendingRelayAdmissions.Single(admission => admission.ActivityId == "act-admit");
        pendingAdmission.AdmittedAtUnixMs.ShouldBe(observedAtMs);
        pendingAdmission.Activity.TransportExtras.NyxUserAccessToken.ShouldBeEmpty();

        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(NyxRelayCallbackAdmittedEvent));
        var admitted = NyxRelayCallbackAdmittedEvent.Parser.ParseFrom(events[0].EventData.Value);
        admitted.AdmittedAtUnixMs.ShouldBe(observedAtMs);
        admitted.Activity.TransportExtras.NyxUserAccessToken.ShouldBeEmpty();
        ContainsSubsequence(events[0].EventData.Value.ToByteArray(), Encoding.UTF8.GetBytes(sentinelUserAccessToken))
            .ShouldBeFalse("relay user access token must stay out of persisted admission event bytes");
        publisher.Sent.ShouldContain(message => message is NyxRelayCallbackTurnRequestedEvent);
    }

    [Fact]
    public void HandleNyxRelayCallbackTurnRequestedAsync_MustOptInToSelfHandling()
    {
        // Regression guard for the 2026-05-21 prod Lark outage. The admit handler self-sends
        // NyxRelayCallbackTurnRequestedEvent via SendToAsync(Id, ...); EventHandlerAttribute
        // defaults AllowSelfHandling=false, so a bare [EventHandler] causes the EventPublisher
        // pipeline (StaticHandlerAdapter) to silently drop the envelope when
        // PublisherActorId == this.Id and the turn never fires. The RecordingEventPublisher
        // used by the other tests in this file only records and does not dispatch, so
        // behavioral tests cannot catch this attribute drift — this reflection-level
        // assertion is the cheapest reliable regression marker.
        //
        // Do NOT also assert OnlySelfHandling=true here: that flag gates by envelope
        // TopologyAudience (must be Self), but SendToAsync(Id, ...) produces a Direct route
        // whose audience reads back as Unspecified, so enabling OnlySelfHandling would
        // re-filter the same envelope we are admitting.
        var method = typeof(ConversationGAgent).GetMethod(
            nameof(ConversationGAgent.HandleNyxRelayCallbackTurnRequestedAsync),
            BindingFlags.Instance | BindingFlags.Public);
        method.ShouldNotBeNull();
        var attr = method!.GetCustomAttribute<EventHandlerAttribute>();
        attr.ShouldNotBeNull(
            "HandleNyxRelayCallbackTurnRequestedAsync must be decorated with [EventHandler].");
        attr!.AllowSelfHandling.ShouldBeTrue(
            "Self-sent NyxRelayCallbackTurnRequestedEvent requires AllowSelfHandling=true; " +
            "without it the pipeline drops the envelope and Lark/Telegram bots stop replying.");
        attr.OnlySelfHandling.ShouldBeFalse(
            "OnlySelfHandling must stay false: it gates by envelope TopologyAudience.Self, " +
            "but SendToAsync(Id, ...) produces a Direct-route envelope whose audience is " +
            "Unspecified, so enabling this flag would re-drop the admitted event.");
    }

    [Theory]
    [InlineData(nameof(ConversationGAgent.HandleDeferredLlmReplyDispatchRequestedAsync), true)]
    [InlineData(nameof(ConversationGAgent.HandleDeferredInboundTurnRetryRequestedAsync), true)]
    [InlineData(nameof(ConversationGAgent.HandleNyxRelayTextOperationCompletedAsync), false)]
    [InlineData(nameof(ConversationGAgent.HandleNyxRelayTextOperationTimeoutFiredAsync), true)]
    public void ConversationSelfContinuationHandlers_MustOptInToSelfHandling(
        string handlerName,
        bool selfAudience)
    {
        var method = typeof(ConversationGAgent).GetMethod(
            handlerName,
            BindingFlags.Instance | BindingFlags.Public);
        method.ShouldNotBeNull();

        var attr = method!.GetCustomAttribute<EventHandlerAttribute>();
        attr.ShouldNotBeNull($"{handlerName} must be decorated with [EventHandler].");
        attr!.AllowSelfHandling.ShouldBeTrue(
            $"{handlerName} handles an actor-owned continuation or timeout whose " +
            "PublisherActorId is the conversation actor itself.");
        attr.OnlySelfHandling.ShouldBeFalse(
            selfAudience
                ? $"{handlerName} must currently allow runtime callback envelopes whose route shape may vary by scheduler."
                : $"{handlerName} receives Direct self-dispatch envelopes, so OnlySelfHandling would filter it.");
    }

    [Fact]
    public async Task HandleNyxRelayCallbackTurnRequestedAsync_WithSanitizedAdmission_RestoresRuntimeTokenOnlyForTurn()
    {
        const string sentinelUserAccessToken = "sentinel-user-access-token-runtime-only";
        var runner = new RecordingTurnRunner();
        var (agent, store) = await CreateAgentAsync(runner, "conv-relay-runtime-token");
        var relay = CreateRelayInbound(
            "act-runtime-token",
            "conv:lark:C1",
            "api-key-1",
            "jti-runtime-token",
            sentinelUserAccessToken);

        await agent.HandleNyxRelayInboundActivityAsync(relay);
        await agent.HandleNyxRelayCallbackTurnRequestedAsync(new NyxRelayCallbackTurnRequestedEvent
        {
            ActivityId = "act-runtime-token",
            RelayApiKeyId = "api-key-1",
            CallbackJti = "jti-runtime-token",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyToken = relay.ReplyToken,
            ReplyTokenExpiresAtUnixMs = relay.ReplyTokenExpiresAtUnixMs,
            NyxUserAccessToken = sentinelUserAccessToken,
        });

        runner.InboundCount.ShouldBe(1);
        runner.LastInboundActivity?.TransportExtras?.NyxUserAccessToken.ShouldBe(sentinelUserAccessToken);
        runner.LastInboundRuntimeContext?.NyxUserAccessToken.ShouldBe(sentinelUserAccessToken);
        agent.State.PendingRelayAdmissions.ShouldBeEmpty();

        var events = await store.GetEventsAsync(agent.Id);
        var sentinelBytes = Encoding.UTF8.GetBytes(sentinelUserAccessToken);
        foreach (var record in events)
        {
            ContainsSubsequence(record.EventData.Value.ToByteArray(), sentinelBytes)
                .ShouldBeFalse($"persisted event {record.EventType} must not contain Nyx user access token bytes");
        }
    }

    [Fact]
    public async Task HandleNyxRelayInboundActivityAsync_DuplicateCallbackJti_NoopsBeforeProcessedMessageIds()
    {
        var runner = new RecordingTurnRunner();
        var (agent, store) = await CreateAgentAsync(runner, "conv-relay-duplicate-admission");
        var relay = CreateRelayInbound("act-dup", "conv:slack:C1", "api-key-1", "jti-dup");

        await agent.HandleNyxRelayInboundActivityAsync(relay);
        await agent.HandleNyxRelayInboundActivityAsync(relay.Clone());

        runner.InboundCount.ShouldBe(0);
        agent.State.ProcessedMessageIds.ShouldBeEmpty();
        agent.State.RelayReplayClaims.Count.ShouldBe(1);
        agent.State.PendingRelayAdmissions.Count.ShouldBe(1);
        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(NyxRelayCallbackAdmittedEvent));
    }

    [Fact]
    public async Task HandleNyxRelayInboundActivityAsync_ExpiredCallbackClaim_AllowsFreshAdmission()
    {
        var store = new InMemoryEventStore();
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        var originalActivity = CreateActivity("act-expired-original", "conv:slack:C1");
        await AppendStateEventAsync(
            store,
            "conv-relay-expired-claim",
            new NyxRelayCallbackAdmittedEvent
            {
                ActivityId = originalActivity.Id,
                RelayApiKeyId = "api-key-expired",
                CallbackJti = "jti-expired",
                Activity = originalActivity,
                AdmittedAtUnixMs = expiredAt - 1000,
                ClaimExpiresAtUnixMs = expiredAt,
            },
            version: 1);

        var publisher = new RecordingEventPublisher();
        var (agent, _) = await CreateAgentAsync(
            new RecordingTurnRunner(),
            "conv-relay-expired-claim",
            store: store,
            eventPublisher: publisher);
        agent.State.RelayReplayClaims.ShouldContain(claim =>
            claim.RelayApiKeyId == "api-key-expired" &&
            claim.CallbackJti == "jti-expired" &&
            claim.ActivityId == "act-expired-original");

        await agent.HandleNyxRelayInboundActivityAsync(
            CreateRelayInbound("act-expired-fresh", "conv:slack:C1", "api-key-expired", "jti-expired"));

        agent.State.RelayReplayClaims.ShouldContain(claim =>
            claim.RelayApiKeyId == "api-key-expired" &&
            claim.CallbackJti == "jti-expired" &&
            claim.ActivityId == "act-expired-fresh" &&
            claim.ExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        agent.State.RelayReplayClaims.ShouldNotContain(claim => claim.ActivityId == "act-expired-original");
        agent.State.PendingRelayAdmissions.ShouldContain(admission => admission.ActivityId == "act-expired-fresh");
        publisher.Sent
            .OfType<NyxRelayCallbackTurnRequestedEvent>()
            .ShouldContain(request => request.ActivityId == "act-expired-fresh");

        var events = await store.GetEventsAsync(agent.Id);
        events.Count(e => e.EventType.Contains(nameof(NyxRelayCallbackAdmittedEvent), StringComparison.Ordinal))
            .ShouldBe(2);
    }

    [Fact]
    public async Task HandleNyxRelayCallbackTurnRequestedAsync_TransientFailure_ExactReplayCallsRunnerOnceAndSchedulesOneRetry()
    {
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = _ => ConversationTurnResult.TransientFailure("rate_limited", "retry later"),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-relay-transient-replay");
        var relay = CreateRelayInbound("act-transient-replay", "conv:slack:C1", "api-key-1", "jti-transient");

        await agent.HandleNyxRelayInboundActivityAsync(relay);
        await agent.HandleNyxRelayCallbackTurnRequestedAsync(new NyxRelayCallbackTurnRequestedEvent
        {
            ActivityId = "act-transient-replay",
            RelayApiKeyId = "api-key-1",
            CallbackJti = "jti-transient",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await agent.HandleNyxRelayInboundActivityAsync(relay.Clone());

        runner.InboundCount.ShouldBe(1);
        agent.State.PendingRelayAdmissions.ShouldBeEmpty();
        agent.State.PendingInboundTurns.ShouldContain(entry => entry.ActivityId == "act-transient-replay");
        var events = await store.GetEventsAsync(agent.Id);
        events.Count(e => e.EventType.Contains(nameof(NyxRelayCallbackAdmittedEvent), StringComparison.Ordinal)).ShouldBe(1);
        events.Count(e => e.EventType.Contains(nameof(InboundTurnRetryScheduledEvent), StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task HandleNyxRelayCallbackTurnRequestedAsync_TerminalFailure_ExactReplayEmitsOneFailure()
    {
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = _ => ConversationTurnResult.PermanentFailure("bad_payload", "rejected"),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-relay-terminal-replay");
        var relay = CreateRelayInbound("act-terminal-replay", "conv:slack:C1", "api-key-1", "jti-terminal");

        await agent.HandleNyxRelayInboundActivityAsync(relay);
        await agent.HandleNyxRelayCallbackTurnRequestedAsync(new NyxRelayCallbackTurnRequestedEvent
        {
            ActivityId = "act-terminal-replay",
            RelayApiKeyId = "api-key-1",
            CallbackJti = "jti-terminal",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await agent.HandleNyxRelayInboundActivityAsync(relay.Clone());

        runner.InboundCount.ShouldBe(1);
        agent.State.PendingRelayAdmissions.ShouldBeEmpty();
        var events = await store.GetEventsAsync(agent.Id);
        events.Count(e => e.EventType.Contains(nameof(ConversationContinueFailedEvent), StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task HandleNyxRelayCallbackTurnRequestedAsync_SuccessAndLlmHandoff_ReapPendingAdmission()
    {
        var runner = new RecordingTurnRunner();
        var (successAgent, _) = await CreateAgentAsync(runner, "conv-relay-success-reap");
        var successRelay = CreateRelayInbound("act-success-reap", "conv:slack:C1", "api-key-1", "jti-success");
        await successAgent.HandleNyxRelayInboundActivityAsync(successRelay);
        await successAgent.HandleNyxRelayCallbackTurnRequestedAsync(new NyxRelayCallbackTurnRequestedEvent
        {
            ActivityId = "act-success-reap",
            RelayApiKeyId = "api-key-1",
            CallbackJti = "jti-success",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        successAgent.State.PendingRelayAdmissions.ShouldBeEmpty();

        var llmRunner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(CreateNeedsLlmReply(activity)),
        };
        var (llmAgent, _) = await CreateAgentAsync(llmRunner, "conv-relay-llm-reap");
        var llmRelay = CreateRelayInbound("act-llm-reap", "conv:slack:C1", "api-key-1", "jti-llm");
        await llmAgent.HandleNyxRelayInboundActivityAsync(llmRelay);
        await llmAgent.HandleNyxRelayCallbackTurnRequestedAsync(new NyxRelayCallbackTurnRequestedEvent
        {
            ActivityId = "act-llm-reap",
            RelayApiKeyId = "api-key-1",
            CallbackJti = "jti-llm",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        llmAgent.State.PendingRelayAdmissions.ShouldBeEmpty();
        llmAgent.State.PendingLlmReplyRequests.ShouldContain(request => request.CorrelationId == "jti-llm");
    }

    [Fact]
    public async Task ActivateAsync_WithPendingRelayAdmission_RedispatchesSelfContinuation()
    {
        var store = new InMemoryEventStore();
        var firstPublisher = new RecordingEventPublisher();
        var (firstAgent, _) = await CreateAgentAsync(
            new RecordingTurnRunner(),
            "conv-relay-rehydrate",
            store: store,
            eventPublisher: firstPublisher);

        await firstAgent.HandleNyxRelayInboundActivityAsync(
            CreateRelayInbound("act-rehydrate", "conv:slack:C1", "api-key-1", "jti-rehydrate"));

        var secondPublisher = new RecordingEventPublisher();
        var (rehydrated, _) = await CreateAgentAsync(
            new RecordingTurnRunner(),
            "conv-relay-rehydrate",
            store: store,
            eventPublisher: secondPublisher);

        rehydrated.State.PendingRelayAdmissions.ShouldContain(admission => admission.ActivityId == "act-rehydrate");
        secondPublisher.Sent
            .OfType<NyxRelayCallbackTurnRequestedEvent>()
            .ShouldContain(requested =>
                requested.ActivityId == "act-rehydrate" &&
                requested.CallbackJti == "jti-rehydrate");
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenRunDispatcherAcceptsRequest_ShouldNotPersistCompletedReplyUntilReadyArrives()
    {
        // Accepted-for-run is weaker than committed/user-visible reply. The actor may persist
        // NeedsLlmReplyEvent and dispatch it immediately, but must not emit a completed fact
        // until the run actor sends LlmReplyReadyEvent (or a terminal failure) back.
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(CreateNeedsLlmReply(activity)),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-accepted-not-committed", dispatcher);

        await agent.HandleInboundActivityAsync(CreateActivity("act-accepted-only", "conv:slack:C1"));

        dispatcher.Dispatched.Count.ShouldBe(1);
        runner.LlmReplyCount.ShouldBe(0);
        agent.State.PendingLlmReplyRequests.ShouldContain(req => req.CorrelationId == "act-accepted-only");

        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(NeedsLlmReplyEvent));
        events.ShouldNotContain(record =>
            record.EventType.Contains(nameof(ConversationTurnCompletedEvent), StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleInboundAndReadyAsync_WhenSameConversationRunsTwice_InjectsAndRetainsPreviousHistory()
    {
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.Id,
                    RunId = activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = 42,
                }),
        };
        var (agent, _) = await CreateAgentAsync(runner, "conv-lark-history", dispatcher);

        await agent.HandleInboundActivityAsync(CreateActivity("act-history-1", "lark:scope-a:chat-1"));
        var firstReady = new LlmReplyReadyEvent
        {
            CorrelationId = "act-history-1",
            RegistrationId = "reg-1",
            SourceActorId = "run-1",
            Activity = CreateActivity("act-history-1", "lark:scope-a:chat-1"),
            Outbound = new MessageContent { Text = "first assistant" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 43,
        };
        firstReady.AppendedHistory.Add(new ConversationHistoryEntry { Role = "user", Content = "first user" });
        firstReady.AppendedHistory.Add(new ConversationHistoryEntry { Role = "assistant", Content = "first assistant" });
        await agent.HandleLlmReplyReadyAsync(firstReady);

        await agent.HandleInboundActivityAsync(CreateActivity("act-history-2", "lark:scope-a:chat-1"));

        agent.State.RetainedHistory.Select(entry => (entry.Role, entry.Content))
            .ShouldContain(("user", "first user"));
        agent.State.RetainedHistory.Select(entry => (entry.Role, entry.Content))
            .ShouldContain(("assistant", "first assistant"));
        dispatcher.Dispatched.Count.ShouldBe(2);
        dispatcher.Dispatched[0].PriorHistory.ShouldBeEmpty();
        dispatcher.Dispatched[1].PriorHistory.Select(entry => (entry.Role, entry.Content))
            .ShouldContain(("user", "first user"));
        dispatcher.Dispatched[1].PriorHistory.Select(entry => (entry.Role, entry.Content))
            .ShouldContain(("assistant", "first assistant"));
    }

    [Fact]
    public async Task HandleInboundAndReadyAsync_WhenRetainedHistoryExceedsCap_DoesNotKeepOrphanToolResult()
    {
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.Id,
                    RunId = activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = 42,
                }),
        };
        var (agent, _) = await CreateAgentAsync(runner, "conv-lark-history-cap", dispatcher);

        await agent.HandleInboundActivityAsync(CreateActivity("act-history-cap-1", "lark:scope-a:chat-cap"));
        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-history-cap-1",
            RegistrationId = "reg-1",
            SourceActorId = "run-cap-1",
            Activity = CreateActivity("act-history-cap-1", "lark:scope-a:chat-cap"),
            Outbound = new MessageContent { Text = "latest assistant" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 43,
        };

        ready.AppendedHistory.Add(new ConversationHistoryEntry
        {
            Role = "assistant",
            Content = "old assistant tool call",
            ToolCalls =
            {
                new ConversationToolCallEntry
                {
                    Id = "old-call",
                    Name = "search",
                    ArgumentsJson = "{}",
                },
            },
        });
        ready.AppendedHistory.Add(new ConversationHistoryEntry
        {
            Role = "tool",
            ToolCallId = "old-call",
            Content = "old result",
        });
        for (var i = 0; i < 100; i++)
        {
            ready.AppendedHistory.Add(new ConversationHistoryEntry
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"recent {i}",
            });
        }

        await agent.HandleLlmReplyReadyAsync(ready);
        await agent.HandleInboundActivityAsync(CreateActivity("act-history-cap-2", "lark:scope-a:chat-cap"));

        agent.State.RetainedHistory.Count.ShouldBeLessThanOrEqualTo(100);
        agent.State.RetainedHistory.ShouldNotContain(entry => entry.Role == "tool" && entry.ToolCallId == "old-call");
        dispatcher.Dispatched.Count.ShouldBe(2);
        dispatcher.Dispatched[1].PriorHistory.Count.ShouldBeLessThanOrEqualTo(100);
        dispatcher.Dispatched[1].PriorHistory.ShouldNotContain(entry => entry.Role == "tool" && entry.ToolCallId == "old-call");
    }

    [Fact]
    public async Task HandleInboundAndReadyAsync_WhenRetainedHistoryExceedsCap_PreservesCompleteToolPair()
    {
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.Id,
                    RunId = activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = 42,
                }),
        };
        var (agent, _) = await CreateAgentAsync(runner, "conv-lark-history-tool-pair", dispatcher);

        await agent.HandleInboundActivityAsync(CreateActivity("act-history-tool-pair-1", "lark:scope-a:chat-tool-pair"));
        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-history-tool-pair-1",
            RegistrationId = "reg-1",
            SourceActorId = "run-tool-pair-1",
            Activity = CreateActivity("act-history-tool-pair-1", "lark:scope-a:chat-tool-pair"),
            Outbound = new MessageContent { Text = "latest assistant" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 43,
        };

        for (var i = 0; i < 98; i++)
        {
            ready.AppendedHistory.Add(new ConversationHistoryEntry
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"older {i}",
            });
        }
        ready.AppendedHistory.Add(new ConversationHistoryEntry
        {
            Role = "assistant",
            Content = "kept assistant tool call",
            ToolCalls =
            {
                new ConversationToolCallEntry
                {
                    Id = "kept-call",
                    Name = "search",
                    ArgumentsJson = "{}",
                },
            },
        });
        ready.AppendedHistory.Add(new ConversationHistoryEntry
        {
            Role = "tool",
            ToolCallId = "kept-call",
            Content = "kept result",
        });
        ready.AppendedHistory.Add(new ConversationHistoryEntry { Role = "user", Content = "latest user" });

        await agent.HandleLlmReplyReadyAsync(ready);

        agent.State.RetainedHistory.Count.ShouldBeLessThanOrEqualTo(100);
        agent.State.RetainedHistory.ShouldContain(entry =>
            entry.Role == "assistant" && entry.ToolCalls.Any(call => call.Id == "kept-call"));
        agent.State.RetainedHistory.ShouldContain(entry => entry.Role == "tool" && entry.ToolCallId == "kept-call");
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenDifferentConversationActorRuns_DoesNotInjectOtherConversationHistory()
    {
        var firstDispatcher = new RecordingRunDispatcher();
        var secondDispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.Id,
                    RunId = activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = 42,
                }),
        };
        var (firstAgent, _) = await CreateAgentAsync(runner, "conv-lark-history-a", firstDispatcher);
        var (secondAgent, _) = await CreateAgentAsync(runner, "conv-lark-history-b", secondDispatcher);

        await firstAgent.HandleInboundActivityAsync(CreateActivity("act-history-a1", "lark:scope-a:chat-1"));
        var firstReady = new LlmReplyReadyEvent
        {
            CorrelationId = "act-history-a1",
            RegistrationId = "reg-1",
            SourceActorId = "run-a1",
            Activity = CreateActivity("act-history-a1", "lark:scope-a:chat-1"),
            Outbound = new MessageContent { Text = "scope a assistant" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 43,
        };
        firstReady.AppendedHistory.Add(new ConversationHistoryEntry { Role = "user", Content = "scope a user" });
        firstReady.AppendedHistory.Add(new ConversationHistoryEntry { Role = "assistant", Content = "scope a assistant" });
        await firstAgent.HandleLlmReplyReadyAsync(firstReady);

        await secondAgent.HandleInboundActivityAsync(CreateActivity("act-history-b1", "lark:scope-b:chat-1"));

        firstAgent.State.RetainedHistory.ShouldNotBeEmpty();
        secondAgent.State.RetainedHistory.ShouldBeEmpty();
        secondDispatcher.Dispatched.ShouldHaveSingleItem();
        secondDispatcher.Dispatched[0].PriorHistory.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenDuplicateCorrelationId_CollapsesToSingleOutboundCommit()
    {
        var runner = new RecordingTurnRunner();
        var (agent, store) = await CreateAgentAsync(runner, "conv-llm-ready");
        await agent.HandleInboundActivityAsync(CreateActivity("act-llm-ready", "conv:slack:C1"));

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-llm-ready",
            RegistrationId = "reg-1",
            RunId = "act-llm-ready",
            SourceActorId = "llm-worker-1",
            Activity = CreateActivity("act-llm-ready", "conv:slack:C1"),
            Outbound = new MessageContent { Text = "reply-from-llm" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 43,
        };

        await agent.HandleLlmReplyReadyAsync(ready);
        await agent.HandleLlmReplyReadyAsync(ready.Clone());

        runner.LlmReplyCount.ShouldBe(1);
        agent.State.ProcessedCommandIds.ShouldContain("llm:act-llm-ready");
        var events = await store.GetEventsAsync(agent.Id);
        // NeedsLlmReplyEvent + DeliveryProducedEvent + LlmReplyDeliveredEvent
        // (ADR-0021 chain.delivered) + ConversationTurnCompletedEvent.
        // Duplicate ready event must not add more.
        events.Count.ShouldBe(4);
        events.Count(e => e.EventType.Contains(nameof(DeliveryProducedEvent), StringComparison.Ordinal))
            .ShouldBe(1);
        events.Last().EventType.ShouldContain(nameof(ConversationTurnCompletedEvent));
        events.Select(e => e.EventType).ShouldContain(s => s.Contains(nameof(LlmReplyDeliveredEvent)));
        agent.State.LastReplyDelivery.RunId.ShouldBe("act-llm-ready");
        agent.State.LastReplyDelivery.OutcomeCase.ShouldBe(ReplyDeliveryStatus.OutcomeOneofCase.Delivered);
        agent.State.LastReplyDelivery.Delivered.ChannelMessageId.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenDeliverySucceeds_UpdatesLastReplyDeliveryState()
    {
        var runner = new RecordingTurnRunner
        {
            LlmReplyResultFactory = reply => ConversationTurnResult.Sent(
                "sent:llm:" + reply.CorrelationId,
                reply.Outbound?.Clone() ?? new MessageContent { Text = "ack" },
                "bot",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = "om_delivery_ok",
                    CorrelationId = reply.CorrelationId,
                }),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-llm-delivered");

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "corr-delivered",
            RegistrationId = "reg-1",
            RunId = "corr-delivered",
            SourceActorId = "agent-run",
            Activity = CreateActivity("corr-delivered", "conv:slack:C1"),
            Outbound = new MessageContent { Text = "reply-from-llm" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 43,
        });

        agent.State.LastReplyDelivery.RunId.ShouldBe("corr-delivered");
        agent.State.LastReplyDelivery.OutcomeCase.ShouldBe(ReplyDeliveryStatus.OutcomeOneofCase.Delivered);
        agent.State.LastReplyDelivery.Delivered.ChannelMessageId.ShouldBe("om_delivery_ok");
        agent.State.LastReplyDelivery.Delivered.AckedAtUnixMs.ShouldBeGreaterThan(0);

        var events = await store.GetEventsAsync(agent.Id);
        events.Select(e => e.EventType).ShouldContain(s => s.Contains(nameof(LlmReplyDeliveredEvent)));
        events.Count(e => e.EventData.Is(DeliveryProducedEvent.Descriptor)).ShouldBe(1);
        var deliveryRecord = events.Single(e => e.EventData.Is(DeliveryProducedEvent.Descriptor));
        var delivery = deliveryRecord.EventData.Unpack<DeliveryProducedEvent>();
        delivery.RunId.ShouldBe("corr-delivered");
        delivery.TurnId.ShouldBe("corr-delivered");
        delivery.DeliveryKind.ShouldBe(DeliveryKind.TextMessage);
        delivery.Status.ShouldBe(DeliveryStatus.Succeeded);
        delivery.ProducedAtVersion.ShouldBe(deliveryRecord.Version);
        delivery.RequestId.ShouldBe("llm:corr-delivered");
        delivery.SourceEventId.ShouldBe("corr-delivered");
        delivery.ProviderMessageId.ShouldBe("om_delivery_ok");
        delivery.CardId.ShouldBeEmpty();
        delivery.Target.Channel.Value.ShouldBe("slack");
        delivery.Target.ConversationKey.ShouldBe("conv:slack:C1");
        delivery.Target.Platform.ShouldBe("slack");
        delivery.Target.AddressId.ShouldBeEmpty();
        delivery.Target.AddressType.ShouldBeEmpty();
        delivery.Target.ConversationId.ShouldBe("conv:slack:C1");
        delivery.Target.ReplyMessageId.ShouldBeEmpty();
        var recentDelivery = agent.State.RecentDeliveries.ShouldHaveSingleItem();
        recentDelivery.RequestId.ShouldBe("llm:corr-delivered");
        recentDelivery.Status.ShouldBe(DeliveryStatus.Succeeded);
        recentDelivery.ProviderMessageId.ShouldBe("om_delivery_ok");
        agent.State.LastSuccessfulDelivery.ShouldNotBeNull();
        agent.State.LastSuccessfulDelivery!.RequestId.ShouldBe("llm:corr-delivered");
        agent.State.LastSuccessfulDelivery.ProviderMessageId.ShouldBe("om_delivery_ok");
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenDeliveryFails_UpdatesLastReplyDeliveryState()
    {
        var runner = new RecordingTurnRunner
        {
            LlmReplyResultFactory = _ => ConversationTurnResult.PermanentFailure("lark_send_failed", "lark rejected send"),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-llm-delivery-failed");

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "corr-delivery-failed",
            RegistrationId = "reg-1",
            RunId = "corr-delivery-failed",
            SourceActorId = "agent-run",
            Activity = CreateActivity("corr-delivery-failed", "conv:slack:C1"),
            Outbound = new MessageContent { Text = "reply-from-llm" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 43,
        });

        agent.State.LastReplyDelivery.RunId.ShouldBe("corr-delivery-failed");
        agent.State.LastReplyDelivery.OutcomeCase.ShouldBe(ReplyDeliveryStatus.OutcomeOneofCase.Failed);
        agent.State.LastReplyDelivery.Failed.ErrorCode.ShouldBe("lark_send_failed");
        agent.State.LastReplyDelivery.Failed.ErrorMessage.ShouldBe("lark rejected send");
        agent.State.LastReplyDelivery.Failed.FailedAtUnixMs.ShouldBeGreaterThan(0);

        var events = await store.GetEventsAsync(agent.Id);
        events.Select(e => e.EventType).ShouldContain(s => s.Contains(nameof(LlmReplyDeliveryFailedEvent)));
        events.Count(e => e.EventData.Is(DeliveryProducedEvent.Descriptor)).ShouldBe(1);
        var deliveryRecord = events.Single(e => e.EventData.Is(DeliveryProducedEvent.Descriptor));
        var delivery = deliveryRecord.EventData.Unpack<DeliveryProducedEvent>();
        delivery.DeliveryKind.ShouldBe(DeliveryKind.TextMessage);
        delivery.Status.ShouldBe(DeliveryStatus.FailedPreSend);
        delivery.ProducedAtVersion.ShouldBe(deliveryRecord.Version);
        delivery.RequestId.ShouldBe("llm:corr-delivery-failed");
        delivery.SourceEventId.ShouldBe("corr-delivery-failed");
        delivery.ProviderMessageId.ShouldBeEmpty();
        delivery.Target.Channel.Value.ShouldBe("slack");
        delivery.Target.ConversationKey.ShouldBe("conv:slack:C1");
        delivery.Target.Platform.ShouldBe("slack");
        delivery.Target.AddressId.ShouldBeEmpty();
        delivery.Target.AddressType.ShouldBeEmpty();
        delivery.Target.ConversationId.ShouldBe("conv:slack:C1");
        delivery.Target.ReplyMessageId.ShouldBeEmpty();
        events.Last().EventType.ShouldContain(nameof(ConversationContinueFailedEvent));
    }

    [Fact]
    public async Task HandleContinueCommandAsync_TransientFailure_LeavesCommandRetriable()
    {
        // Retriable continue failures (retry_after_ms) must NOT mark the command id as processed —
        // callers expect to re-dispatch the same command id after the back-off elapses.
        var runner = new RecordingTurnRunner
        {
            ContinueResultFactory = _ => ConversationTurnResult.TransientFailure("rate_limited", "retry later", TimeSpan.FromMilliseconds(250)),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-7");

        await agent.HandleContinueCommandAsync(CreateContinueCommand("cmd-retry"));

        agent.State.ProcessedCommandIds.ShouldNotContain("cmd-retry");
        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(ConversationContinueFailedEvent));

        // A subsequent retry succeeds rather than being rejected as DuplicateCommand.
        runner.ContinueResultFactory = null;
        await agent.HandleContinueCommandAsync(CreateContinueCommand("cmd-retry"));
        runner.ContinueCount.ShouldBe(2);
        agent.State.ProcessedCommandIds.ShouldContain("cmd-retry");
    }

    [Fact]
    public async Task HandleContinueCommandAsync_TransientFailureWithoutRetryAfter_StaysRetriable()
    {
        // Runner returns TransientFailure without an explicit retryAfter. Retry policy must derive
        // from FailureKind (retriable), not from whether RetryAfter was supplied — otherwise the
        // command id gets consumed and the caller cannot re-dispatch.
        var runner = new RecordingTurnRunner
        {
            ContinueResultFactory = _ => ConversationTurnResult.TransientFailure("rate_limited", "retry later"),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-9");

        await agent.HandleContinueCommandAsync(CreateContinueCommand("cmd-transient"));

        agent.State.ProcessedCommandIds.ShouldNotContain("cmd-transient");
        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(ConversationContinueFailedEvent));
        var parsed = ConversationContinueFailedEvent.Parser.ParseFrom(events[0].EventData.Value);
        parsed.RetryPolicyCase.ShouldBe(ConversationContinueFailedEvent.RetryPolicyOneofCase.RetryAfterMs);
        parsed.RetryAfterMs.ShouldBe(0);
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenRunDispatcherIsRegistered_DispatchesDirectlyWithoutWaitingForReminder()
    {
        // Regression: previously the inbound LlmReplyRequest path scheduled a 100ms durable
        // Reminder before DispatchAsync, which Orleans rounded up to ~1 minute and effectively
        // dropped the dispatch in production. The inbound path must call dispatcher.DispatchAsync
        // inline so the LLM worker picks it up immediately.
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                CreateNeedsLlmReply(activity, requestedAtUnixMs: 42)),
        };
        var (agent, _) = await CreateAgentAsync(runner, "conv-direct-dispatch", dispatcher);

        await agent.HandleInboundActivityAsync(CreateActivity("act-direct", "conv:slack:C1"));

        dispatcher.Dispatched.Count.ShouldBe(1);
        dispatcher.Dispatched[0].CorrelationId.ShouldBe("act-direct");
        dispatcher.Dispatched[0].RunId.ShouldBe("act-direct");
        dispatcher.Dispatched[0].TargetActorId.ShouldBe(agent.Id);
    }

    [Fact]
    public async Task HandleNyxRelayInboundActivityAsync_WhenRouteUsesGAgentToolHint_CarriesTargetRefToDispatcher()
    {
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                CreateNeedsLlmReply(activity, requestedAtUnixMs: 42)),
        };
        var queryPort = StaticChatRoutePolicyQueryPort.ForSnapshot(new ChatRoutePolicySnapshot(
            ForwardToModelAction("fallback-model"),
            [
                new ChatRouteRule
                {
                    RuleId = "summary",
                    Priority = 100,
                    Match = new ChatRouteMatch
                    {
                        SourceKind = ChatSourceKind.NyxRelay,
                        Channel = "lark",
                        CommandName = "/summary",
                    },
                    Action = GAgentToolHint("target-gagent-1"),
                },
            ]));
        var resolver = new ChatRouteResolver(new StaticChatRouteFallbackProvider("fallback-model"));
        var (agent, store) = await CreateAgentAsync(
            runner,
            "channel-conversation:conv:lark:C1:scope:owner",
            dispatcher,
            queryPort: queryPort,
            chatRouteResolver: resolver);

        var inboundActivity = CreateActivity("act-route", "conv:lark:C1");
        inboundActivity.ChannelId = new ChannelId { Value = "lark" };
        inboundActivity.Bot = new BotInstanceId { Value = "owner-scope" };
        inboundActivity.From = new ParticipantRef { CanonicalId = "sender-1" };
        inboundActivity.Content = new MessageContent { Text = "/summary status" };
        inboundActivity.TransportExtras = new TransportExtras
        {
            NyxPlatform = "lark",
            NyxRegistrationScopeId = "owner-scope",
        };
        inboundActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-route",
            CorrelationId = "corr-route",
        };

        await agent.HandleNyxRelayInboundActivityAsync(new NyxRelayInboundActivity
        {
            Activity = inboundActivity,
            ReplyToken = "runtime-only-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            CorrelationId = "corr-route",
        });

        dispatcher.Dispatched.ShouldHaveSingleItem();
        dispatcher.Dispatched[0].RunId.ShouldBe("corr-route");
        dispatcher.Dispatched[0].TargetRef.ForwardToModel.ToolChoiceHint.PrefilledArguments.Fields["actor_id"]
            .StringValue.ShouldBe("target-gagent-1");
        dispatcher.Dispatched[0].ReplyToken.ShouldBe("runtime-only-token");

        var events = await store.GetEventsAsync(agent.Id);
        events.ShouldHaveSingleItem();
        var parsed = NeedsLlmReplyEvent.Parser.ParseFrom(events[0].EventData.Value);
        parsed.TargetRef.ShouldBeNull("route decisions are transient and must not be persisted with the pending LLM request");
        parsed.ReplyToken.ShouldBeEmpty();
        agent.State.PendingLlmReplyRequests.Single().TargetRef.ShouldBeNull(
            "actor state is rebuilt from the persisted event and must not retain the transient route decision");
    }

    [Fact]
    public async Task HandleNyxRelayInboundActivityAsync_NeverPersistsReplyTokenIntoEventStore()
    {
        // Issue #366 §4 invariant: relay reply_token must stay actor-owned runtime state.
        // The transient run command NyxRelayInboundActivity carries the token across the
        // dispatch boundary, but the actor must not write it into any persisted event payload.
        const string sentinelReplyToken = "sentinel-reply-token-9f3c5b2e-must-not-persist";
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                CreateNeedsLlmReply(activity, requestedAtUnixMs: 42)),
            LlmReplyResultFactory = reply => ConversationTurnResult.Sent(
                "sent:" + reply.CorrelationId,
                new MessageContent { Text = "ack" },
                "bot",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = reply.Activity?.OutboundDelivery?.ReplyMessageId ?? string.Empty,
                    CorrelationId = reply.CorrelationId,
                }),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-relay-token-leak");

        var inboundActivity = CreateActivity("act-relay-leak", "conv:slack:C1");
        inboundActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-leak",
            CorrelationId = "corr-relay-leak",
        };
        var relayInbound = new NyxRelayInboundActivity
        {
            Activity = inboundActivity,
            ReplyToken = sentinelReplyToken,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            CorrelationId = "corr-relay-leak",
        };

        await agent.HandleNyxRelayInboundActivityAsync(relayInbound);
        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "corr-relay-leak",
            RegistrationId = "reg-1",
            RunId = "corr-relay-leak",
            SourceActorId = "llm-worker-1",
            Activity = inboundActivity.Clone(),
            Outbound = new MessageContent { Text = "reply-from-llm" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 43,
        });

        var events = await store.GetEventsAsync(agent.Id);
        events.ShouldNotBeEmpty();
        var sentinelBytes = Encoding.UTF8.GetBytes(sentinelReplyToken);
        foreach (var record in events)
        {
            var payloadBytes = record.EventData?.Value?.ToByteArray() ?? Array.Empty<byte>();
            ContainsSubsequence(payloadBytes, sentinelBytes)
                .ShouldBeFalse($"persisted event {record.EventType} must not contain reply_token bytes");
        }
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return true;
        }
        return false;
    }

    [Fact]
    public async Task HandleDeferredLlmReplyDispatchRequestedAsync_MissingRuntimeRelayToken_FinalizesNotRetryable()
    {
        const string sentinelReplyToken = "sentinel-retry-token-7c10";
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                CreateNeedsLlmReply(activity, targetActorId: "stale-unscoped-actor")),
        };
        var (agent, store) = await CreateAgentAsync(runner, "channel-conversation:conv:slack:C1:scope:owner", dispatcher);

        var inboundActivity = CreateActivity("nyx-msg-1", "conv:slack:C1");
        inboundActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "nyx-msg-1",
            CorrelationId = "callback-jti-1",
        };

        await agent.HandleNyxRelayInboundActivityAsync(new NyxRelayInboundActivity
        {
            Activity = inboundActivity,
            ReplyToken = sentinelReplyToken,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
            CorrelationId = "legacy-callback-jti-1",
        });

        dispatcher.Dispatched.Count.ShouldBe(1);
        dispatcher.Dispatched[0].ReplyToken.ShouldBe(sentinelReplyToken);
        dispatcher.Dispatched[0].TargetActorId.ShouldBe(agent.Id);
        dispatcher.Dispatched.Clear();

        await agent.HandleDeferredLlmReplyDispatchRequestedAsync(new DeferredLlmReplyDispatchRequestedEvent
        {
            CorrelationId = "callback-jti-1",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        dispatcher.Dispatched.ShouldBeEmpty();
        agent.State.PendingLlmReplyRequests.ShouldNotContain(req => req.CorrelationId == "callback-jti-1");
        var failed = (await store.GetEventsAsync(agent.Id))
            .Where(e => e.EventType.Contains(nameof(ConversationContinueFailedEvent), StringComparison.Ordinal))
            .Select(e => ConversationContinueFailedEvent.Parser.ParseFrom(e.EventData.Value))
            .LastOrDefault(e => e.ErrorCode == "missing_runtime_reply_token");
        failed.ShouldNotBeNull();
        failed!.RetryPolicyCase.ShouldBe(ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_RunEchoedReplyToken_CompletesWithoutPersistingLifecycleCredential()
    {
        const string sentinelReplyToken = "sentinel-cleanup-token-6d41";
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                CreateNeedsLlmReply(activity, targetActorId: "stale-unscoped-actor")),
        };
        var (agent, _) = await CreateAgentAsync(runner, "channel-conversation:conv:slack:C1:scope:owner");

        var inboundActivity = CreateActivity("nyx-msg-cleanup", "conv:slack:C1");
        inboundActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "nyx-msg-cleanup",
            CorrelationId = "callback-jti-cleanup",
        };

        await agent.HandleNyxRelayInboundActivityAsync(new NyxRelayInboundActivity
        {
            Activity = inboundActivity,
            ReplyToken = sentinelReplyToken,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
            CorrelationId = "legacy-callback-jti-cleanup",
        });

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "nyx-msg-cleanup",
            RegistrationId = "reg-1",
            RunId = "nyx-msg-cleanup",
            SourceActorId = "llm-worker-1",
            Activity = inboundActivity.Clone(),
            Outbound = new MessageContent { Text = "reply-from-llm" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyToken = sentinelReplyToken,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
        });

        agent.State.PendingLlmReplyRequests.ShouldNotContain(req => req.CorrelationId == "nyx-msg-cleanup");
        agent.State.ActiveReplyLifecycles.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleInboundActivityAsync_StripsReplyTokenFromPersistedNeedsLlmReplyEvent_ButKeepsItOnRunCommandCopy()
    {
        // Strip-on-persist invariant: NeedsLlmReplyEvent must keep reply_token on the
        // copy dispatched to the run actor so the LLM worker can echo it back, but the persisted
        // copy that lands in event store must omit it.
        const string sentinelReplyToken = "sentinel-strip-on-persist-1f8b3";
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                CreateNeedsLlmReply(
                    activity,
                    replyToken: sentinelReplyToken,
                    replyTokenExpiresAtUnixMs: DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds())),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-strip-token", dispatcher);

        var inboundActivity = CreateActivity("act-strip", "conv:slack:C1");
        inboundActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-strip",
            CorrelationId = "corr-strip",
        };
        await agent.HandleInboundActivityAsync(inboundActivity);

        dispatcher.Dispatched.Count.ShouldBe(1);
        dispatcher.Dispatched[0].ReplyToken.ShouldBe(sentinelReplyToken);

        var events = await store.GetEventsAsync(agent.Id);
        events.ShouldNotBeEmpty();
        var sentinelBytes = Encoding.UTF8.GetBytes(sentinelReplyToken);
        foreach (var record in events)
        {
            var payloadBytes = record.EventData?.Value?.ToByteArray() ?? Array.Empty<byte>();
            ContainsSubsequence(payloadBytes, sentinelBytes)
                .ShouldBeFalse($"persisted event {record.EventType} must not contain reply_token bytes");
        }
    }

    [Fact]
    public async Task HandleInboundActivityAsync_StripsCredentialMetadataKeysFromPersistedNeedsLlmReplyEvent_ButKeepsThemOnRunCommandCopy()
    {
        // Strip-on-persist invariant for per-call NyxID credentials carried in Metadata.
        // The run-command copy keeps them so AgentRunGAgent can forward them to the LLM
        // call, but the persisted state copy must never carry them into event store /
        // projection / read model.
        const string sentinelSenderToken = "sentinel-sender-nyxid-token-9c4f";
        const string sentinelOwnerToken = "sentinel-owner-nyxid-token-7a12";
        const string sentinelOrgToken = "sentinel-owner-org-token-3e09";
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity =>
            {
                var request = CreateNeedsLlmReply(
                    activity,
                    replyToken: "relay-token-strip-cred",
                    replyTokenExpiresAtUnixMs: DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds());
                request.Metadata["nyxid.sender_access_token"] = sentinelSenderToken;
                request.Metadata["nyxid.access_token"] = sentinelOwnerToken;
                request.Metadata["nyxid.org_token"] = sentinelOrgToken;
                request.Metadata["aevatar.sender_binding_id"] = "bnd-keep";
                return ConversationTurnResult.LlmReplyRequested(request);
            },
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-strip-credential-meta", dispatcher);

        var inboundActivity = CreateActivity("act-strip-cred", "conv:slack:C1");
        inboundActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-strip-cred",
            CorrelationId = "corr-strip-cred",
        };
        await agent.HandleInboundActivityAsync(inboundActivity);

        dispatcher.Dispatched.Count.ShouldBe(1);
        dispatcher.Dispatched[0].Metadata["nyxid.sender_access_token"].ShouldBe(sentinelSenderToken);
        dispatcher.Dispatched[0].Metadata["nyxid.access_token"].ShouldBe(sentinelOwnerToken);
        dispatcher.Dispatched[0].Metadata["nyxid.org_token"].ShouldBe(sentinelOrgToken);

        var pending = agent.State.PendingLlmReplyRequests.Single();
        pending.Metadata.ContainsKey("nyxid.sender_access_token").ShouldBeFalse();
        pending.Metadata.ContainsKey("nyxid.access_token").ShouldBeFalse();
        pending.Metadata.ContainsKey("nyxid.org_token").ShouldBeFalse();
        // Non-credential metadata stays — only credential keys are scrubbed.
        pending.Metadata["aevatar.sender_binding_id"].ShouldBe("bnd-keep");

        var events = await store.GetEventsAsync(agent.Id);
        events.ShouldNotBeEmpty();
        foreach (var sentinel in new[] { sentinelSenderToken, sentinelOwnerToken, sentinelOrgToken })
        {
            var sentinelBytes = Encoding.UTF8.GetBytes(sentinel);
            foreach (var record in events)
            {
                var payloadBytes = record.EventData?.Value?.ToByteArray() ?? Array.Empty<byte>();
                ContainsSubsequence(payloadBytes, sentinelBytes)
                    .ShouldBeFalse(
                        $"persisted event {record.EventType} must not contain credential bytes for {sentinel}");
            }
        }
    }

    [Fact]
    public async Task HandleInboundActivityAsync_PersistsDurableToolContextButStripsTypedCredentials()
    {
        const string sentinelSenderToken = "sentinel-typed-sender-token-56bf";
        const string sentinelOwnerToken = "sentinel-typed-owner-token-91d4";
        const string sentinelOrgToken = "sentinel-typed-org-token-e720";
        var durableSkillRecovery = new AgentSkillRecoveryContext(
            RequireInitialOrnnSearch: true,
            RequireOrnnSearchOnBlocker: true,
            CommandName: "summary",
            OriginalCommand: "/summary",
            PrimarySkillName: "project-summary",
            MaxOrnnSearchAttempts: 2);
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.Id,
                    RunId = activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = 42,
                    ToolContext = (AgentToolExecutionContext.Empty with
                    {
                        Credentials = new AgentToolCredentials(
                            sentinelOwnerToken,
                            sentinelOrgToken,
                            sentinelSenderToken),
                        SkillRecovery = durableSkillRecovery,
                    }).ToPayload(),
                }),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-persist-tool-context", dispatcher);

        await agent.HandleInboundActivityAsync(CreateActivity("act-tool-context", "conv:slack:C1"));

        dispatcher.Dispatched.ShouldHaveSingleItem();
        var dispatchedContext = AgentToolExecutionContextMapper.FromPayload(dispatcher.Dispatched[0].ToolContext);
        dispatchedContext.Credentials.NyxIdAccessToken.ShouldBe(sentinelOwnerToken);
        dispatchedContext.Credentials.NyxIdOrgToken.ShouldBe(sentinelOrgToken);
        dispatchedContext.Credentials.SenderNyxIdAccessToken.ShouldBe(sentinelSenderToken);

        var pending = agent.State.PendingLlmReplyRequests.Single();
        var persistedContext = AgentToolExecutionContextMapper.FromPayload(pending.ToolContext);
        persistedContext.SkillRecovery.ShouldBe(durableSkillRecovery);
        persistedContext.Credentials.ShouldBe(AgentToolCredentials.Empty);

        var events = await store.GetEventsAsync(agent.Id);
        events.ShouldNotBeEmpty();
        foreach (var sentinel in new[] { sentinelSenderToken, sentinelOwnerToken, sentinelOrgToken })
        {
            var sentinelBytes = Encoding.UTF8.GetBytes(sentinel);
            foreach (var record in events)
            {
                var payloadBytes = record.EventData?.Value?.ToByteArray() ?? Array.Empty<byte>();
                ContainsSubsequence(payloadBytes, sentinelBytes)
                    .ShouldBeFalse(
                        $"persisted event {record.EventType} must not contain typed credential bytes for {sentinel}");
            }
        }
    }

    [Fact]
    public async Task HandleDeferredLlmReplyDispatchRequestedAsync_StrippedRelayRequestWithoutRuntimeToken_FinalizesNotRetryable()
    {
        // Runtime credentials are not actor state. The persisted NeedsLlmReplyEvent in
        // State.PendingLlmReplyRequests has an empty ReplyToken; a durable-reminder retry
        // must finish as not-retryable rather than rehydrate from an actor token registry.
        const string sentinelReplyToken = "sentinel-retry-enrich-b3d7a";
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                CreateNeedsLlmReply(
                    activity,
                    replyToken: sentinelReplyToken,
                    replyTokenExpiresAtUnixMs: DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds())),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-retry-enrich", dispatcher);

        var inboundActivity = CreateActivity("act-retry", "conv:slack:C1");
        inboundActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-retry",
            CorrelationId = "corr-retry",
        };
        var relayInbound = new NyxRelayInboundActivity
        {
            Activity = inboundActivity,
            ReplyToken = sentinelReplyToken,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
            CorrelationId = "corr-retry",
        };

        await agent.HandleNyxRelayInboundActivityAsync(relayInbound);
        dispatcher.Dispatched.Count.ShouldBe(1);
        dispatcher.Dispatched[0].ReplyToken.ShouldBe(sentinelReplyToken);

        dispatcher.Dispatched.Clear();
        await agent.HandleDeferredLlmReplyDispatchRequestedAsync(new DeferredLlmReplyDispatchRequestedEvent
        {
            CorrelationId = "corr-retry",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        dispatcher.Dispatched.ShouldBeEmpty();
        agent.State.PendingLlmReplyRequests.ShouldNotContain(req => req.CorrelationId == "corr-retry");
        var failed = (await store.GetEventsAsync(agent.Id))
            .Where(e => e.EventType.Contains(nameof(ConversationContinueFailedEvent), StringComparison.Ordinal))
            .Select(e => ConversationContinueFailedEvent.Parser.ParseFrom(e.EventData.Value))
            .LastOrDefault(e => e.ErrorCode == "missing_runtime_reply_token");
        failed.ShouldNotBeNull();
        failed!.RetryPolicyCase.ShouldBe(ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task ActivateAsync_AfterRelayRunDispatchFailure_RestoresRuntimeCredentialsFromReferences()
    {
        const string sentinelReplyToken = "sentinel-dispatch-recovery-reply-token";
        const string sentinelUserAccessToken = "sentinel-dispatch-recovery-user-token";
        const string actorId = "conv-dispatch-recovery";
        var eventStore = new InMemoryEventStore();
        var runtimeSecretStore = new InMemoryRuntimeSecretStore();
        var firstRunner = new RecordingTurnRunner
        {
            InboundResultFactory = activity =>
            {
                var request = CreateNeedsLlmReply(activity);
                request.RunId = "agent-run-dispatch-recovery";
                return ConversationTurnResult.LlmReplyRequested(request);
            },
        };
        var (firstAgent, _) = await CreateAgentAsync(
            firstRunner,
            actorId,
            new FailingRunDispatcher(),
            store: eventStore,
            runtimeSecretStore: runtimeSecretStore);
        var inboundActivity = CreateActivity("act-dispatch-recovery", "conv:slack:C1");
        inboundActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-dispatch-recovery",
            CorrelationId = "corr-dispatch-recovery",
        };
        inboundActivity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = sentinelUserAccessToken,
        };

        await firstAgent.HandleNyxRelayInboundActivityAsync(new NyxRelayInboundActivity
        {
            Activity = inboundActivity,
            ReplyToken = sentinelReplyToken,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
            CorrelationId = "callback-dispatch-recovery",
        });

        var persisted = firstAgent.State.PendingLlmReplyRequests.ShouldHaveSingleItem();
        persisted.ReplyToken.ShouldBeEmpty();
        persisted.Activity.TransportExtras.NyxUserAccessToken.ShouldBeEmpty();
        persisted.RelayReplyTokenRef.Ref.ShouldNotBeNullOrWhiteSpace();
        persisted.RelayUserAccessTokenRef.Ref.ShouldNotBeNullOrWhiteSpace();

        var recoveredDispatcher = new RecordingRunDispatcher();
        await CreateAgentAsync(
            new RecordingTurnRunner(),
            actorId,
            recoveredDispatcher,
            store: eventStore,
            runtimeSecretStore: runtimeSecretStore);

        var recovered = recoveredDispatcher.Dispatched.ShouldHaveSingleItem();
        recovered.ReplyToken.ShouldBe(sentinelReplyToken);
        recovered.ReplyTokenExpiresAtUnixMs.ShouldBeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        recovered.Activity.TransportExtras.NyxUserAccessToken.ShouldBe(sentinelUserAccessToken);

        var events = await eventStore.GetEventsAsync(actorId);
        foreach (var sentinel in new[] { sentinelReplyToken, sentinelUserAccessToken })
        {
            var sentinelBytes = Encoding.UTF8.GetBytes(sentinel);
            foreach (var record in events)
            {
                var payloadBytes = record.EventData?.Value?.ToByteArray() ?? Array.Empty<byte>();
                ContainsSubsequence(payloadBytes, sentinelBytes)
                    .ShouldBeFalse($"persisted event {record.EventType} must not contain raw runtime credential bytes");
            }
        }
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_PrefersRunEchoedReplyToken_OverActorRuntimeDict()
    {
        // The outbound reply consumes the run-echoed reply_token from LlmReplyReadyEvent
        // directly; ConversationGAgent no longer owns an actor token registry.
        ConversationTurnRuntimeContext? observedContext = null;
        var runner = new RecordingTurnRunner
        {
            LlmReplyResultFactory = reply => ConversationTurnResult.Sent(
                "sent:" + reply.CorrelationId,
                new MessageContent { Text = "ack" },
                "bot",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = reply.Activity?.OutboundDelivery?.ReplyMessageId ?? string.Empty,
                    CorrelationId = reply.CorrelationId,
                }),
        };
        runner.LlmReplyContextObserver = ctx => observedContext = ctx;
        var (agent, _) = await CreateAgentAsync(runner, "conv-run-echo");

        var activity = CreateActivity("act-run-echo", "conv:slack:C1");
        activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-echo",
            CorrelationId = "corr-run-echo",
        };

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "nyx-msg-run-echo",
            RegistrationId = "reg-1",
            RunId = "nyx-msg-run-echo",
            SourceActorId = "llm-worker-1",
            Activity = activity.Clone(),
            Outbound = new MessageContent { Text = "reply-from-llm" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyToken = "run-echoed-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
        });

        observedContext.ShouldNotBeNull();
        observedContext!.NyxRelayReplyToken.ShouldNotBeNull();
        observedContext.NyxRelayReplyToken!.ReplyToken.ShouldBe("run-echoed-token");
        observedContext.NyxRelayReplyToken.CorrelationId.ShouldBe("corr-run-echo");
        observedContext.NyxRelayReplyToken.ReplyMessageId.ShouldBe("relay-msg-echo");
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_ResolvesDurableTerminalCredentialReferences()
    {
        const string runId = "workflow-draft-run-secret-ref";
        const string correlationId = "corr-workflow-secret-ref";
        const string replyToken = "durable-terminal-reply-token";
        const string userAccessToken = "durable-terminal-user-token";
        var runtimeSecretStore = new InMemoryRuntimeSecretStore();
        var replyReference = (await runtimeSecretStore.PutAsync(new StoreRuntimeSecretRequest(
            "channel-relay-reply-token",
            runId,
            correlationId,
            replyToken,
            TimeSpan.FromMinutes(10),
            ConsumeOnce: false,
            AuditReason: "test durable terminal reply credential"))).Reference;
        var userReference = (await runtimeSecretStore.PutAsync(new StoreRuntimeSecretRequest(
            "channel-relay-user-access-token",
            runId,
            correlationId,
            userAccessToken,
            TimeSpan.FromMinutes(10),
            ConsumeOnce: false,
            AuditReason: "test durable terminal user credential"))).Reference;
        ConversationTurnRuntimeContext? observedContext = null;
        var runner = new RecordingTurnRunner
        {
            LlmReplyResultFactory = reply => ConversationTurnResult.Sent(
                "sent:" + reply.CorrelationId,
                new MessageContent { Text = "ack" },
                "bot",
                new OutboundDeliveryContext
                {
                    ReplyMessageId = reply.Activity?.OutboundDelivery?.ReplyMessageId ?? string.Empty,
                    CorrelationId = reply.CorrelationId,
                }),
            LlmReplyContextObserver = context => observedContext = context,
        };
        var (agent, _) = await CreateAgentAsync(
            runner,
            "conv-workflow-secret-ref",
            runtimeSecretStore: runtimeSecretStore);
        var activity = CreateActivity("act-workflow-secret-ref", "conv:slack:C1");
        activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-workflow-secret-ref",
            CorrelationId = correlationId,
        };

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = correlationId,
            RegistrationId = "reg-1",
            RunId = runId,
            SourceActorId = "workflow-draft-run-actor-1",
            Activity = activity,
            Outbound = new MessageContent { Text = "workflow complete" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            RelayReplyTokenRef = replyReference,
            RelayUserAccessTokenRef = userReference,
        });

        observedContext.ShouldNotBeNull();
        observedContext!.NyxRelayReplyToken.ShouldNotBeNull();
        observedContext.NyxRelayReplyToken!.ReplyToken.ShouldBe(replyToken);
        observedContext.NyxRelayReplyToken.CorrelationId.ShouldBe(correlationId);
        observedContext.NyxUserAccessToken.ShouldBe(userAccessToken);
    }

    [Fact]
    public async Task HandleDeferredLlmReplyDroppedAsync_RetiresPendingRequestWithNotRetryableFailure()
    {
        // Run actor gates (stale-age, missing relay credential, malformed payload) need
        // a way to tell the actor "stop tracking this pending request" so it doesn't
        // silently accumulate in State.PendingLlmReplyRequests until the next
        // rehydration. The actor's drop handler emits a NotRetryable
        // ConversationContinueFailedEvent which routes through the existing state
        // matcher to remove the pending entry.
        var dispatcher = new RecordingRunDispatcher();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                CreateNeedsLlmReply(
                    activity,
                    replyToken: "drop-test-token",
                    replyTokenExpiresAtUnixMs: DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds())),
        };
        var (agent, store) = await CreateAgentAsync(runner, "conv-drop-clears", dispatcher);

        var inboundActivity = CreateActivity("act-drop", "conv:slack:C1");
        inboundActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-drop",
            CorrelationId = "corr-drop",
        };
        await agent.HandleInboundActivityAsync(inboundActivity);
        agent.State.PendingLlmReplyRequests.ShouldContain(req => req.CorrelationId == "corr-drop");

        await agent.HandleDeferredLlmReplyDroppedAsync(new DeferredLlmReplyDroppedEvent
        {
            CorrelationId = "corr-drop",
            Reason = "stale_agent_run_request_dropped",
            DroppedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        agent.State.PendingLlmReplyRequests.ShouldNotContain(req => req.CorrelationId == "corr-drop");
        var events = await store.GetEventsAsync(agent.Id);
        var lastEvent = events[^1];
        lastEvent.EventType.ShouldContain(nameof(ConversationContinueFailedEvent));
        var failed = ConversationContinueFailedEvent.Parser.ParseFrom(lastEvent.EventData.Value);
        failed.ErrorCode.ShouldBe("stale_agent_run_request_dropped");
        failed.RetryPolicyCase.ShouldBe(ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task HandleDeferredLlmReplyDroppedAsync_IgnoresUnknownCorrelationId()
    {
        var (agent, store) = await CreateAgentAsync(new RecordingTurnRunner(), "conv-drop-unknown");
        var initialEvents = (await store.GetEventsAsync(agent.Id)).Count;

        await agent.HandleDeferredLlmReplyDroppedAsync(new DeferredLlmReplyDroppedEvent
        {
            CorrelationId = "corr-not-pending",
            Reason = "stale_agent_run_request_dropped",
            DroppedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(initialEvents);
    }

    [Fact]
    public async Task HandleContinueCommandAsync_PermanentFailure_MarksCommandProcessed()
    {
        // Terminal (non-retryable) continue failures consume the command id so a buggy caller's
        // redispatch is collapsed to DuplicateCommand rather than re-executing the failing turn.
        var runner = new RecordingTurnRunner
        {
            ContinueResultFactory = _ => ConversationTurnResult.PermanentFailure("permanent_error", "bad input"),
        };
        var (agent, _) = await CreateAgentAsync(runner, "conv-8");

        await agent.HandleContinueCommandAsync(CreateContinueCommand("cmd-permanent"));

        agent.State.ProcessedCommandIds.ShouldContain("cmd-permanent");
        runner.ContinueCount.ShouldBe(1);

        // Redispatch of the same id is now rejected as DuplicateCommand; runner is not invoked again.
        await agent.HandleContinueCommandAsync(CreateContinueCommand("cmd-permanent"));
        runner.ContinueCount.ShouldBe(1);
    }

    [Fact]
    public async Task HandleLlmReplyStreamChunkAsync_FirstChunk_CallsRunStreamChunkWithoutPlatformMessageId()
    {
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, currentPmid) =>
                ConversationStreamChunkResult.Succeeded(currentPmid ?? "om_first"),
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, _) = await CreateAgentAsync(runner, "conv-stream-first", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream", "relay-msg-1", "hello"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        runner.StreamChunkCount.ShouldBe(1);
        runner.LastStreamChunkCurrentPlatformMessageId.ShouldBeNull();
    }

    [Fact]
    public async Task HandleLlmReplyStreamChunkAsync_SubsequentChunk_PassesStoredPlatformMessageId()
    {
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, currentPmid) =>
                ConversationStreamChunkResult.Succeeded(currentPmid ?? "om_first"),
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, _) = await CreateAgentAsync(runner, "conv-stream-2", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-2", "relay-msg-1", "first chunk"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-2", "relay-msg-1", "first chunk plus more"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        runner.StreamChunkCount.ShouldBe(2);
        runner.LastStreamChunkCurrentPlatformMessageId.ShouldBe("om_first");
    }

    [Fact]
    public async Task HandleLlmReplyStreamChunkAsync_WhenRunnerFails_MarksDisabledAndDropsFurtherChunks()
    {
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, _) =>
                ConversationStreamChunkResult.Failed("relay_reply_edit_unsupported", "nope", editUnsupported: true),
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, _) = await CreateAgentAsync(runner, "conv-stream-fail", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-fail", "relay-msg-1", "first"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-fail", "relay-msg-1", "first plus second"));
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-fail", "relay-msg-1", "first plus second plus third"));

        runner.StreamChunkCount.ShouldBe(1);
    }

    [Fact]
    public async Task HandleLlmReplyStreamChunkAsync_WithoutReplyToken_DisablesStreamingForTurn()
    {
        var runner = new RecordingTurnRunner();
        var (agent, _) = await CreateAgentAsync(runner, "conv-stream-no-token");

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunkWithoutReplyToken("act-stream-no-token", "relay-msg-1", "hello"));

        runner.StreamChunkCount.ShouldBe(0);
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenStreamingSucceeded_PersistsCompletedWithoutInvokingRunLlmReply()
    {
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, currentPmid) =>
                ConversationStreamChunkResult.Succeeded(currentPmid ?? "om_stream"),
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, store) = await CreateAgentAsync(runner, "conv-stream-short-circuit", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-sc", "relay-msg-1", "final text"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-sc",
            RegistrationId = "reg-1",
            RunId = "act-stream-sc",
            SourceActorId = "agent-run",
            Activity = CreateRelayActivity("act-stream-sc", "relay-msg-1"),
            Outbound = new MessageContent { Text = "final text" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
        };
        await agent.HandleLlmReplyReadyAsync(ready);

        runner.LlmReplyCount.ShouldBe(0);
        // Streaming bypasses RunLlmReplyAsync (where the non-streaming swap lives), so the GAgent
        // must invoke OnReplyDeliveredAsync explicitly to fire the runner's post-reply housekeeping
        // (e.g. Lark Typing→DONE reaction swap). Without this, the most common production path
        // would never swap reactions.
        runner.OnReplyDeliveredCount.ShouldBe(1);
        runner.LastOnReplyDeliveredActivity.ShouldNotBeNull();
        runner.LastOnReplyDeliveredActivity!.Id.ShouldBe("act-stream-sc");
        var events = await store.GetEventsAsync(agent.Id);
        events.ShouldNotBeEmpty();
        events.Last().EventType.ShouldContain(nameof(ConversationTurnCompletedEvent));
        var completed = ConversationTurnCompletedEvent.Parser.ParseFrom(events.Last().EventData.Value);
        completed.Outbound.Text.ShouldBe("final text");
        completed.SentActivityId.ShouldStartWith("nyx-relay-stream:");
        events.Count(e => e.EventData.Is(DeliveryProducedEvent.Descriptor)).ShouldBe(1);
        var deliveryRecord = events.Single(e => e.EventData.Is(DeliveryProducedEvent.Descriptor));
        var delivery = deliveryRecord.EventData.Unpack<DeliveryProducedEvent>();
        delivery.RunId.ShouldBe("act-stream-sc");
        delivery.TurnId.ShouldBe("act-stream-sc");
        delivery.DeliveryKind.ShouldBe(DeliveryKind.TextMessage);
        delivery.Status.ShouldBe(DeliveryStatus.Succeeded);
        delivery.ProducedAtVersion.ShouldBe(deliveryRecord.Version);
        delivery.RequestId.ShouldBe("llm:act-stream-sc");
        delivery.SourceEventId.ShouldBe("act-stream-sc");
        delivery.ProviderMessageId.ShouldBe("nyx-relay-stream:om_stream");
        delivery.CardId.ShouldBeEmpty();
        delivery.Target.Channel.Value.ShouldBe("lark");
        delivery.Target.ConversationKey.ShouldBe("conv:lark:grp");
        delivery.Target.Platform.ShouldBe("lark");
        delivery.Target.AddressId.ShouldBe("relay-msg-1");
        delivery.Target.AddressType.ShouldBeEmpty();
        delivery.Target.ConversationId.ShouldBe("conv:lark:grp");
        delivery.Target.ReplyMessageId.ShouldBe("relay-msg-1");
        var recentDelivery = agent.State.RecentDeliveries.ShouldHaveSingleItem();
        recentDelivery.RequestId.ShouldBe("llm:act-stream-sc");
        recentDelivery.Status.ShouldBe(DeliveryStatus.Succeeded);
        recentDelivery.ProviderMessageId.ShouldBe("nyx-relay-stream:om_stream");
        agent.State.LastSuccessfulDelivery.ShouldNotBeNull();
        agent.State.LastSuccessfulDelivery!.RequestId.ShouldBe("llm:act-stream-sc");
        agent.State.LastSuccessfulDelivery.ProviderMessageId.ShouldBe("nyx-relay-stream:om_stream");
    }

    [Fact]
    public async Task NyxRelayStreaming_EmitsTransitionFactsForStartEditAndTerminal()
    {
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, currentPmid) =>
                ConversationStreamChunkResult.Succeeded(currentPmid ?? "om_nyx_emit"),
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, store) = await CreateAgentAsync(runner, "conv-nyx-emit", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-nyx-emit", "relay-msg-1", "hello"));
        var started = LastReplyLifecycleChanged(await store.GetEventsAsync(agent.Id));
        AssertReplyLifecycleTransition(
            started,
            ConversationReplyLifecycleMode.NyxRelayText,
            "act-nyx-emit",
            ConversationReplyLifecyclePhase.TextIdle,
            ConversationReplyLifecyclePhase.TextIdle);
        started.NyxRelayOperation.ShouldBe(NyxRelayTextOperationKind.Interim);
        started.OperationSequence.ShouldBe(1);
        started.OperationGeneration.ShouldBe(1);
        started.QueuedAccumulatedText.ShouldBe("hello");

        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        var flushed = LastReplyLifecycleChanged(await store.GetEventsAsync(agent.Id));
        AssertReplyLifecycleTransition(
            flushed,
            ConversationReplyLifecycleMode.NyxRelayText,
            "act-nyx-emit",
            ConversationReplyLifecyclePhase.TextIdle,
            ConversationReplyLifecyclePhase.TextPlaceholderSent);
        flushed.PlatformMessageIdAssigned.ShouldBe("om_nyx_emit");
        flushed.FlushedTextDelta.ShouldBe("hello");
        flushed.NyxRelayOperation.ShouldBe(NyxRelayTextOperationKind.Unspecified);
        flushed.OperationSequence.ShouldBe(0);
        flushed.QueuedAccumulatedText.ShouldBeEmpty();

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-nyx-emit", "relay-msg-1", "hello edited"));
        var editStarted = LastReplyLifecycleChanged(await store.GetEventsAsync(agent.Id));
        AssertReplyLifecycleTransition(
            editStarted,
            ConversationReplyLifecycleMode.NyxRelayText,
            "act-nyx-emit",
            ConversationReplyLifecyclePhase.TextPlaceholderSent,
            ConversationReplyLifecyclePhase.TextPlaceholderSent);
        editStarted.NyxRelayOperation.ShouldBe(NyxRelayTextOperationKind.Interim);
        editStarted.OperationSequence.ShouldBe(1);
        editStarted.OperationGeneration.ShouldBe(2);
        editStarted.QueuedAccumulatedText.ShouldBe("hello edited");

        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        var edited = LastReplyLifecycleChanged(await store.GetEventsAsync(agent.Id));
        AssertReplyLifecycleTransition(
            edited,
            ConversationReplyLifecycleMode.NyxRelayText,
            "act-nyx-emit",
            ConversationReplyLifecyclePhase.TextPlaceholderSent,
            ConversationReplyLifecyclePhase.TextStreaming);
        edited.FlushedTextDelta.ShouldBe("hello edited");
        edited.EditCountDelta.ShouldBe(1);
        edited.NyxRelayOperation.ShouldBe(NyxRelayTextOperationKind.Unspecified);
        edited.OperationSequence.ShouldBe(0);
        edited.OperationGeneration.ShouldBe(2);
        edited.QueuedAccumulatedText.ShouldBeEmpty();

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "act-nyx-emit",
            RegistrationId = "reg-1",
            RunId = "act-nyx-emit",
            SourceActorId = "agent-run",
            Activity = CreateRelayActivity("act-nyx-emit", "relay-msg-1"),
            Outbound = new MessageContent { Text = "hello edited" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
        });
        var terminated = LastReplyLifecycleChanged(await store.GetEventsAsync(agent.Id));
        AssertReplyLifecycleTransition(
            terminated,
            ConversationReplyLifecycleMode.NyxRelayText,
            "act-nyx-emit",
            ConversationReplyLifecyclePhase.TextStreaming,
            ConversationReplyLifecyclePhase.TextTerminalSucceeded);
        terminated.TerminalReason.ShouldBe("completed");
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_TextStreamingLifecycleSurvivesReactivation()
    {
        var store = new InMemoryEventStore();
        var firstRunner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, currentPmid) =>
                ConversationStreamChunkResult.Succeeded(currentPmid ?? "om_reactivated"),
        };
        var (firstAgent, _) = await CreateAgentAsync(firstRunner, "conv-stream-reactivate", store: store);

        await firstAgent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-reactivate", "relay-msg-1", "first partial"));
        await firstAgent.HandleNyxRelayTextOperationCompletedAsync(new NyxRelayTextOperationCompletedEvent
        {
            CorrelationId = "act-stream-reactivate",
            Operation = NyxRelayTextOperationKind.Interim,
            Sequence = 1,
            OperationGeneration = firstAgent.State.ActiveReplyLifecycles.Single().NyxRelayOperationGeneration,
            State = NyxRelayTextOperationResultState.Succeeded,
            RawResult = new NyxRelayTextOperationRawResult { PlatformMessageId = "om_reactivated" },
            Chunk = CreateStreamChunk("act-stream-reactivate", "relay-msg-1", "first partial"),
        });

        var lifecycle = firstAgent.State.ActiveReplyLifecycles.Single();
        lifecycle.Mode.ShouldBe(ConversationReplyLifecycleMode.NyxRelayText);
        lifecycle.PlatformMessageId.ShouldBe("om_reactivated");
        lifecycle.Phase.ShouldBe(ConversationReplyLifecyclePhase.TextPlaceholderSent);
        lifecycle.LastFlushedText.ShouldBe("first partial");

        var secondRunner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, currentPmid) =>
                ConversationStreamChunkResult.Succeeded(currentPmid ?? "om_reactivated"),
        };
        var secondDispatch = new RecordingActorDispatchPort();
        var (secondAgent, _) = await CreateAgentAsync(secondRunner, "conv-stream-reactivate", store: store, dispatchPort: secondDispatch);

        await secondAgent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-reactivate",
            RegistrationId = "reg-1",
            RunId = "act-stream-reactivate",
            SourceActorId = "agent-run",
            Activity = CreateRelayActivity("act-stream-reactivate", "relay-msg-1"),
            Outbound = new MessageContent { Text = "final after activation" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
            ReplyToken = "runtime-ready-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        });
        await CompleteNextNyxRelayTextOperationAsync(secondAgent, secondDispatch);

        secondRunner.LlmReplyCount.ShouldBe(0);
        secondRunner.StreamChunkCount.ShouldBe(1);
        secondRunner.LastStreamChunkCurrentPlatformMessageId.ShouldBe("om_reactivated");
        secondAgent.State.ActiveReplyLifecycles.ShouldBeEmpty();
        var completed = ConversationTurnCompletedEvent.Parser.ParseFrom(
            (await store.GetEventsAsync(secondAgent.Id)).Last().EventData.Value);
        completed.Outbound.Text.ShouldBe("final after activation");
        completed.SentActivityId.ShouldStartWith("nyx-relay-stream:");
    }

    [Fact]
    public async Task ActivateAsync_ReplyLifecycleTransitionFacts_DeriveStateAcrossNyxRelayTransitions()
    {
        var store = new InMemoryEventStore();
        await AppendStateEventAsync(
            store,
            "conv-nyx-fact-replay",
            new ConversationReplyLifecycleChangedEvent
            {
                CorrelationId = "corr-nyx-fact",
                Mode = ConversationReplyLifecycleMode.NyxRelayText,
                PreviousPhase = ConversationReplyLifecyclePhase.TextIdle,
                Phase = ConversationReplyLifecyclePhase.TextPlaceholderSent,
                ChangedAtUnixMs = 100,
                PlatformMessageIdAssigned = "om_fact",
                FlushedTextDelta = "first",
                OperationGeneration = 1,
            },
            1);
        await AppendStateEventAsync(
            store,
            "conv-nyx-fact-replay",
            new ConversationReplyLifecycleChangedEvent
            {
                CorrelationId = "corr-nyx-fact",
                Mode = ConversationReplyLifecycleMode.NyxRelayText,
                PreviousPhase = ConversationReplyLifecyclePhase.TextPlaceholderSent,
                Phase = ConversationReplyLifecyclePhase.TextStreaming,
                ChangedAtUnixMs = 200,
                FlushedTextDelta = "second",
                EditCountDelta = 2,
                NyxRelayOperation = NyxRelayTextOperationKind.Final,
                OperationSequence = 2,
                OperationGeneration = 2,
                FinalizeText = "final",
                FinalizeCommandId = "cmd-final",
                NyxRelayTerminalState = LlmReplyTerminalState.Completed,
            },
            2);
        await AppendStateEventAsync(
            store,
            "conv-nyx-fact-replay",
            new ConversationReplyLifecycleChangedEvent
            {
                CorrelationId = "corr-nyx-fact",
                Mode = ConversationReplyLifecycleMode.NyxRelayText,
                PreviousPhase = ConversationReplyLifecyclePhase.TextStreaming,
                Phase = ConversationReplyLifecyclePhase.TextTerminalSucceeded,
                ChangedAtUnixMs = 300,
                NyxRelayOperation = NyxRelayTextOperationKind.Unspecified,
                OperationSequence = 0,
                FinalizeText = string.Empty,
                FinalizeCommandId = string.Empty,
                NyxRelayTerminalState = LlmReplyTerminalState.Unspecified,
                TerminalReason = "completed",
            },
            3);

        var (agent, _) = await CreateAgentAsync(new RecordingTurnRunner(), "conv-nyx-fact-replay", store: store);

        var lifecycle = agent.State.ActiveReplyLifecycles.ShouldHaveSingleItem();
        lifecycle.CorrelationId.ShouldBe("corr-nyx-fact");
        lifecycle.Mode.ShouldBe(ConversationReplyLifecycleMode.NyxRelayText);
        lifecycle.Phase.ShouldBe(ConversationReplyLifecyclePhase.TextTerminalSucceeded);
        lifecycle.PlatformMessageId.ShouldBe("om_fact");
        lifecycle.LastFlushedText.ShouldBe("second");
        lifecycle.EditCount.ShouldBe(2);
        lifecycle.NyxRelayInFlightOperation.ShouldBe(NyxRelayTextOperationKind.Unspecified);
        lifecycle.NyxRelayInFlightSequence.ShouldBe(0);
        lifecycle.NyxRelayOperationGeneration.ShouldBe(2);
        lifecycle.PendingFinalizeText.ShouldBeEmpty();
        lifecycle.PendingFinalizeCommandId.ShouldBeEmpty();
        lifecycle.PendingNyxRelayTerminalState.ShouldBe(LlmReplyTerminalState.Unspecified);
        lifecycle.TerminalReason.ShouldBe("completed");
        lifecycle.UpdatedAtUnixMs.ShouldBe(300);
    }

    [Fact]
    public async Task ActivateAsync_WhenConversationEventStreamCompactedWithoutSnapshot_RecoversAndAcceptsNewTurn()
    {
        var store = new InMemoryEventStore();
        const string agentId = "channel-conversation:lark:dm:user-1:scope:owner-1";

        await AppendStateEventAsync(
            store,
            agentId,
            new ConversationTurnCompletedEvent
            {
                ProcessedActivityId = "old-activity",
                CompletedAtUnixMs = 1,
            },
            1);
        (await store.DeleteEventsUpToAsync(agentId, 1)).ShouldBe(1);

        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = _ => ConversationTurnResult.Sent(
                "new-activity",
                new MessageContent { Text = "pong" },
                "reply-new-activity"),
        };

        var (agent, _) = await CreateAgentAsync(runner, agentId, store: store);

        agent.EventSourcing!.CurrentVersion.ShouldBe(1);
        await agent.HandleInboundActivityAsync(CreateActivity("new-activity", "lark:dm:user-1"));

        agent.EventSourcing.CurrentVersion.ShouldBe(2);
        var events = await store.GetEventsAsync(agentId);
        events.ShouldHaveSingleItem().Version.ShouldBe(2);
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenStreamingDisabled_FallsBackToRunLlmReplyAsync()
    {
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, _) =>
                ConversationStreamChunkResult.Failed("relay_reply_edit_unsupported", "nope", editUnsupported: true),
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, store) = await CreateAgentAsync(runner, "conv-stream-fallback", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-fb", "relay-msg-1", "partial"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-fb",
            RegistrationId = "reg-1",
            RunId = "act-stream-fb",
            SourceActorId = "agent-run",
            Activity = CreateRelayActivity("act-stream-fb", "relay-msg-1"),
            Outbound = new MessageContent { Text = "final text" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
        };
        await agent.HandleLlmReplyReadyAsync(ready);

        runner.LlmReplyCount.ShouldBe(1);
        // The non-streaming fallback runs through RunLlmReplyAsync, where the production runner
        // already fires the post-reply swap internally. The GAgent must NOT also call
        // OnReplyDeliveredAsync on this path or the swap would run twice (extra Lark API calls,
        // duplicate DONE reaction attempts).
        runner.OnReplyDeliveredCount.ShouldBe(0);
        var events = await store.GetEventsAsync(agent.Id);
        events.Last(e => e.EventType.Contains(nameof(ConversationTurnCompletedEvent), StringComparison.Ordinal))
            .EventType.ShouldContain(nameof(ConversationTurnCompletedEvent));
    }

    [Fact]
    public async Task HandleLlmReplyStreamChunkAsync_InterimEditFailureAfterTokenConsumed_SuppressesSubsequentChunksWithoutDisablingFinalEdit()
    {
        // Regression for PR#374 P1 review: once the first chunk consumes the NyxID /reply token,
        // an interim /reply/update failure must NOT mark the turn as fallback-safe. Marking it
        // Disabled would send the final LlmReplyReady path into RunLlmReplyAsync, which re-uses
        // the already-consumed JTI and yields 401. Instead the state must be SuppressInterim so
        // later interim chunks are dropped but the final edit can still reconcile the user
        // message.
        var callCount = 0;
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, pmid) =>
            {
                callCount++;
                if (callCount == 1)
                    return ConversationStreamChunkResult.Succeeded("om_first_consumed");
                return ConversationStreamChunkResult.Failed("transient_edit_error", "boom");
            },
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, _) = await CreateAgentAsync(runner, "conv-stream-suppress", dispatchPort: dispatch);

        // First chunk consumes the reply token.
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-suppress", "relay-msg-1", "hello"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        // Interim edit fails after token consumed.
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-suppress", "relay-msg-1", "hello world"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        // Later interim chunk must be dropped (not dispatched to runner).
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-suppress", "relay-msg-1", "hello world again"));

        callCount.ShouldBe(2);
    }

    [Fact]
    public async Task HandleLlmReplyStreamChunkAsync_TransientInterimEditFailure_RetriesImmediateSelfOperation()
    {
        var callCount = 0;
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, pmid) =>
            {
                callCount++;
                return callCount switch
                {
                    1 => ConversationStreamChunkResult.Succeeded("om_retry_success"),
                    2 => ConversationStreamChunkResult.Failed(
                        "relay_reply_update_rejected",
                        "transient",
                        failureKind: FailureKind.TransientAdapterError),
                    _ => ConversationStreamChunkResult.Succeeded(pmid ?? "om_retry_success"),
                };
            },
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, _) = await CreateAgentAsync(runner, "conv-stream-interim-retry", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-interim-retry", "relay-msg-1", "hello"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-interim-retry", "relay-msg-1", "hello world"));
        var failed = await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        var lifecycleAfterRetryQueued = agent.State.ActiveReplyLifecycles.ShouldHaveSingleItem();
        lifecycleAfterRetryQueued.NyxRelayRetryAttempt.ShouldBe(1);
        lifecycleAfterRetryQueued.NyxRelayOperationGeneration.ShouldBe(failed.OperationGeneration + 1);
        lifecycleAfterRetryQueued.NyxRelayInFlightSequence.ShouldBe(failed.Sequence);

        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        callCount.ShouldBe(3);
        var lifecycle = agent.State.ActiveReplyLifecycles.ShouldHaveSingleItem();
        lifecycle.Phase.ShouldBe(ConversationReplyLifecyclePhase.TextStreaming);
        lifecycle.LastFlushedText.ShouldBe("hello world");
        lifecycle.NyxRelayRetryAttempt.ShouldBe(0);
    }

    [Fact]
    public async Task HandleLlmReplyStreamChunkAsync_TransientInterimEditRetryExhaustion_SuppressesInterimButAllowsFinalEdit()
    {
        var callCount = 0;
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, pmid) =>
            {
                callCount++;
                if (callCount == 1)
                    return ConversationStreamChunkResult.Succeeded("om_retry_exhaust");
                if (callCount <= 4)
                    return ConversationStreamChunkResult.Failed(
                        "relay_reply_update_rejected",
                        "transient",
                        failureKind: FailureKind.TransientAdapterError);
                return ConversationStreamChunkResult.Succeeded(pmid ?? "om_retry_exhaust");
            },
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, store) = await CreateAgentAsync(runner, "conv-stream-interim-retry-exhaust", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-interim-retry-exhaust", "relay-msg-1", "hello"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-interim-retry-exhaust", "relay-msg-1", "hello world"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        var suppressed = agent.State.ActiveReplyLifecycles.ShouldHaveSingleItem();
        suppressed.Phase.ShouldBe(ConversationReplyLifecyclePhase.TextSuppressingInterim);
        suppressed.NyxRelayRetryAttempt.ShouldBe(0);
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-interim-retry-exhaust", "relay-msg-1", "hello dropped"));
        callCount.ShouldBe(4);

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-interim-retry-exhaust",
            RegistrationId = "reg-1",
            RunId = "act-stream-interim-retry-exhaust",
            SourceActorId = "agent-run",
            Activity = CreateRelayActivity("act-stream-interim-retry-exhaust", "relay-msg-1"),
            Outbound = new MessageContent { Text = "hello final" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
        });
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        runner.LlmReplyCount.ShouldBe(0);
        callCount.ShouldBe(5);
        var completed = ConversationTurnCompletedEvent.Parser.ParseFrom(
            (await store.GetEventsAsync(agent.Id)).Last().EventData.Value);
        completed.Outbound.Text.ShouldBe("hello final");
    }

    [Fact]
    public async Task HandleLlmReplyStreamChunkAsync_PermanentInterimEditFailure_DoesNotRetry()
    {
        var callCount = 0;
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, pmid) =>
            {
                callCount++;
                if (callCount == 1)
                    return ConversationStreamChunkResult.Succeeded("om_permanent_no_retry");
                return ConversationStreamChunkResult.Failed(
                    "relay_reply_edit_unsupported",
                    "edit unsupported",
                    editUnsupported: true,
                    failureKind: FailureKind.PermanentAdapterError,
                    rawErrorKey: "edit_unsupported");
            },
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, _) = await CreateAgentAsync(runner, "conv-stream-permanent-no-retry", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-permanent-no-retry", "relay-msg-1", "hello"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-permanent-no-retry", "relay-msg-1", "hello world"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        callCount.ShouldBe(2);
        var lifecycle = agent.State.ActiveReplyLifecycles.ShouldHaveSingleItem();
        lifecycle.Phase.ShouldBe(ConversationReplyLifecyclePhase.TextSuppressingInterim);
        lifecycle.NyxRelayRetryAttempt.ShouldBe(0);
    }

    [Fact]
    public async Task HandleLlmReplyStreamChunkAsync_TransientInterimEditFailureWithPositiveRetryAfter_DoesNotScheduleRetry()
    {
        var callCount = 0;
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, pmid) =>
            {
                callCount++;
                if (callCount == 1)
                    return ConversationStreamChunkResult.Succeeded("om_retry_after_no_retry");
                return ConversationStreamChunkResult.Failed(
                    "relay_reply_update_rejected",
                    "rate limited",
                    failureKind: FailureKind.TransientAdapterError,
                    retryAfter: TimeSpan.FromSeconds(4),
                    rawErrorKey: "rate_limited");
            },
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, _) = await CreateAgentAsync(runner, "conv-stream-retry-after-no-retry", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-retry-after-no-retry", "relay-msg-1", "hello"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-retry-after-no-retry", "relay-msg-1", "hello world"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        callCount.ShouldBe(2);
        var lifecycle = agent.State.ActiveReplyLifecycles.ShouldHaveSingleItem();
        lifecycle.Phase.ShouldBe(ConversationReplyLifecyclePhase.TextSuppressingInterim);
        lifecycle.NyxRelayRetryAttempt.ShouldBe(0);
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenTokenAlreadyConsumedAndInterimEditFailed_RetriesFinalEditInsteadOfReusingToken()
    {
        // Regression for PR#374 P1 review: final LlmReplyReady must try the final /reply/update
        // via RunStreamChunkAsync instead of falling through to RunLlmReplyAsync (which would
        // reuse the already-consumed reply token and 401).
        var callCount = 0;
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, pmid) =>
            {
                callCount++;
                if (callCount == 1)
                    return ConversationStreamChunkResult.Succeeded("om_first_consumed");
                if (callCount == 2)
                    return ConversationStreamChunkResult.Failed("transient_edit_error", "boom");
                // Third call is the final edit initiated by TryCompleteStreamedReplyAsync.
                return ConversationStreamChunkResult.Succeeded(pmid ?? "om_first_consumed");
            },
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, store) = await CreateAgentAsync(runner, "conv-stream-final-retry", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-final-retry", "relay-msg-1", "hello"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-final-retry", "relay-msg-1", "hello world"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-final-retry",
            RegistrationId = "reg-1",
            RunId = "act-stream-final-retry",
            SourceActorId = "agent-run",
            Activity = CreateRelayActivity("act-stream-final-retry", "relay-msg-1"),
            Outbound = new MessageContent { Text = "hello world final" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
        };
        await agent.HandleLlmReplyReadyAsync(ready);
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        // Must not fall back to RunLlmReplyAsync — the token is already consumed.
        runner.LlmReplyCount.ShouldBe(0);
        // Third RunStreamChunkAsync call is the final edit.
        callCount.ShouldBe(3);

        var events = await store.GetEventsAsync(agent.Id);
        var completed = ConversationTurnCompletedEvent.Parser.ParseFrom(events.Last().EventData.Value);
        completed.Outbound.Text.ShouldBe("hello world final");
        completed.SentActivityId.ShouldStartWith("nyx-relay-stream:");
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenTokenConsumedAndFinalEditAlsoFails_PersistsLastFlushedPartialAsTerminalWithoutReusingToken()
    {
        // Regression for PR#374 P1 review: if the final edit also fails after the token was
        // consumed, the actor must not fall back to RunLlmReplyAsync (would 401 on dead token).
        // Instead it persists the last flushed partial as the terminal user-visible state so the
        // pipeline stops spinning on a guaranteed-failing send.
        var callCount = 0;
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, pmid) =>
            {
                callCount++;
                if (callCount == 1)
                    return ConversationStreamChunkResult.Succeeded("om_first_consumed");
                return ConversationStreamChunkResult.Failed("transient_edit_error", "boom");
            },
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, store) = await CreateAgentAsync(runner, "conv-stream-final-degraded", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-final-degraded", "relay-msg-1", "hello partial"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-final-degraded", "relay-msg-1", "hello partial more"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-final-degraded",
            RegistrationId = "reg-1",
            RunId = "act-stream-final-degraded",
            SourceActorId = "agent-run",
            Activity = CreateRelayActivity("act-stream-final-degraded", "relay-msg-1"),
            Outbound = new MessageContent { Text = "hello partial more final" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
        };
        await agent.HandleLlmReplyReadyAsync(ready);
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        runner.LlmReplyCount.ShouldBe(0);
        var events = await store.GetEventsAsync(agent.Id);
        events.Last().EventType.ShouldContain(nameof(ConversationTurnCompletedEvent));
        var completed = ConversationTurnCompletedEvent.Parser.ParseFrom(events.Last().EventData.Value);
        // The user sees the last successfully flushed partial, not the final LLM text.
        completed.Outbound.Text.ShouldBe("hello partial");
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenStreamingStartedThenLlmFailed_EditsPlaceholderInsteadOfReusingToken()
    {
        // Production scenario (issue observed 2026-05-03): user sends a message,
        // streaming sink fires the first chunk via /reply (consuming the reply
        // token, placing a "..." placeholder), the LLM call then 429's before
        // any real chunk arrives. Pre-fix the failure path fell through to
        // RunLlmReplyAsync which issued a fresh /reply against the dead token
        // and got 401, leaving the user staring at "..." forever with no error
        // text. Self-heal: TryCompleteStreamedReplyAsync's Failed branch must
        // EDIT the placeholder via RunStreamChunkAsync with the failure text
        // instead of reusing the consumed reply token.
        var callCount = 0;
        string? lastEditedText = null;
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (chunk, pmid) =>
            {
                callCount++;
                lastEditedText = chunk.AccumulatedText;
                if (callCount == 1)
                    return ConversationStreamChunkResult.Succeeded("om_placeholder_consumed");
                // Second call is the failure-edit initiated from the Failed
                // branch; it succeeds in production because /reply/update
                // works on the existing message regardless of the reply token.
                return ConversationStreamChunkResult.Succeeded(pmid ?? "om_placeholder_consumed");
            },
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, store) = await CreateAgentAsync(runner, "conv-stream-failed-edit", dispatchPort: dispatch);

        // First chunk lands the placeholder + consumes the reply token.
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-failed", "relay-msg-1", "..."));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-failed",
            RegistrationId = "reg-1",
            RunId = "act-stream-failed",
            SourceActorId = "agent-run",
            Activity = CreateRelayActivity("act-stream-failed", "relay-msg-1"),
            // Run actor classifies the LLM exception into a user-facing
            // message and stuffs it into Outbound.Text on the Failed event.
            Outbound = new MessageContent { Text = "Sorry, the upstream model is rate limited (HTTP 429). Please try again in a moment." },
            TerminalState = LlmReplyTerminalState.Failed,
            ErrorCode = "llm_reply_failed",
            ErrorSummary = "Upstream LLM rate limited.",
            ReadyAtUnixMs = 100,
        };
        await agent.HandleLlmReplyReadyAsync(ready);
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        // Must NOT fall through to RunLlmReplyAsync (would 401 on the dead token).
        runner.LlmReplyCount.ShouldBe(0);
        // Two RunStreamChunkAsync calls: first chunk + failure-edit.
        callCount.ShouldBe(2);
        // The placeholder was edited with the classified failure text.
        lastEditedText.ShouldNotBeNull();
        lastEditedText.ShouldContain("rate limited");

        var events = await store.GetEventsAsync(agent.Id);
        events.Last().EventType.ShouldContain(nameof(ConversationTurnCompletedEvent));
        var completed = ConversationTurnCompletedEvent.Parser.ParseFrom(events.Last().EventData.Value);
        completed.Outbound.Text.ShouldContain("rate limited");
        completed.SentActivityId.ShouldStartWith("nyx-relay-stream:");
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenStreamingStartedAndFailedEditAlsoFails_PersistsLastFlushedAsTerminalWithoutReusingToken()
    {
        // Defence in depth for the Failed branch: if even the in-place edit
        // is rejected (e.g. Lark refuses an edit of a message past its window),
        // we still must NOT fall through to RunLlmReplyAsync. Persist what
        // the user already sees (the streaming partial / placeholder) and
        // stop — anything else would 401 on the dead token.
        var callCount = 0;
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, _) =>
            {
                callCount++;
                if (callCount == 1)
                    return ConversationStreamChunkResult.Succeeded("om_placeholder_consumed");
                return ConversationStreamChunkResult.Failed("relay_reply_edit_unsupported", "lark refused", editUnsupported: true);
            },
        };
        var dispatch = new RecordingActorDispatchPort();
        var (agent, store) = await CreateAgentAsync(runner, "conv-stream-failed-edit-deny", dispatchPort: dispatch);

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-failed-deny", "relay-msg-1", "first partial"));
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-failed-deny",
            RegistrationId = "reg-1",
            RunId = "act-stream-failed-deny",
            SourceActorId = "agent-run",
            Activity = CreateRelayActivity("act-stream-failed-deny", "relay-msg-1"),
            Outbound = new MessageContent { Text = "Sorry, the LLM call failed." },
            TerminalState = LlmReplyTerminalState.Failed,
            ErrorCode = "llm_reply_failed",
            ErrorSummary = "Upstream failure.",
            ReadyAtUnixMs = 100,
        };
        await agent.HandleLlmReplyReadyAsync(ready);
        await CompleteNextNyxRelayTextOperationAsync(agent, dispatch);

        runner.LlmReplyCount.ShouldBe(0);
        var events = await store.GetEventsAsync(agent.Id);
        var completed = ConversationTurnCompletedEvent.Parser.ParseFrom(events.Last().EventData.Value);
        // User keeps the last flushed partial since the edit attempt failed too.
        completed.Outbound.Text.ShouldBe("first partial");
    }

    private static LlmReplyStreamChunkEvent CreateStreamChunk(string correlationId, string replyMessageId, string accumulatedText) =>
        new()
        {
            CorrelationId = correlationId,
            RegistrationId = "reg-1",
            Activity = CreateRelayActivity(correlationId, replyMessageId),
            AccumulatedText = accumulatedText,
            ChunkAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyToken = "runtime-token-" + correlationId,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };

    private static LlmReplyStreamChunkEvent CreateStreamChunkWithoutReplyToken(string correlationId, string replyMessageId, string accumulatedText)
    {
        var chunk = CreateStreamChunk(correlationId, replyMessageId, accumulatedText);
        chunk.ReplyToken = string.Empty;
        chunk.ReplyTokenExpiresAtUnixMs = 0;
        return chunk;
    }

    private static ChatActivity CreateRelayActivity(string correlationId, string replyMessageId) =>
        new()
        {
            Id = correlationId,
            Type = ActivityType.Message,
            ChannelId = new ChannelId { Value = "lark" },
            Bot = new BotInstanceId { Value = "lark-bot" },
            Conversation = new ConversationReference
            {
                Channel = new ChannelId { Value = "lark" },
                Bot = new BotInstanceId { Value = "lark-bot" },
                Scope = ConversationScope.Group,
                CanonicalKey = "conv:lark:grp",
            },
            Content = new MessageContent { Text = "user question" },
            OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = replyMessageId,
                CorrelationId = correlationId,
            },
        };

    private static NyxRelayInboundActivity CreateRelayInbound(
        string activityId,
        string canonicalKey,
        string relayApiKeyId,
        string callbackJti,
        string? nyxUserAccessToken = null,
        long callbackObservedAtUnixMs = 0)
    {
        var activity = CreateActivity(activityId, canonicalKey);
        activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = activityId,
            CorrelationId = callbackJti,
        };
        activity.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = nyxUserAccessToken ?? string.Empty,
        };
        return new NyxRelayInboundActivity
        {
            Activity = activity,
            ReplyToken = "reply-token-" + callbackJti,
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
            CorrelationId = callbackJti,
            RelayApiKeyId = relayApiKeyId,
            CallbackJti = callbackJti,
            CallbackObservedAtUnixMs = callbackObservedAtUnixMs > 0
                ? callbackObservedAtUnixMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CallbackReplayExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };
    }

    private static Task AppendStateEventAsync(
        IEventStore store,
        string agentId,
        IMessage evt,
        long version) =>
        store.AppendAsync(
            agentId,
            [
                new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Version = version,
                    EventType = evt.Descriptor.FullName,
                    EventData = Google.Protobuf.WellKnownTypes.Any.Pack(evt),
                    AgentId = agentId,
                },
            ],
            expectedVersion: version - 1);

    private static ConversationReplyLifecycleChangedEvent LastReplyLifecycleChanged(
        IReadOnlyList<StateEvent> events) =>
        events
            .Where(e => e.EventType == ConversationReplyLifecycleChangedEvent.Descriptor.FullName)
            .Select(e => ConversationReplyLifecycleChangedEvent.Parser.ParseFrom(e.EventData.Value))
            .Last();

    private static void AssertReplyLifecycleTransition(
        ConversationReplyLifecycleChangedEvent evt,
        ConversationReplyLifecycleMode mode,
        string correlationId,
        ConversationReplyLifecyclePhase previousPhase,
        ConversationReplyLifecyclePhase phase)
    {
        evt.Mode.ShouldBe(mode);
        evt.CorrelationId.ShouldBe(correlationId);
        evt.PreviousPhase.ShouldBe(previousPhase);
        evt.Phase.ShouldBe(phase);
        evt.ChangedAtUnixMs.ShouldBeGreaterThan(0);
    }

    private static async Task<(ConversationGAgent agent, IEventStore store)> CreateAgentAsync(
        RecordingTurnRunner runner,
        string agentId,
        IChannelLlmReplyRunDispatcher? dispatcher = null,
        IConversationCardTurnRunner? cardRunner = null,
        IChatRoutePolicyQueryPort? queryPort = null,
        ChatRouteResolver? chatRouteResolver = null,
        IEventStore? store = null,
        IEventPublisher? eventPublisher = null,
        RecordingActorDispatchPort? dispatchPort = null,
        IRuntimeSecretStore? runtimeSecretStore = null)
    {
        store ??= new InMemoryEventStore();
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(store);
        services.AddSingleton<IActorDispatchPort>(dispatchPort ?? new RecordingActorDispatchPort());
        services.AddSingleton<IActorRuntimeCallbackScheduler, RecordingCallbackScheduler>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddSingleton<IConversationTurnRunner>(runner);
        if (cardRunner is not null)
            services.AddSingleton(cardRunner);
        if (dispatcher is not null)
            services.AddSingleton(dispatcher);
        if (queryPort is not null)
            services.AddSingleton(queryPort);
        if (chatRouteResolver is not null)
            services.AddSingleton(chatRouteResolver);
        if (runtimeSecretStore is not null)
            services.AddSingleton(runtimeSecretStore);
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));

        var sp = services.BuildServiceProvider();
        var publisher = eventPublisher ?? new RecordingEventPublisher();
        var agent = new ConversationGAgent
        {
            Services = sp,
            EventPublisher = publisher,
            EventSourcingBehaviorFactory =
                sp.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(),
        };
        SetId(agent, agentId);
        if (publisher is RecordingEventPublisher recordingPublisher)
            recordingPublisher.SelfTarget = agent;
        await agent.ActivateAsync();
        return (agent, store);
    }

    private static void SetId(object agent, string id)
    {
        var type = agent.GetType();
        var prop = type.GetProperty("Id")!;
        var setter = prop.GetSetMethod(nonPublic: true);
        if (setter is not null)
        {
            setter.Invoke(agent, new object?[] { id });
            return;
        }

        // Fall back to walking the base type for the internal SetId method.
        var current = type;
        while (current is not null)
        {
            var setIdMethod = current.GetMethod("SetId",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (setIdMethod is not null)
            {
                setIdMethod.Invoke(agent, new object?[] { id });
                return;
            }
            current = current.BaseType;
        }

        throw new InvalidOperationException("Unable to set agent id via reflection.");
    }

    private static ChatActivity CreateActivity(string id, string canonicalKey) => new()
    {
        Id = id,
        Type = ActivityType.Message,
        ChannelId = new ChannelId { Value = "slack" },
        Bot = new BotInstanceId { Value = "ops-bot" },
        Conversation = new ConversationReference
        {
            Channel = new ChannelId { Value = "slack" },
            Bot = new BotInstanceId { Value = "ops-bot" },
            Scope = ConversationScope.Channel,
            CanonicalKey = canonicalKey,
        },
        Content = new MessageContent { Text = "hi" },
    };

    private static ChatActivity CreateLarkActivity(
        string id,
        string text,
        string canonicalKey,
        string platformMessageId,
        string token) => new()
        {
            Id = id,
            Type = ActivityType.Message,
            ChannelId = new ChannelId { Value = "lark" },
            Bot = new BotInstanceId { Value = "ops-bot" },
            Conversation = new ConversationReference
            {
                Channel = new ChannelId { Value = "lark" },
                Bot = new BotInstanceId { Value = "ops-bot" },
                Scope = ConversationScope.DirectMessage,
                CanonicalKey = canonicalKey,
            },
            Content = new MessageContent { Text = text },
            TransportExtras = new TransportExtras
            {
                NyxPlatform = "lark",
                NyxPlatformMessageId = platformMessageId,
                NyxUserAccessToken = token,
            },
        };

    private static ChatActivity CreateLarkImageActivity(
        string id,
        string text,
        string canonicalKey,
        string platformMessageId,
        string imageKey,
        string token)
    {
        var activity = CreateLarkActivity(id, text, canonicalKey, platformMessageId, token);
        activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = imageKey,
            Kind = AttachmentKind.Image,
            ContentType = "image/png",
            Name = "photo.png",
            SizeBytes = 512,
        });
        return activity;
    }

    private static string ReadRepositoryText(string relativePath) =>
        File.ReadAllText(Path.Combine(GetRepositoryRoot(), relativePath), Encoding.UTF8);

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }

    private static ConversationContinueRequestedEvent CreateContinueCommand(string commandId) => new()
    {
        CommandId = commandId,
        CorrelationId = "corr-1",
        CausationId = string.Empty,
        Kind = PrincipalKind.Bot,
        Conversation = new ConversationReference
        {
            Channel = new ChannelId { Value = "slack" },
            Bot = new BotInstanceId { Value = "ops-bot" },
            Scope = ConversationScope.Channel,
            CanonicalKey = "conv:slack:C1",
        },
        Payload = new MessageContent { Text = "ping" },
        DispatchedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    // Sync (PR #1106 r2): production now requires the LLM reply producer to supply run_id before handoff.
    private static NeedsLlmReplyEvent CreateNeedsLlmReply(
        ChatActivity activity,
        string? targetActorId = null,
        long? requestedAtUnixMs = null,
        string? replyToken = null,
        long replyTokenExpiresAtUnixMs = 0)
    {
        var correlationId = activity.OutboundDelivery?.CorrelationId ?? activity.Id;
        return new NeedsLlmReplyEvent
        {
            CorrelationId = correlationId,
            RunId = correlationId,
            TargetActorId = targetActorId ?? "conversation:actor",
            RegistrationId = "reg-1",
            Activity = activity.Clone(),
            RequestedAtUnixMs = requestedAtUnixMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyToken = replyToken ?? string.Empty,
            ReplyTokenExpiresAtUnixMs = replyTokenExpiresAtUnixMs,
        };
    }

    private static async Task<NyxRelayTextOperationCompletedEvent> CompleteNextNyxRelayTextOperationAsync(
        ConversationGAgent agent,
        RecordingActorDispatchPort dispatchPort)
    {
        var completed = await dispatchPort.WaitForPayloadAsync<NyxRelayTextOperationCompletedEvent>();
        await agent.HandleNyxRelayTextOperationCompletedAsync(completed);
        return completed;
    }

    private sealed class RecordingTurnRunner : IConversationTurnRunner
    {
        public int InboundCount;
        public int LlmReplyCount;
        public int ContinueCount;
        public Func<ChatActivity, ConversationTurnResult>? InboundResultFactory { get; set; }
        public Func<LlmReplyReadyEvent, ConversationTurnResult>? LlmReplyResultFactory { get; set; }
        public Action<ConversationTurnRuntimeContext>? LlmReplyContextObserver { get; set; }
        public Func<ConversationContinueRequestedEvent, ConversationTurnResult>? ContinueResultFactory { get; set; }
        public ChatActivity? LastInboundActivity { get; private set; }
        public ConversationTurnRuntimeContext? LastInboundRuntimeContext { get; private set; }

        public Task<ConversationTurnResult> RunInboundAsync(
            ChatActivity activity,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            Interlocked.Increment(ref InboundCount);
            LastInboundActivity = activity.Clone();
            LastInboundRuntimeContext = runtimeContext;
            var result = InboundResultFactory is null
                ? ConversationTurnResult.Sent("sent:" + activity.Id, new MessageContent { Text = "ack" }, "bot")
                : InboundResultFactory(activity);
            return Task.FromResult(result);
        }

        public Task<ConversationTurnResult> RunLlmReplyAsync(
            LlmReplyReadyEvent reply,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            Interlocked.Increment(ref LlmReplyCount);
            LlmReplyContextObserver?.Invoke(runtimeContext);
            var result = LlmReplyResultFactory is null
                ? ConversationTurnResult.Sent(
                    "sent:llm:" + reply.CorrelationId,
                    reply.Outbound?.Clone() ?? new MessageContent { Text = "ack" },
                    "bot",
                    reply.Activity?.OutboundDelivery?.Clone())
                : LlmReplyResultFactory(reply);
            return Task.FromResult(result);
        }

        public Task<ConversationTurnResult> RunContinueAsync(ConversationContinueRequestedEvent command, CancellationToken ct)
        {
            Interlocked.Increment(ref ContinueCount);
            var result = ContinueResultFactory is null
                ? ConversationTurnResult.Sent("sent:" + command.CommandId, new MessageContent { Text = "ack" }, "bot")
                : ContinueResultFactory(command);
            return Task.FromResult(result);
        }

        public int StreamChunkCount;
        public string? LastStreamChunkCurrentPlatformMessageId { get; private set; }
        public Func<LlmReplyStreamChunkEvent, string?, ConversationStreamChunkResult>? StreamChunkResultFactory { get; set; }

        public Task<ConversationStreamChunkResult> RunStreamChunkAsync(
            LlmReplyStreamChunkEvent chunk,
            string? currentPlatformMessageId,
            NyxRelayTextOperationKind operation,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            Interlocked.Increment(ref StreamChunkCount);
            LastStreamChunkCurrentPlatformMessageId = currentPlatformMessageId;
            var result = StreamChunkResultFactory is null
                ? ConversationStreamChunkResult.Succeeded(
                    currentPlatformMessageId ?? $"om_{chunk.CorrelationId}")
                : StreamChunkResultFactory(chunk, currentPlatformMessageId);
            return Task.FromResult(result);
        }

        public int OnReplyDeliveredCount;
        public ChatActivity? LastOnReplyDeliveredActivity { get; private set; }

        public Task OnReplyDeliveredAsync(ChatActivity activity, CancellationToken ct)
        {
            Interlocked.Increment(ref OnReplyDeliveredCount);
            LastOnReplyDeliveredActivity = activity;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRunDispatcher : IChannelLlmReplyRunDispatcher
    {
        public List<NeedsLlmReplyEvent> Dispatched { get; } = [];

        public Task DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct)
        {
            Dispatched.Add(request.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class FailingRunDispatcher : IChannelLlmReplyRunDispatcher
    {
        public Task DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct) =>
            Task.FromException(new InvalidOperationException("simulated actor dispatch failure"));
    }

    private sealed class StaticChatRoutePolicyQueryPort(ChatRoutePolicySnapshot? snapshot) : IChatRoutePolicyQueryPort
    {
        public static StaticChatRoutePolicyQueryPort ForSnapshot(ChatRoutePolicySnapshot? snapshot) => new(snapshot);

        public Task<ChatRoutePolicySnapshot?> LookupForCallerAsync(
            OwnerScope callerScope,
            CancellationToken ct = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class StaticChatRouteFallbackProvider(string modelName) : IChatRouteFallbackProvider
    {
        public ChatRouteDecision GetFallbackDecision() => new()
        {
            Action = ForwardToModelAction(modelName),
            UsedFallback = true,
            MatchedRuleId = string.Empty,
            ResolvedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
    }

    private static ChatRouteAction ForwardToModelAction(string modelName) => new()
    {
        ForwardToModel = new ForwardToModel { ModelName = modelName },
    };

    private static ChatRouteAction GAgentToolHint(string actorId) => new()
    {
        ForwardToModel = new ForwardToModel
        {
            ToolChoiceHint = new ChatRouteToolChoiceHint
            {
                ToolName = "aevatar_invoke_gagent",
                PrefilledArguments = new Struct
                {
                    Fields =
                    {
                        ["actor_id"] = Value.ForString(actorId),
                    },
                },
            },
        },
    };

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public ConversationGAgent? SelfTarget { get; set; }
        public List<IMessage> Published { get; } = [];
        public List<IMessage> Sent { get; } = [];

        public Task PublishAsync<T>(
            T evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task SendToAsync<T>(
            string targetActorId,
            T evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where T : IMessage
        {
            Sent.Add(evt);
            // Sync (PR #1106 r2): reply operation execution now advances through an actor self-message.
            if (evt is ReplyOperationStepEvent step &&
                SelfTarget is not null &&
                string.Equals(targetActorId, SelfTarget.Id, StringComparison.Ordinal))
                return SelfTarget.HandleReplyOperationStepAsync(step);

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];
        private readonly Queue<EventEnvelope> _pending = new();
        private readonly SemaphoreSlim _available = new(0);

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            var clone = envelope.Clone();
            Dispatches.Add((actorId, clone.Clone()));
            lock (_pending)
            {
                _pending.Enqueue(clone);
            }
            _available.Release();
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public async Task<T> WaitForPayloadAsync<T>(Func<T, bool>? predicate = null)
            where T : IMessage<T>, new()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero || !await _available.WaitAsync(remaining))
                    break;

                EventEnvelope envelope;
                lock (_pending)
                {
                    envelope = _pending.Dequeue();
                }

                if (!envelope.Payload.Is(new T().Descriptor))
                    continue;

                var payload = envelope.Payload.Unpack<T>();
                if (predicate is null || predicate(payload))
                    return payload;
            }

            throw new TimeoutException($"Timed out waiting for dispatched {typeof(T).Name}.");
        }
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
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

}
