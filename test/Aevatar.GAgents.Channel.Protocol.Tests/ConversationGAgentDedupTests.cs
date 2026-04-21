using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
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

    private static (ConversationGAgent agent, IEventStore store) CreateAgent(RecordingTurnRunner runner, string agentId)
    {
        var store = new InMemoryEventStore();
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(store);
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddSingleton<IConversationTurnRunner>(runner);
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
        public int ContinueCount;
        public Func<ChatActivity, ConversationTurnResult>? InboundResultFactory { get; set; }
        public Func<ConversationContinueRequestedEvent, ConversationTurnResult>? ContinueResultFactory { get; set; }

        public Task<ConversationTurnResult> RunInboundAsync(ChatActivity activity, CancellationToken ct)
        {
            Interlocked.Increment(ref InboundCount);
            var result = InboundResultFactory is null
                ? ConversationTurnResult.Sent("sent:" + activity.Id, new MessageContent { Text = "ack" }, "bot")
                : InboundResultFactory(activity);
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
}
