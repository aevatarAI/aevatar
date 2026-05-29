using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ChatRunActorTests
{
    [Fact]
    public async Task StartAndTerminate_ShouldInitializeSessionState_AndClearActiveSubscriptions()
    {
        var actor = CreateActor("resp_1");
        var startedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-23T00:00:00+00:00"));

        await actor.HandleStartAsync(new StartChatRunRequested
        {
            ResponseId = "resp_1",
            ModelName = "gpt-test",
            IdleTtl = Duration.FromTimeSpan(TimeSpan.FromMinutes(5)),
            StartedAt = startedAt,
            Messages =
            {
                new ChatRunMessageRecord
                {
                    Role = "user",
                    Content = "hello",
                },
            },
        });
        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_complete",
            runId: "run_complete",
            waitMode: ChatRunSubRunWaitMode.Complete));

        await actor.HandleTerminateAsync(new TerminateChatRunRequested
        {
            Reason = "client_closed",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-23T00:01:00+00:00")),
        });

        actor.State.ResponseId.Should().Be("resp_1");
        actor.State.ModelName.Should().Be("gpt-test");
        actor.State.Messages.Should().ContainSingle(message => message.Role == "user" && message.Content == "hello");
        actor.State.IdleTtl.Should().Be(Duration.FromTimeSpan(TimeSpan.FromMinutes(5)));
        actor.State.InitializedAt.Should().Be(startedAt);
        actor.State.Terminated.Should().BeTrue();
        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitWaitStreamToolCall_ShouldRecordHistory_WithoutCreatingActiveSubscription()
    {
        var actor = await StartedActorAsync("resp_stream");

        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_stream",
            runId: "run_stream",
            waitMode: ChatRunSubRunWaitMode.Stream,
            streamTopic: "aevatar://actors/actor-1/runs/run_stream",
            resultJson: """
                {"run_id":"run_stream","stream_topic":"aevatar://actors/actor-1/runs/run_stream","wait":"stream"}
                """));

        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
        var history = actor.State.ToolCallHistory.Should().ContainSingle().Subject;
        history.ToolCallId.Should().Be("call_stream");
        history.RunId.Should().Be("run_stream");
        history.InternalResultJson.Should().Contain("aevatar://actors/actor-1/runs/run_stream");
        history.LlmRound.Should().Be(1);
    }

    [Fact]
    public async Task SubmitWaitCompleteThenTerminal_ShouldFoldToolResult_AndAdvanceRound()
    {
        var actor = await StartedActorAsync("resp_complete");

        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_complete",
            runId: "run_complete",
            waitMode: ChatRunSubRunWaitMode.Complete,
            llmRound: 2));

        actor.State.ActiveSubRunSubscriptions.Should().ContainSingle(subscription =>
            subscription.RunId == "run_complete" &&
            subscription.CallerToolCallId == "call_complete" &&
            subscription.WaitMode == ChatRunSubRunWaitMode.Complete);

        await actor.HandleSubRunTerminalAsync(new ChatRunSubRunTerminalObserved
        {
            RunId = "run_complete",
            Status = "RunFinished",
            InternalResultJson = """{"run_id":"run_complete","status":"RunFinished","content":"done"}""",
        });

        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
        actor.State.ToolCallHistory.Should().ContainSingle()
            .Which.InternalResultJson.Should().Contain("\"content\":\"done\"");
        actor.State.Messages.Should().ContainSingle(message =>
            message.Role == "tool" &&
            message.ToolCallId == "call_complete" &&
            message.Content.Contains("\"content\":\"done\"", StringComparison.Ordinal));
        actor.State.CurrentLlmRound.Should().Be(3);
    }

    [Fact]
    public async Task State_ShouldExposeTypedSubRunSubscriptionShape()
    {
        var actor = await StartedActorAsync("resp_typed");

        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_complete",
            runId: "run_typed",
            waitMode: ChatRunSubRunWaitMode.Complete));

        typeof(ChatRunState)
            .GetProperty(nameof(ChatRunState.ActiveSubRunSubscriptions))!
            .PropertyType
            .Should()
            .Be(typeof(RepeatedField<ChatRunSubRunSubscription>));
        var subscription = actor.State.ActiveSubRunSubscriptions.Should().ContainSingle().Subject;
        subscription.RunId.Should().Be("run_typed");
        subscription.TargetKind.Should().Be(ChatRunSubRunTargetKind.Gagent);
        subscription.TargetId.Should().Be("actor-1");
        subscription.StreamTopic.Should().Be("aevatar://actors/actor-1/runs/run_typed");
    }

    [Fact]
    public async Task MultipleWaitCompleteSubRuns_ShouldCorrelateTerminalEventsByRunId()
    {
        var actor = await StartedActorAsync("resp_multi");

        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_first",
            runId: "run_first",
            waitMode: ChatRunSubRunWaitMode.Complete));
        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_second",
            runId: "run_second",
            waitMode: ChatRunSubRunWaitMode.Complete));

        actor.State.ActiveSubRunSubscriptions.Select(static subscription => subscription.RunId)
            .Should()
            .Equal("run_first", "run_second");

        await actor.HandleSubRunTerminalAsync(new ChatRunSubRunTerminalObserved
        {
            RunId = "run_second",
            InternalResultJson = """{"run_id":"run_second","content":"second"}""",
        });

        actor.State.ActiveSubRunSubscriptions.Should().ContainSingle()
            .Which.RunId.Should().Be("run_first");
        actor.State.Messages.Should().ContainSingle(message =>
            message.Role == "tool" &&
            message.ToolCallId == "call_second" &&
            message.Content.Contains("second", StringComparison.Ordinal));

        await actor.HandleSubRunTerminalAsync(new ChatRunSubRunTerminalObserved
        {
            RunId = "run_first",
            InternalResultJson = """{"run_id":"run_first","content":"first"}""",
        });

        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
        actor.State.Messages
            .Where(static message => message.Role == "tool")
            .Select(static message => message.ToolCallId)
            .Should()
            .Equal("call_second", "call_first");
        actor.State.ToolCallHistory.Single(call => call.RunId == "run_first")
            .InternalResultJson.Should().Contain("first");
        actor.State.ToolCallHistory.Single(call => call.RunId == "run_second")
            .InternalResultJson.Should().Contain("second");
    }

    [Fact]
    public async Task Terminate_ShouldRemoveRelayAsync_ForEachActiveSubscription()
    {
        var streamProvider = new RecordingStreamProvider();
        var actor = await StartedActorAsync("resp_relay_cleanup", streamProvider);

        await actor.HandlePrepareSubRunObservationAsync(BuildPrepareObservation(
            toolCallId: "call_first",
            runId: "run_first",
            actorId: "actor-1"));
        await actor.HandlePrepareSubRunObservationAsync(BuildPrepareObservation(
            toolCallId: "call_second",
            runId: "run_second",
            actorId: "actor-2"));

        streamProvider.Upserts
            .Select(static call => (call.StreamId, call.Binding.TargetStreamId))
            .Should()
            .Equal(("actor-1", actor.Id), ("actor-2", actor.Id));
        actor.State.ActiveSubRunSubscriptions.Should().HaveCount(2);

        await actor.HandleTerminateAsync(new TerminateChatRunRequested
        {
            Reason = "client_closed",
        });

        streamProvider.Removes.Should().Equal(
            ("actor-1", actor.Id),
            ("actor-2", actor.Id));
        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Terminate_WhenRemoveRelayThrows_ShouldStillClearState()
    {
        var streamProvider = new RecordingStreamProvider
        {
            ThrowOnRemove = true,
        };
        var logger = new RecordingLogger();
        var actor = await StartedActorAsync("resp_relay_cleanup_throw", streamProvider);
        actor.Logger = logger;

        await actor.HandlePrepareSubRunObservationAsync(BuildPrepareObservation(
            toolCallId: "call_first",
            runId: "run_first",
            actorId: "actor-1"));
        await actor.HandlePrepareSubRunObservationAsync(BuildPrepareObservation(
            toolCallId: "call_second",
            runId: "run_second",
            actorId: "actor-2"));

        await actor.HandleTerminateAsync(new TerminateChatRunRequested
        {
            Reason = "client_closed",
        });

        streamProvider.Removes.Should().Equal(
            ("actor-1", actor.Id),
            ("actor-2", actor.Id));
        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
        logger.Entries.Should().HaveCount(2);
        logger.Entries.Should().OnlyContain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is InvalidOperationException &&
            entry.Message.Contains("Failed to remove chat run sub-run observation relay", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DispatchPortPath_ShouldPublishToolResultReadyOnSelfStream()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddAevatarRuntime()
            .AddSingleton<IChatRunActorPort, ChatRunActorAdapter>()
            .BuildServiceProvider();
        var port = services.GetRequiredService<IChatRunActorPort>();
        var subscriptionProvider = services.GetRequiredService<IActorEventSubscriptionProvider>();
        var runtime = services.GetRequiredService<IActorRuntime>();
        var ready = new TaskCompletionSource<ChatRunToolResultReady>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var actorId = await port.StartAsync(new ChatRunStartRequest(
            "resp_publisher",
            "gpt-test",
            [ChatMessage.User("hello")]));
        await using var subscription = await subscriptionProvider.SubscribeAsync<ChatRunToolResultReady>(
            actorId,
            message =>
            {
                ready.TrySetResult(message.Clone());
                return Task.CompletedTask;
            });

        await port.SubmitToolCallAsync(
            actorId,
            new ChatRunToolCompletionRequest(
                "resp_publisher",
                "gpt-test",
                [ChatMessage.User("hello")],
                new ToolCall
                {
                    Id = "call_publish",
                    Name = "aevatar_invoke_gagent",
                    ArgumentsJson = """{"actor_id":"actor-1","wait":"complete"}""",
                },
                """{"actor_id":"actor-1","wait":"complete"}""",
                """{"opaque":"tool-output"}""",
                1,
                RunId: "run_publish",
                StreamTopic: "aevatar://actors/actor-1/runs/run_publish",
                ActorId: "actor-1",
                WaitMode: ChatRunSubRunWaitMode.Complete));
        await port.ObserveSubRunTerminalAsync(actorId, new ChatRunSubRunTerminalObserved
        {
            RunId = "run_publish",
            Status = "RunFinished",
            InternalResultJson = """{"run_id":"run_publish","content":"published"}""",
            ActorId = "actor-1",
            CompletionObserved = true,
        });

        var published = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        published.RunId.Should().Be("run_publish");
        published.CallerToolCallId.Should().Be("call_publish");
        published.InternalResultJson.Should().Contain("published");
        published.Status.Should().Be("RunFinished");
        published.ActorId.Should().Be("actor-1");
        published.CompletionObserved.Should().BeTrue();

        var actor = await runtime.GetAsync(actorId);
        ((ChatRunActor)actor!.Agent).State.ActiveSubRunSubscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task ForwardedCommittedCompletion_ShouldPublishObservedToolResultReady()
    {
        var eventStore = new InMemoryEventStore();
        var actor = await StartedActorAsync("resp_forwarded_completion", eventStore);

        await actor.HandleSubmitToolCallAsync(BuildSubmit(
            toolCallId: "call_forwarded",
            runId: "run_forwarded",
            waitMode: ChatRunSubRunWaitMode.Complete,
            streamTopic: "aevatar://actors/role-actor-1/runs/run_forwarded",
            resultJson: """{"run_id":"run_forwarded","actor_id":"role-actor-1","wait":"complete"}""",
            actorId: "role-actor-1",
            llmRound: 3));

        await actor.HandleObservedEnvelopeAsync(BuildForwardedCommittedCompletionEnvelope(
            sourceActorId: "role-actor-1",
            targetActorId: actor.Id,
            runId: "run_forwarded",
            content: "forwarded done"));

        actor.State.ActiveSubRunSubscriptions.Should().BeEmpty();
        var folded = actor.State.ToolCallHistory.Should().ContainSingle().Subject;
        folded.RunId.Should().Be("run_forwarded");
        folded.InternalResultJson.Should().Contain("\"content\":\"forwarded done\"");

        var foldedEvent = (await eventStore.GetEventsAsync(actor.Id))
            .Select(static evt => evt.EventData)
            .Where(static payload => payload.Is(ChatRunSubRunTerminalFoldedEvent.Descriptor))
            .Select(static payload => payload.Unpack<ChatRunSubRunTerminalFoldedEvent>())
            .Should()
            .ContainSingle()
            .Subject;
        foldedEvent.Status.Should().Be(GAgentRunTerminalStatus.TextMessageCompleted.ToString());
        foldedEvent.ActorId.Should().Be("role-actor-1");
        foldedEvent.CompletionObserved.Should().BeTrue();
    }

    private static ChatRunActor CreateActor(string responseId) =>
        CreateActor(responseId, new InMemoryEventStore());

    private static ChatRunActor CreateActor(string responseId, InMemoryEventStore eventStore) =>
        GAgentServiceTestKit.CreateStatefulAgent<ChatRunActor, ChatRunState>(
            eventStore,
            "chat-run-" + responseId,
            static () => new ChatRunActor());

    private static async Task<ChatRunActor> StartedActorAsync(string responseId)
    {
        var actor = CreateActor(responseId);
        await StartActorAsync(actor, responseId);
        return actor;
    }

    private static async Task<ChatRunActor> StartedActorAsync(
        string responseId,
        InMemoryEventStore eventStore)
    {
        var actor = CreateActor(responseId, eventStore);
        await StartActorAsync(actor, responseId);
        return actor;
    }

    private static async Task<ChatRunActor> StartedActorAsync(
        string responseId,
        RecordingStreamProvider streamProvider)
    {
        var actor = CreateActor(responseId);
        actor.Services = new ServiceCollection()
            .AddSingleton<IStreamProvider>(streamProvider)
            .BuildServiceProvider();
        await StartActorAsync(actor, responseId);
        return actor;
    }

    private static async Task StartActorAsync(ChatRunActor actor, string responseId)
    {
        await actor.HandleStartAsync(new StartChatRunRequested
        {
            ResponseId = responseId,
            ModelName = "gpt-test",
            Messages =
            {
                new ChatRunMessageRecord
                {
                    Role = "user",
                    Content = "hello",
                },
            },
        });
    }

    private static SubmitChatRunToolCallRequested BuildSubmit(
        string toolCallId,
        string runId,
        ChatRunSubRunWaitMode waitMode,
        string streamTopic = "",
        string resultJson = "",
        string actorId = "actor-1",
        int llmRound = 1)
    {
        streamTopic = string.IsNullOrWhiteSpace(streamTopic)
            ? $"aevatar://actors/{actorId}/runs/{runId}"
            : streamTopic;
        resultJson = string.IsNullOrWhiteSpace(resultJson)
            ? $$"""{"run_id":"{{runId}}","actor_id":"{{actorId}}","stream_topic":"{{streamTopic}}","wait":"{{WaitModeValue(waitMode)}}"}"""
            : resultJson;
        return new SubmitChatRunToolCallRequested
        {
            ToolCallId = toolCallId,
            ToolName = "aevatar_invoke_gagent",
            Arguments = BuildArguments(waitMode),
            InternalResultJson = resultJson,
            RunId = runId,
            TargetKind = ChatRunSubRunTargetKind.Gagent,
            TargetId = actorId,
            WaitMode = waitMode,
            StreamTopic = streamTopic,
            ActorId = actorId,
            LlmRound = llmRound,
        };
    }

    private static PrepareChatRunSubRunObservationRequested BuildPrepareObservation(
        string toolCallId,
        string runId,
        string actorId)
    {
        var streamTopic = $"aevatar://actors/{actorId}/runs/{runId}";
        return new PrepareChatRunSubRunObservationRequested
        {
            ToolCallId = toolCallId,
            ToolName = "aevatar_invoke_gagent",
            Arguments = BuildArguments(ChatRunSubRunWaitMode.Complete),
            RunId = runId,
            TargetKind = ChatRunSubRunTargetKind.Gagent,
            TargetId = actorId,
            WaitMode = ChatRunSubRunWaitMode.Complete,
            StreamTopic = streamTopic,
            ActorId = actorId,
            LlmRound = 1,
        };
    }

    private static Struct BuildArguments(ChatRunSubRunWaitMode waitMode)
    {
        var arguments = new Struct();
        arguments.Fields["actor_id"] = Google.Protobuf.WellKnownTypes.Value.ForString("actor-1");
        arguments.Fields["wait"] = Google.Protobuf.WellKnownTypes.Value.ForString(WaitModeValue(waitMode));
        return arguments;
    }

    private static string WaitModeValue(ChatRunSubRunWaitMode waitMode) =>
        waitMode switch
        {
            ChatRunSubRunWaitMode.Ack => "ack",
            ChatRunSubRunWaitMode.Complete => "complete",
            _ => "stream",
        };

    private static EventEnvelope BuildForwardedCommittedCompletionEnvelope(
        string sourceActorId,
        string targetActorId,
        string runId,
        string content)
    {
        var original = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = sourceActorId,
                    EventId = Guid.NewGuid().ToString("N"),
                    EventType = RoleChatSessionCompletedEvent.Descriptor.FullName,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = 7,
                    EventData = Any.Pack(new RoleChatSessionCompletedEvent
                    {
                        SessionId = runId,
                        Content = content,
                        ContentEmitted = true,
                        RoleId = sourceActorId,
                    }),
                },
            }),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(sourceActorId),
        };

        return StreamForwardingRules.BuildForwardedEnvelope(
            original,
            sourceActorId,
            targetActorId,
            StreamForwardingMode.HandleThenForward);
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        public List<(string StreamId, StreamForwardingBinding Binding)> Upserts { get; } = [];

        public List<(string StreamId, string TargetStreamId)> Removes { get; } = [];

        public bool ThrowOnRemove { get; init; }

        public IStream GetStream(string actorId) => new RecordingStream(actorId, this);

        private sealed class RecordingStream(string streamId, RecordingStreamProvider owner) : IStream
        {
            public string StreamId { get; } = streamId;

            public Task ProduceAsync<T>(T message, CancellationToken ct = default)
                where T : IMessage =>
                Task.CompletedTask;

            public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
                where T : IMessage, new() =>
                Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());

            public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
            {
                owner.Upserts.Add((StreamId, CloneBinding(binding)));
                return Task.CompletedTask;
            }

            public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
            {
                owner.Removes.Add((StreamId, targetStreamId));
                if (owner.ThrowOnRemove)
                    throw new InvalidOperationException("relay remove failed");

                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
        }

        private static StreamForwardingBinding CloneBinding(StreamForwardingBinding binding) =>
            new()
            {
                SourceStreamId = binding.SourceStreamId,
                TargetStreamId = binding.TargetStreamId,
                ForwardingMode = binding.ForwardingMode,
                DirectionFilter = new HashSet<TopologyAudience>(binding.DirectionFilter),
                EventTypeFilter = new HashSet<string>(binding.EventTypeFilter, StringComparer.Ordinal),
                Version = binding.Version,
                LeaseId = binding.LeaseId,
            };
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
