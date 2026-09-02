using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class LlmSessionRunObservationServiceTests
{
    [Fact]
    public async Task ObserveAsync_ShouldAttachBeforeDispatch()
    {
        var ports = new RecordingObservationPorts([CompletedEnvelope("resp-1", "done")]);
        var service = ports.CreateService();

        var result = await service.ObserveAsync(Request(ports), null);

        result.Error.Should().BeNull();
        result.Completion!.OutputText.Should().Be("done");
        ports.Events.Should().ContainInOrder("prepare", "attach", "dispatch", "detach", "release-projection", "release-preparation");
    }

    [Fact]
    public async Task ObserveAsync_ShouldEmitTextDelta()
    {
        var ports = new RecordingObservationPorts([
            ChunkEnvelope("resp-1", "Hel"),
            ChunkEnvelope("resp-1", "lo"),
            CompletedEnvelope("resp-1", "Hello"),
        ]);
        var service = ports.CreateService();
        var deltas = new List<LlmSessionRunObservedDelta>();

        var result = await service.ObserveAsync(Request(ports), (delta, _) =>
        {
            deltas.Add(delta);
            return ValueTask.CompletedTask;
        });

        result.Error.Should().BeNull();
        result.Completion!.OutputText.Should().Be("Hello");
        deltas.Select(static delta => delta.TextDelta).Where(static text => text != null)
            .Should().Equal("Hel", "lo");
    }

    [Fact]
    public async Task ObserveAsync_ShouldIgnoreRunStartedEventForOutput()
    {
        var ports = new RecordingObservationPorts([
            StartedEnvelope("resp-1"),
            ChunkEnvelope("resp-1", "done"),
            CompletedEnvelope("resp-1", "done"),
        ]);
        var service = ports.CreateService();
        var deltas = new List<LlmSessionRunObservedDelta>();

        var result = await service.ObserveAsync(Request(ports), (delta, _) =>
        {
            deltas.Add(delta);
            return ValueTask.CompletedTask;
        });

        result.Error.Should().BeNull();
        result.Completion!.OutputText.Should().Be("done");
        deltas.Should().ContainSingle();
        deltas[0].TextDelta.Should().Be("done");
    }

    [Fact]
    public async Task ObserveAsync_ShouldSuppressLiveToolDeltas_AndKeepTerminalToolSnapshot()
    {
        var ports = new RecordingObservationPorts([
            ChunkEnvelope("resp-1", toolCallDelta: RuntimeTool("call-1", "get_weather", """{"city":"SG"}""")),
            ToolEnvelope("resp-1", RuntimeTool("call-1", "get_weather", """{"city":"SG"}""")),
            CompletedEnvelope("resp-1", "done", [RuntimeTool("call-1", "get_weather", """{"city":"SG"}""")]),
        ]);
        var service = ports.CreateService();
        var deltas = new List<LlmSessionRunObservedDelta>();

        var result = await service.ObserveAsync(Request(ports), (delta, _) =>
        {
            deltas.Add(delta);
            return ValueTask.CompletedTask;
        });

        result.Error.Should().BeNull();
        result.Completion!.ToolCalls.Should().ContainSingle().Which.CallId.Should().Be("call-1");
        deltas.Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveAsync_ShouldBuildCompletedSnapshot()
    {
        var ports = new RecordingObservationPorts([
            CompletedEnvelope(
                "resp-1",
                "done",
                [RuntimeTool("call-1", "client_tool", """{"ok":true}""")],
                new LlmSessionTokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 }),
        ]);
        var service = ports.CreateService();

        var result = await service.ObserveAsync(Request(ports), null);

        result.Error.Should().BeNull();
        result.Completion.Should().NotBeNull();
        result.Completion!.OutputText.Should().Be("done");
        result.Completion.ToolCalls.Should().ContainSingle().Which.ResultJson.Should().Be("""{"ok":true}""");
        result.Completion.Usage.Should().Be(new TokenUsage(3, 4, 7));
        result.Admission!.CorrelationId.Should().Be("resp-1");
    }

    [Fact]
    public async Task ObserveAsync_ShouldReturnFailedError()
    {
        var ports = new RecordingObservationPorts([FailedEnvelope("resp-1", "provider_error", "provider crashed")]);
        var service = ports.CreateService();

        var result = await service.ObserveAsync(Request(ports), null);

        result.Error.Should().BeEquivalentTo(new LlmSessionRunObservedError(
            LlmSessionRunObservedTerminalKind.Failed,
            500,
            "provider_error",
            "provider crashed"));
        result.Completion.Should().BeNull();
    }

    [Fact]
    public async Task ObserveAsync_ShouldReturnCancelledError()
    {
        var ports = new RecordingObservationPorts([CancelledEnvelope("resp-1")]);
        var service = ports.CreateService();

        var result = await service.ObserveAsync(Request(ports), null);

        result.Error.Should().BeEquivalentTo(new LlmSessionRunObservedError(
            LlmSessionRunObservedTerminalKind.Cancelled,
            409,
            "run_cancelled",
            "LLM run was cancelled."));
        result.Completion.Should().BeNull();
    }

    [Fact]
    public async Task ObserveAsync_ShouldReturnTimeoutError()
    {
        var ports = new RecordingObservationPorts([]);
        var service = ports.CreateService();

        var result = await service.ObserveAsync(
            Request(ports, timeout: TimeSpan.FromMilliseconds(20), completeAfterPublish: false),
            null);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be(LlmSessionRunObservedTerminalKind.TimedOut);
        result.Error.StatusCode.Should().Be(504);
        result.Error.Code.Should().Be("response_timeout");
        ports.Events.Should().Contain(["detach", "release-projection", "release-preparation"]);
    }

    [Fact]
    public async Task ObserveAsync_ShouldReturnObservationUnavailable_WhenPreparationIsUnavailable()
    {
        var ports = new RecordingObservationPorts([], preparationAvailable: false);
        var service = ports.CreateService();
        var dispatched = false;

        var result = await service.ObserveAsync(
            Request(ports, dispatch: _ =>
            {
                dispatched = true;
                return Task.FromResult(new DispatchAdmission(true, "cmd-1", DateTimeOffset.UtcNow, "actor-1", "resp-1"));
            }),
            null);

        result.Error.Should().BeEquivalentTo(new LlmSessionRunObservedError(
            LlmSessionRunObservedTerminalKind.ObservationUnavailable,
            503,
            "observation_unavailable",
            "LLM run observation is unavailable."));
        result.Completion.Should().BeNull();
        result.Admission.Should().BeNull();
        dispatched.Should().BeFalse();
        ports.Events.Should().Equal("prepare");
    }

    [Fact]
    public async Task ObserveAsync_ShouldReturnObservationUnavailable_WhenAttachmentIsUnavailable()
    {
        var ports = new RecordingObservationPorts([], attachmentAvailable: false);
        var service = ports.CreateService();
        var dispatched = false;

        var result = await service.ObserveAsync(
            Request(ports, dispatch: _ =>
            {
                dispatched = true;
                return Task.FromResult(new DispatchAdmission(true, "cmd-1", DateTimeOffset.UtcNow, "actor-1", "resp-1"));
            }),
            null);

        result.Error.Should().BeEquivalentTo(new LlmSessionRunObservedError(
            LlmSessionRunObservedTerminalKind.ObservationUnavailable,
            503,
            "observation_unavailable",
            "LLM run observation attachment is unavailable."));
        result.Completion.Should().BeNull();
        result.Admission.Should().BeNull();
        dispatched.Should().BeFalse();
        ports.Events.Should().Equal("prepare", "attach", "release-preparation");
    }

    [Fact]
    public async Task ObserveAsync_ShouldReturnObservationUnavailable_WhenSinkCompletesWithoutTerminal()
    {
        var ports = new RecordingObservationPorts([ChunkEnvelope("resp-1", "partial")]);
        var service = ports.CreateService();
        var deltas = new List<LlmSessionRunObservedDelta>();

        var result = await service.ObserveAsync(Request(ports), (delta, _) =>
        {
            deltas.Add(delta);
            return ValueTask.CompletedTask;
        });

        result.Error.Should().BeEquivalentTo(new LlmSessionRunObservedError(
            LlmSessionRunObservedTerminalKind.ObservationUnavailable,
            503,
            "observation_unavailable",
            "LLM run observation ended without a terminal event."));
        result.Completion.Should().BeNull();
        result.Admission!.CorrelationId.Should().Be("resp-1");
        deltas.Select(static delta => delta.TextDelta).Should().Equal("partial");
        ports.Events.Should().ContainInOrder("prepare", "attach", "dispatch", "detach", "release-projection", "release-preparation");
    }

    [Fact]
    public async Task ObserveAsync_ShouldUnwrapCommittedEnvelope_AndEmitDeltaAndCompletion()
    {
        var ports = new RecordingObservationPorts([
            CommittedEnvelope(ChunkEnvelope("resp-1", "Hel")),
            CommittedEnvelope(ChunkEnvelope("resp-1", "lo")),
            CommittedEnvelope(CompletedEnvelope("resp-1", "Hello")),
        ]);
        var service = ports.CreateService();
        var deltas = new List<LlmSessionRunObservedDelta>();

        var result = await service.ObserveAsync(Request(ports), (delta, _) =>
        {
            deltas.Add(delta);
            return ValueTask.CompletedTask;
        });

        result.Error.Should().BeNull();
        result.Completion!.OutputText.Should().Be("Hello");
        result.Admission!.CorrelationId.Should().Be("resp-1");
        deltas.Select(static delta => delta.TextDelta).Where(static text => text != null)
            .Should().Equal("Hel", "lo");
    }

    [Fact]
    public async Task ObserveAsync_ShouldDetachAndRelease_WhenDispatchThrows()
    {
        var ports = new RecordingObservationPorts([]);
        var service = ports.CreateService();

        var act = () => service.ObserveAsync(
            Request(
                ports,
                dispatch: _ => Task.FromException<DispatchAdmission>(new NyxIdAuthenticationRequiredException("provider"))),
            null);

        await act.Should().ThrowAsync<NyxIdAuthenticationRequiredException>();
        ports.Events.Should().Contain(["detach", "release-projection", "release-preparation"]);
    }

    private static LlmSessionRunObservationRequest Request(
        RecordingObservationPorts ports,
        TimeSpan? timeout = null,
        bool completeAfterPublish = true,
        Func<CancellationToken, Task<DispatchAdmission>>? dispatch = null) =>
        new(
            "actor-1",
            "resp-1",
            "resp-1:llm-run",
            dispatch ?? (ct =>
            {
                ports.Events.Add("dispatch");
                ports.PublishAll(completeAfterPublish);
                return Task.FromResult(new DispatchAdmission(true, "cmd-1", DateTimeOffset.UtcNow, "actor-1", "resp-1"));
            }),
            timeout ?? TimeSpan.FromSeconds(5));

    private static EventEnvelope ChunkEnvelope(
        string responseId,
        string? text = null,
        LlmSessionRuntimeToolCall? toolCallDelta = null) =>
        Envelope(responseId, new LlmStreamChunkObserved
        {
            ResponseId = responseId,
            RunId = $"{responseId}:llm-run",
            DeltaText = text ?? string.Empty,
            ToolCallDelta = toolCallDelta,
        });

    private static EventEnvelope StartedEnvelope(string responseId) =>
        Envelope(responseId, new LlmRunStartedEvent
        {
            ResponseId = responseId,
            RunId = $"{responseId}:llm-run",
            Sequence = 1,
            StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

    private static EventEnvelope ToolEnvelope(string responseId, LlmSessionRuntimeToolCall toolCall) =>
        Envelope(responseId, new LlmToolCallObserved
        {
            ResponseId = responseId,
            RunId = $"{responseId}:llm-run",
            ToolCall = toolCall,
            Forwarded = true,
        });

    private static EventEnvelope CompletedEnvelope(
        string responseId,
        string outputText,
        IReadOnlyList<LlmSessionRuntimeToolCall>? toolCalls = null,
        LlmSessionTokenUsage? usage = null)
    {
        var completed = new LlmRunCompleted
        {
            ResponseId = responseId,
            RunId = $"{responseId}:llm-run",
            OutputText = outputText,
            Usage = usage,
            CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        completed.ForwardedToolCalls.AddRange(toolCalls ?? []);
        return Envelope(responseId, completed);
    }

    private static EventEnvelope FailedEnvelope(string responseId, string code, string message) =>
        Envelope(responseId, new LlmRunFailed
        {
            ResponseId = responseId,
            RunId = $"{responseId}:llm-run",
            FailureCode = code,
            FailureMessage = message,
            FailedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

    private static EventEnvelope CancelledEnvelope(string responseId) =>
        Envelope(responseId, new LlmRunCancelled
        {
            ResponseId = responseId,
            RunId = $"{responseId}:llm-run",
            CancelledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

    private static EventEnvelope Envelope(string responseId, Google.Protobuf.IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(payload),
            Propagation = new EnvelopePropagation { CorrelationId = responseId },
        };

    private static EventEnvelope CommittedEnvelope(EventEnvelope envelope) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventData = envelope.Payload,
                },
            }),
            Propagation = envelope.Propagation,
        };

    private static LlmSessionRuntimeToolCall RuntimeTool(string callId, string name, string argumentsJson) =>
        new()
        {
            CallId = callId,
            ToolName = name,
            ArgumentsJson = argumentsJson,
            Arguments = ResponsesProtoPayloads.ParseStruct(argumentsJson),
        };

    private sealed class RecordingObservationPorts
    {
        private readonly IReadOnlyList<EventEnvelope> _events;
        private readonly bool _preparationAvailable;
        private readonly bool _attachmentAvailable;

        public RecordingObservationPorts(
            IReadOnlyList<EventEnvelope> events,
            bool preparationAvailable = true,
            bool attachmentAvailable = true)
        {
            _events = events;
            _preparationAvailable = preparationAvailable;
            _attachmentAvailable = attachmentAvailable;
            ScopePreparationPort = new RecordingScopeLeasePreparationPort(this);
            ProjectionPort = new RecordingProjectionPort(this);
        }

        public List<string> Events { get; } = [];

        public RecordingScopeLeasePreparationPort ScopePreparationPort { get; }

        public RecordingProjectionPort ProjectionPort { get; }

        public LlmSessionRunObservationService CreateService() => new(ScopePreparationPort, ProjectionPort);

        public void PublishAll(bool completeAfterPublish)
        {
            foreach (var envelope in _events)
                ProjectionPort.Sink?.Push(envelope);
            if (completeAfterPublish)
                ProjectionPort.Sink?.Complete();
        }

        public sealed class RecordingScopeLeasePreparationPort(RecordingObservationPorts owner)
            : ILlmSessionObservationScopeLeasePreparationPort
        {
            public Task<LlmSessionObservationScopeLeasePreparation?> PrepareAsync(
                string actorId,
                string responseId,
                CancellationToken ct = default)
            {
                owner.Events.Add("prepare");
                if (!owner._preparationAvailable)
                    return Task.FromResult<LlmSessionObservationScopeLeasePreparation?>(null);

                return Task.FromResult<LlmSessionObservationScopeLeasePreparation?>(
                    new LlmSessionObservationScopeLeasePreparation(actorId, responseId));
            }

            public Task ReleaseAsync(
                LlmSessionObservationScopeLeasePreparation preparation,
                CancellationToken ct = default)
            {
                owner.Events.Add("release-preparation");
                return Task.CompletedTask;
            }
        }

        public sealed class RecordingProjectionPort(RecordingObservationPorts owner) : ILlmSessionObservationProjectionPort
        {
            public IEventSink<EventEnvelope>? Sink { get; private set; }

            public bool ProjectionEnabled => true;

            public Task<EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>?> AttachExistingResponseProjectionAsync(
                string actorId,
                string responseId,
                IEventSink<EventEnvelope> sink,
                CancellationToken ct = default)
            {
                owner.Events.Add("attach");
                if (!owner._attachmentAvailable)
                    return Task.FromResult<EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>?>(null);

                Sink = sink;
                return Task.FromResult<EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>?>(
                    new EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>(
                        new ObservationLease(actorId, responseId),
                        new NoOpAsyncDisposable()));
            }

            public Task<IAsyncDisposable?> AttachLiveSinkAsync(
                ILlmSessionObservationProjectionLease lease,
                IEventSink<EventEnvelope> sink,
                CancellationToken ct = default) =>
                Task.FromResult<IAsyncDisposable?>(new NoOpAsyncDisposable());

            public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default)
            {
                owner.Events.Add("detach");
                Sink = null;
                return Task.CompletedTask;
            }

            public Task ReleaseActorProjectionAsync(
                ILlmSessionObservationProjectionLease lease,
                CancellationToken ct = default)
            {
                owner.Events.Add("release-projection");
                return Task.CompletedTask;
            }
        }

        private sealed record ObservationLease(string ActorId, string ResponseId)
            : ILlmSessionObservationProjectionLease;

        private sealed class NoOpAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
