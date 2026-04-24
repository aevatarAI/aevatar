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
    public async Task HandleInboundActivityAsync_WhenRunnerReportsFailure_EmitsFailedEvent()
    {
        var runner = new RecordingTurnRunner
        {
            InboundResultFactory = _ => ConversationTurnResult.TransientFailure("rate_limited", "retry later", TimeSpan.FromMilliseconds(250)),
        };
        var (agent, store) = CreateAgent(runner, "conv-6");

        await agent.HandleInboundActivityAsync(CreateActivity("act-fail", "conv:slack:C1"));

        agent.State.ProcessedMessageIds.ShouldBeEmpty();
        var events = await store.GetEventsAsync(agent.Id);
        events.Count.ShouldBe(1);
        events[0].EventType.ShouldContain(nameof(ConversationContinueFailedEvent));
        var parsed = ConversationContinueFailedEvent.Parser.ParseFrom(events[0].EventData.Value);
        parsed.Kind.ShouldBe(FailureKind.TransientAdapterError);
        parsed.RetryAfterMs.ShouldBe(250);
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
            CorrelationId = "corr-inbox-echo",
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
