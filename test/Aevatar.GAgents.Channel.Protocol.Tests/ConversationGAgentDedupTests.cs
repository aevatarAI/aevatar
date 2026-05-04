using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ConversationGAgentDedupTests
{
    [Fact]
    public async Task HandleInboundActivityAsync_WhenDuplicateActivityId_CollapsesToSingleCommit()
    {
        var runner = new RecordingTurnRunner();
        var (agent, store) = CreateAgent(runner, "conv-1");

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
        var (agent, store) = CreateAgent(runner, "conv-2");

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
        var (agent, _) = CreateAgent(runner, "conv-3");

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
        var (agent, store) = CreateAgent(runner, "conv-4");

        var cmd = CreateContinueCommand("cmd-1");
        await agent.HandleContinueCommandAsync(cmd);
        await agent.HandleContinueCommandAsync(cmd.Clone());

        runner.ContinueCount.ShouldBe(1);
        agent.State.ProcessedCommandIds.ShouldContain("cmd-1");

        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(2);

        var rejected = events.Skip(1).First();
        rejected.EventType.ShouldContain(nameof(ConversationContinueRejectedEvent));
        var parsed = ConversationContinueRejectedEvent.Parser.ParseFrom(rejected.EventData.Value);
        parsed.Reason.ShouldBe(RejectReason.DuplicateCommand);
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenCapExceeded_RemovesOldestDedupEntry()
    {
        var runner = new RecordingTurnRunner();
        var (agent, _) = CreateAgent(runner, "conv-5");

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
        var (agent, store) = CreateAgent(runner, "conv-6");

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
        var (agent, store) = CreateAgent(runner, "conv-retry-success");

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
        var (agent, store) = CreateAgent(runner, "conv-retry-exhaust");

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
                return ConversationTurnResult.LlmReplyRequested(
                    new NeedsLlmReplyEvent
                    {
                        CorrelationId = activity.Id,
                        TargetActorId = "conversation:actor",
                        RegistrationId = "reg-1",
                        Activity = activity.Clone(),
                        RequestedAtUnixMs = 7,
                    });
            },
        };
        var (agent, store) = CreateAgent(runner, "conv-llm-supersedes-retry");

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
        var (agent, store) = CreateAgent(runner, "conv-permanent-inbound");

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
        var (agent, store) = CreateAgent(runner, "conv-relay");

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
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = 42,
                }),
        };
        var (agent, store) = CreateAgent(runner, "conv-llm-request");

        await agent.HandleInboundActivityAsync(CreateActivity("act-llm", "conv:slack:C1"));

        agent.State.ProcessedMessageIds.ShouldContain("act-llm");
        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(NeedsLlmReplyEvent));
        var parsed = NeedsLlmReplyEvent.Parser.ParseFrom(events[0].EventData.Value);
        parsed.CorrelationId.ShouldBe("act-llm");
        parsed.Activity.Id.ShouldBe("act-llm");
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenDuplicateCorrelationId_CollapsesToSingleOutboundCommit()
    {
        var runner = new RecordingTurnRunner();
        var (agent, store) = CreateAgent(runner, "conv-llm-ready");
        await agent.HandleInboundActivityAsync(CreateActivity("act-llm-ready", "conv:slack:C1"));

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-llm-ready",
            RegistrationId = "reg-1",
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
        events.Count.ShouldBe(2);
        events.Last().EventType.ShouldContain(nameof(ConversationTurnCompletedEvent));
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
        var (agent, store) = CreateAgent(runner, "conv-7");

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
        var (agent, store) = CreateAgent(runner, "conv-9");

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
    public async Task HandleInboundActivityAsync_WhenInboxIsRegistered_DispatchesDirectlyWithoutWaitingForReminder()
    {
        // Regression: previously the inbound LlmReplyRequest path scheduled a 100ms durable
        // Reminder before EnqueueAsync, which Orleans rounded up to ~1 minute and effectively
        // dropped the dispatch in production. The inbound path must call inbox.EnqueueAsync
        // inline so the LLM worker picks it up immediately.
        var inbox = new RecordingInbox();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = 42,
                }),
        };
        var (agent, _) = CreateAgent(runner, "conv-direct-dispatch", inbox);

        await agent.HandleInboundActivityAsync(CreateActivity("act-direct", "conv:slack:C1"));

        inbox.Enqueued.Count.ShouldBe(1);
        inbox.Enqueued[0].CorrelationId.ShouldBe("act-direct");
        inbox.Enqueued[0].TargetActorId.ShouldBe(agent.Id);
    }

    [Fact]
    public async Task HandleNyxRelayInboundActivityAsync_NeverPersistsReplyTokenIntoEventStore()
    {
        // Issue #366 §4 invariant: relay reply_token must stay actor-owned runtime state.
        // The transient inbox envelope NyxRelayInboundActivity carries the token across the
        // dispatch boundary, but the actor must not write it into any persisted event payload.
        const string sentinelReplyToken = "sentinel-reply-token-9f3c5b2e-must-not-persist";
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.OutboundDelivery?.CorrelationId ?? activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = 42,
                }),
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
        var (agent, store) = CreateAgent(runner, "conv-relay-token-leak");

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
    public async Task HandleDeferredLlmReplyDispatchRequestedAsync_RehydratesRelayTokenUsingOutboundDeliveryCorrelation()
    {
        // NyxID's message_id and callback correlation_id are distinct. Pending LLM
        // requests are tracked by message_id, while reply tokens are keyed by the
        // callback correlation_id carried in OutboundDelivery.
        const string sentinelReplyToken = "sentinel-retry-token-7c10";
        var inbox = new RecordingInbox();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.Id,
                    TargetActorId = "stale-unscoped-actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }),
        };
        var (agent, _) = CreateAgent(runner, "channel-conversation:conv:slack:C1:scope:owner", inbox);

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

        inbox.Enqueued.Count.ShouldBe(1);
        inbox.Enqueued[0].ReplyToken.ShouldBe(sentinelReplyToken);
        inbox.Enqueued[0].TargetActorId.ShouldBe(agent.Id);
        inbox.Enqueued.Clear();

        await agent.HandleDeferredLlmReplyDispatchRequestedAsync(new DeferredLlmReplyDispatchRequestedEvent
        {
            CorrelationId = "nyx-msg-1",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        inbox.Enqueued.Count.ShouldBe(1);
        inbox.Enqueued[0].CorrelationId.ShouldBe("nyx-msg-1");
        inbox.Enqueued[0].ReplyToken.ShouldBe(sentinelReplyToken);
        inbox.Enqueued[0].TargetActorId.ShouldBe(agent.Id);
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_RemovesRelayTokenUsingOutboundDeliveryCorrelation()
    {
        const string sentinelReplyToken = "sentinel-cleanup-token-6d41";
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.Id,
                    TargetActorId = "stale-unscoped-actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }),
        };
        var (agent, _) = CreateAgent(runner, "channel-conversation:conv:slack:C1:scope:owner");

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

        GetNyxRelayReplyTokenCount(agent).ShouldBe(1);

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "nyx-msg-cleanup",
            RegistrationId = "reg-1",
            SourceActorId = "llm-worker-1",
            Activity = inboundActivity.Clone(),
            Outbound = new MessageContent { Text = "reply-from-llm" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        GetNyxRelayReplyTokenCount(agent).ShouldBe(0);
    }

    [Fact]
    public async Task HandleInboundActivityAsync_StripsReplyTokenFromPersistedNeedsLlmReplyEvent_ButKeepsItOnInboxCopy()
    {
        // Strip-on-persist invariant: NeedsLlmReplyEvent must keep reply_token on the
        // copy enqueued to inbox so the LLM worker can echo it back, but the persisted
        // copy that lands in event store must omit it.
        const string sentinelReplyToken = "sentinel-strip-on-persist-1f8b3";
        var inbox = new RecordingInbox();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.OutboundDelivery?.CorrelationId ?? activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ReplyToken = sentinelReplyToken,
                    ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
                }),
        };
        var (agent, store) = CreateAgent(runner, "conv-strip-token", inbox);

        var inboundActivity = CreateActivity("act-strip", "conv:slack:C1");
        inboundActivity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-strip",
            CorrelationId = "corr-strip",
        };
        await agent.HandleInboundActivityAsync(inboundActivity);

        inbox.Enqueued.Count.ShouldBe(1);
        inbox.Enqueued[0].ReplyToken.ShouldBe(sentinelReplyToken);

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
    public async Task HandleDeferredLlmReplyDispatchRequestedAsync_ReEnrichesStrippedPendingRequestWithActorRuntimeToken()
    {
        // Regression for Codex review: the persisted NeedsLlmReplyEvent in
        // State.PendingLlmReplyRequests always has an empty ReplyToken (strip-on-persist).
        // On the retry / durable-reminder path we walk that state, so the inbox must see
        // the token re-enriched from the actor's in-memory dict while the activation is
        // still alive. Without enrichment the inbox subscriber's relay gate would drop
        // the retry and permanently lose the reply.
        const string sentinelReplyToken = "sentinel-retry-enrich-b3d7a";
        var inbox = new RecordingInbox();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.OutboundDelivery?.CorrelationId ?? activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ReplyToken = sentinelReplyToken,
                    ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
                }),
        };
        var (agent, _) = CreateAgent(runner, "conv-retry-enrich", inbox);

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

        // Inbound capture populates the actor runtime dict and enqueues with ReplyToken set directly.
        await agent.HandleNyxRelayInboundActivityAsync(relayInbound);
        inbox.Enqueued.Count.ShouldBe(1);
        inbox.Enqueued[0].ReplyToken.ShouldBe(sentinelReplyToken);

        // Simulate the durable-reminder retry firing: pendingRequest is read from state
        // where ReplyToken was stripped. DispatchPendingLlmReplyAsync must re-enrich
        // from the actor dict so the inbox still receives the token.
        await agent.HandleDeferredLlmReplyDispatchRequestedAsync(new DeferredLlmReplyDispatchRequestedEvent
        {
            CorrelationId = "corr-retry",
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        inbox.Enqueued.Count.ShouldBe(2);
        inbox.Enqueued[1].ReplyToken.ShouldBe(sentinelReplyToken);
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_PrefersInboxEchoedReplyToken_OverActorRuntimeDict()
    {
        // After a pod restart the in-memory _nyxRelayReplyTokens dict is empty, so the
        // outbound reply must be able to consume the inbox-echoed reply_token from
        // LlmReplyReadyEvent directly. Capture the token observed by the runner to confirm.
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
        var (agent, _) = CreateAgent(runner, "conv-inbox-echo");

        var activity = CreateActivity("act-inbox-echo", "conv:slack:C1");
        activity.OutboundDelivery = new OutboundDeliveryContext
        {
            ReplyMessageId = "relay-msg-echo",
            CorrelationId = "corr-inbox-echo",
        };

        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "nyx-msg-inbox-echo",
            RegistrationId = "reg-1",
            SourceActorId = "llm-worker-1",
            Activity = activity.Clone(),
            Outbound = new MessageContent { Text = "reply-from-llm" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyToken = "inbox-echoed-token",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeMilliseconds(),
        });

        observedContext.ShouldNotBeNull();
        observedContext!.NyxRelayReplyToken.ShouldNotBeNull();
        observedContext.NyxRelayReplyToken!.ReplyToken.ShouldBe("inbox-echoed-token");
        observedContext.NyxRelayReplyToken.CorrelationId.ShouldBe("corr-inbox-echo");
        observedContext.NyxRelayReplyToken.ReplyMessageId.ShouldBe("relay-msg-echo");
    }

    [Fact]
    public async Task HandleDeferredLlmReplyDroppedAsync_RetiresPendingRequestWithNotRetryableFailure()
    {
        // Inbox-side gates (stale-age, missing relay credential, malformed payload) need
        // a way to tell the actor "stop tracking this pending request" so it doesn't
        // silently accumulate in State.PendingLlmReplyRequests until the next
        // rehydration. The actor's drop handler emits a NotRetryable
        // ConversationContinueFailedEvent which routes through the existing state
        // matcher to remove the pending entry.
        var inbox = new RecordingInbox();
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = activity => ConversationTurnResult.LlmReplyRequested(
                new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.OutboundDelivery?.CorrelationId ?? activity.Id,
                    TargetActorId = "conversation:actor",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ReplyToken = "drop-test-token",
                    ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
                }),
        };
        var (agent, store) = CreateAgent(runner, "conv-drop-clears", inbox);

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
            Reason = "stale_inbox_request_dropped",
            DroppedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        agent.State.PendingLlmReplyRequests.ShouldNotContain(req => req.CorrelationId == "corr-drop");
        var events = await store.GetEventsAsync(agent.Id);
        var lastEvent = events[^1];
        lastEvent.EventType.ShouldContain(nameof(ConversationContinueFailedEvent));
        var failed = ConversationContinueFailedEvent.Parser.ParseFrom(lastEvent.EventData.Value);
        failed.ErrorCode.ShouldBe("stale_inbox_request_dropped");
        failed.RetryPolicyCase.ShouldBe(ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public async Task HandleDeferredLlmReplyDroppedAsync_IgnoresUnknownCorrelationId()
    {
        var (agent, store) = CreateAgent(new RecordingTurnRunner(), "conv-drop-unknown");
        var initialEvents = (await store.GetEventsAsync(agent.Id)).Count;

        await agent.HandleDeferredLlmReplyDroppedAsync(new DeferredLlmReplyDroppedEvent
        {
            CorrelationId = "corr-not-pending",
            Reason = "stale_inbox_request_dropped",
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
        var (agent, _) = CreateAgent(runner, "conv-8");

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
        var (agent, _) = CreateAgent(runner, "conv-stream-first");
        SeedReplyToken(agent, "act-stream", "token-1", "relay-msg-1");

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream", "relay-msg-1", "hello"));

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
        var (agent, _) = CreateAgent(runner, "conv-stream-2");
        SeedReplyToken(agent, "act-stream-2", "token-1", "relay-msg-1");

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-2", "relay-msg-1", "first chunk"));
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-2", "relay-msg-1", "first chunk plus more"));

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
        var (agent, _) = CreateAgent(runner, "conv-stream-fail");
        SeedReplyToken(agent, "act-stream-fail", "token-1", "relay-msg-1");

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-fail", "relay-msg-1", "first"));
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
        var (agent, _) = CreateAgent(runner, "conv-stream-no-token");

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-no-token", "relay-msg-1", "hello"));

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
        var (agent, store) = CreateAgent(runner, "conv-stream-short-circuit");
        SeedReplyToken(agent, "act-stream-sc", "token-1", "relay-msg-1");

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-sc", "relay-msg-1", "final text"));

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-sc",
            RegistrationId = "reg-1",
            SourceActorId = "llm-inbox",
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
    }

    [Fact]
    public async Task HandleLlmReplyReadyAsync_WhenStreamingDisabled_FallsBackToRunLlmReplyAsync()
    {
        var runner = new RecordingTurnRunner
        {
            StreamChunkResultFactory = (_, _) =>
                ConversationStreamChunkResult.Failed("relay_reply_edit_unsupported", "nope", editUnsupported: true),
        };
        var (agent, store) = CreateAgent(runner, "conv-stream-fallback");
        SeedReplyToken(agent, "act-stream-fb", "token-1", "relay-msg-1");

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-fb", "relay-msg-1", "partial"));

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-fb",
            RegistrationId = "reg-1",
            SourceActorId = "llm-inbox",
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
        events.Last().EventType.ShouldContain(nameof(ConversationTurnCompletedEvent));
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
        var (agent, _) = CreateAgent(runner, "conv-stream-suppress");
        SeedReplyToken(agent, "act-stream-suppress", "token-1", "relay-msg-1");

        // First chunk consumes the reply token.
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-suppress", "relay-msg-1", "hello"));
        // Interim edit fails after token consumed.
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-suppress", "relay-msg-1", "hello world"));
        // Later interim chunk must be dropped (not dispatched to runner).
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-suppress", "relay-msg-1", "hello world again"));

        callCount.ShouldBe(2);
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
        var (agent, store) = CreateAgent(runner, "conv-stream-final-retry");
        SeedReplyToken(agent, "act-stream-final-retry", "token-1", "relay-msg-1");

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-final-retry", "relay-msg-1", "hello"));
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-final-retry", "relay-msg-1", "hello world"));

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-final-retry",
            RegistrationId = "reg-1",
            SourceActorId = "llm-inbox",
            Activity = CreateRelayActivity("act-stream-final-retry", "relay-msg-1"),
            Outbound = new MessageContent { Text = "hello world final" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
        };
        await agent.HandleLlmReplyReadyAsync(ready);

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
        var (agent, store) = CreateAgent(runner, "conv-stream-final-degraded");
        SeedReplyToken(agent, "act-stream-final-degraded", "token-1", "relay-msg-1");

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-final-degraded", "relay-msg-1", "hello partial"));
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-final-degraded", "relay-msg-1", "hello partial more"));

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-final-degraded",
            RegistrationId = "reg-1",
            SourceActorId = "llm-inbox",
            Activity = CreateRelayActivity("act-stream-final-degraded", "relay-msg-1"),
            Outbound = new MessageContent { Text = "hello partial more final" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 100,
        };
        await agent.HandleLlmReplyReadyAsync(ready);

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
        var (agent, store) = CreateAgent(runner, "conv-stream-failed-edit");
        SeedReplyToken(agent, "act-stream-failed", "token-1", "relay-msg-1");

        // First chunk lands the placeholder + consumes the reply token.
        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-failed", "relay-msg-1", "..."));

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-failed",
            RegistrationId = "reg-1",
            SourceActorId = "llm-inbox",
            Activity = CreateRelayActivity("act-stream-failed", "relay-msg-1"),
            // Inbox runtime classifies the LLM exception into a user-facing
            // message and stuffs it into Outbound.Text on the Failed event.
            Outbound = new MessageContent { Text = "Sorry, the upstream model is rate limited (HTTP 429). Please try again in a moment." },
            TerminalState = LlmReplyTerminalState.Failed,
            ErrorCode = "llm_reply_failed",
            ErrorSummary = "Upstream LLM rate limited.",
            ReadyAtUnixMs = 100,
        };
        await agent.HandleLlmReplyReadyAsync(ready);

        // Must NOT fall through to RunLlmReplyAsync (would 401 on the dead token).
        runner.LlmReplyCount.ShouldBe(0);
        // Two RunStreamChunkAsync calls: first chunk + failure-edit.
        callCount.ShouldBe(2);
        // The placeholder was edited with the classified failure text.
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
        var (agent, store) = CreateAgent(runner, "conv-stream-failed-edit-deny");
        SeedReplyToken(agent, "act-stream-failed-deny", "token-1", "relay-msg-1");

        await agent.HandleLlmReplyStreamChunkAsync(
            CreateStreamChunk("act-stream-failed-deny", "relay-msg-1", "first partial"));

        var ready = new LlmReplyReadyEvent
        {
            CorrelationId = "act-stream-failed-deny",
            RegistrationId = "reg-1",
            SourceActorId = "llm-inbox",
            Activity = CreateRelayActivity("act-stream-failed-deny", "relay-msg-1"),
            Outbound = new MessageContent { Text = "Sorry, the LLM call failed." },
            TerminalState = LlmReplyTerminalState.Failed,
            ErrorCode = "llm_reply_failed",
            ErrorSummary = "Upstream failure.",
            ReadyAtUnixMs = 100,
        };
        await agent.HandleLlmReplyReadyAsync(ready);

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
        };

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

    private static void SeedReplyToken(ConversationGAgent agent, string correlationId, string replyToken, string replyMessageId)
    {
        var field = typeof(ConversationGAgent).GetField(
            "_nyxRelayReplyTokens",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var dict = (Dictionary<string, NyxRelayReplyTokenContext>)field.GetValue(agent)!;
        dict[correlationId] = new NyxRelayReplyTokenContext(
            correlationId,
            replyToken,
            replyMessageId,
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static (ConversationGAgent agent, IEventStore store) CreateAgent(
        RecordingTurnRunner runner,
        string agentId,
        IChannelLlmReplyInbox? inbox = null)
    {
        var store = new InMemoryEventStore();
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(store);
        services.AddSingleton<IActorRuntimeCallbackScheduler, RecordingCallbackScheduler>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddSingleton<IConversationTurnRunner>(runner);
        if (inbox is not null)
            services.AddSingleton(inbox);
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));

        var sp = services.BuildServiceProvider();
        var agent = new ConversationGAgent
        {
            Services = sp,
            EventSourcingBehaviorFactory =
                sp.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(),
        };
        SetId(agent, agentId);
        agent.ActivateAsync().GetAwaiter().GetResult();
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

    private static int GetNyxRelayReplyTokenCount(ConversationGAgent agent)
    {
        var field = typeof(ConversationGAgent).GetField(
            "_nyxRelayReplyTokens",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field.ShouldNotBeNull();
        var value = field.GetValue(agent);
        value.ShouldNotBeNull();
        return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
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

    private sealed class RecordingTurnRunner : IConversationTurnRunner
    {
        public int InboundCount;
        public int LlmReplyCount;
        public int ContinueCount;
        public Func<ChatActivity, ConversationTurnResult>? InboundResultFactory { get; set; }
        public Func<LlmReplyReadyEvent, ConversationTurnResult>? LlmReplyResultFactory { get; set; }
        public Action<ConversationTurnRuntimeContext>? LlmReplyContextObserver { get; set; }
        public Func<ConversationContinueRequestedEvent, ConversationTurnResult>? ContinueResultFactory { get; set; }

        public Task<ConversationTurnResult> RunInboundAsync(
            ChatActivity activity,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            Interlocked.Increment(ref InboundCount);
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

    private sealed class RecordingInbox : IChannelLlmReplyInbox
    {
        public List<NeedsLlmReplyEvent> Enqueued { get; } = [];

        public Task EnqueueAsync(NeedsLlmReplyEvent request, CancellationToken ct)
        {
            Enqueued.Add(request.Clone());
            return Task.CompletedTask;
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
