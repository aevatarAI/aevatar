using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class LlmRunExecutorTests
{
    [Fact]
    public async Task StartAsync_ShouldDispatchRunStartedCommand_AndReturnBeforeStreamingLoopDispatchesRecordCommands()
    {
        var provider = new GateControlledLlmProviderFactory();
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var observation = new RecordingLlmRunObservation();
        var dispatch = new RecordingDispatchPort(observation);
        var executor = CreateExecutor(core, dispatch);

        var request = new LlmRunExecutorRequest(
            "session-actor-1",
            "resp_executor",
            "run_1",
            BuildRunRequest("resp_executor"),
            "ApiKey");

        var admission = await executor.StartAsync(request);
        dispatch.Calls.Should().ContainSingle();
        admission.Accepted.Should().BeTrue();
        admission.ActorId.Should().Be("session-actor-1");
        admission.CorrelationId.Should().Be("resp_executor");
        admission.CommandId.Should().Be("start-resp_executor");
        var started = dispatch.Calls[0].Envelope.Payload!.Unpack<RecordLlmRunStarted>();
        started.Command.Should().BeEquivalentTo(request.Command);
        started.StartedAt.Should().NotBeNull();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(ToExecutionRequest(request), cts.Token);
        provider.Release.SetResult();
        await dispatch.WaitForCallsAsync(3, cts.Token);
        await executeTask;

        dispatch.Calls.Should().HaveCount(3);
        dispatch.Calls.Select(call => call.ActorId).Should().OnlyContain(actorId => actorId == "session-actor-1");
        var chunk = dispatch.Calls[1].Envelope.Payload!.Unpack<RecordLlmStreamChunkObserved>();
        chunk.ResponseId.Should().Be("resp_executor");
        chunk.RunId.Should().Be("run_1");
        chunk.RecordId.Should().Be("run_1:chunk:1");
        chunk.DeltaText.Should().Be("done");
        dispatch.Calls[1].Envelope.Propagation!.CorrelationId.Should().Be(chunk.ResponseId);

        var completed = dispatch.Calls[2].Envelope.Payload!.Unpack<RecordLlmRunCompleted>();
        completed.RecordId.Should().Be("run_1:completed:2");
        completed.OutputText.Should().Be("done");
        AssertDirectEnvelope(dispatch.Calls[2], completed.RecordId, completed.ResponseId, RecordLlmRunCompleted.Descriptor.FullName);
    }

    [Fact]
    public async Task RunExecutionScheduler_ShouldEnqueueRequest_WithoutBlockingTheActorTurn()
    {
        var queue = new RecordingExecutionQueue();
        var scheduler = new LlmRunExecutionScheduler(queue);
        var request = new LlmRunExecutionRequest(
            " session-actor-scheduler ",
            " resp_scheduler ",
            " run_1 ",
            BuildRunRequest("resp_scheduler"),
            "ApiKey");

        // The scheduler runs inside the session actor's turn; it must complete synchronously
        // (enqueue only) and never await execution.
        var schedule = scheduler.ScheduleAsync(request, CancellationToken.None);
        schedule.IsCompleted.Should().BeTrue();
        await schedule;

        var enqueued = queue.Enqueued.Should().ContainSingle().Subject;
        enqueued.SessionActorId.Should().Be("session-actor-scheduler");
        enqueued.ResponseId.Should().Be("resp_scheduler");
        enqueued.RunId.Should().Be("run_1");
        enqueued.Command.ResponseId.Should().Be("resp_scheduler");
        enqueued.Command.BearerToken.Should().Be("token-1");
        enqueued.OriginPlatform.Should().Be("ApiKey");
        enqueued.Command.Should().NotBeSameAs(
            request.Command,
            "the request crosses from the actor turn to a worker thread and must be cloned");
    }

    [Fact]
    public async Task LlmRunExecutionQueue_ShouldRoundTripRequestsInFifoOrder()
    {
        var queue = new LlmRunExecutionQueue(
            Options.Create(new LlmRunExecutionWorkerOptions { QueueCapacity = 8 }));
        queue.Enqueue(BuildExecutionRequest("resp_a", "run_a"));
        queue.Enqueue(BuildExecutionRequest("resp_b", "run_b"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var drained = await DrainAsync(queue, 2, cts.Token);

        drained.Select(static request => request.RunId).Should().ContainInOrder("run_a", "run_b");
    }

    [Fact]
    public void LlmRunExecutionQueue_WhenFull_ShouldThrowQueueFull_WithoutBlocking()
    {
        var queue = new LlmRunExecutionQueue(
            Options.Create(new LlmRunExecutionWorkerOptions { QueueCapacity = 1 }));
        queue.Enqueue(BuildExecutionRequest("resp_1", "run_1"));

        var overflow = () => queue.Enqueue(BuildExecutionRequest("resp_2", "run_2"));

        overflow.Should().Throw<LlmRunExecutionQueueFullException>()
            .WithMessage("*resp_2*run_2*");
    }

    [Fact]
    public async Task LlmRunExecutionQueue_Drain_ShouldHandRequestToExecutorOffTheEnqueueStack()
    {
        var queue = new LlmRunExecutionQueue(
            Options.Create(new LlmRunExecutionWorkerOptions { QueueCapacity = 8 }));
        var executor = new RecordingLlmRunExecutor();
        queue.Enqueue(BuildExecutionRequest("resp_drain", "run_drain"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var request in queue.DequeueAllAsync(cts.Token))
        {
            await executor.ExecuteAsync(request, CancellationToken.None);
            break;
        }

        executor.ExecuteRequests.Should().ContainSingle()
            .Which.RunId.Should().Be("run_drain");
    }

    [Fact]
    public async Task StartAsync_ShouldDispatchForwardedToolCallRecordCommandsInOrder()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":""",
                    },
                },
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        ArgumentsJson = "\"Singapore\"}",
                    },
                    Usage = new TokenUsage(5, 7, 12),
                    IsLast = true,
                },
            ],
        ]);
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var observation = new RecordingLlmRunObservation();
        var dispatch = new RecordingDispatchPort(observation);
        var executor = CreateExecutor(core, dispatch);

        var request = new LlmRunExecutorRequest(
            "session-actor-2",
            "resp_forwarded",
            " run_forwarded ",
            BuildRunRequest("resp_forwarded", BuildForwardedSelection()),
            "ApiKey");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(ToExecutionRequest(request), cts.Token);
        await dispatch.WaitForCallsAsync(3, cts.Token);
        await executeTask;

        dispatch.Calls.Select(call => call.ActorId).Should().OnlyContain(actorId => actorId == "session-actor-2");
        var firstChunk = dispatch.Calls[0].Envelope.Payload!.Unpack<RecordLlmStreamChunkObserved>();
        firstChunk.RecordId.Should().Be("run_forwarded:chunk:1");
        firstChunk.ToolCallDelta!.CallId.Should().Be("call_1");

        var secondChunk = dispatch.Calls[1].Envelope.Payload!.Unpack<RecordLlmStreamChunkObserved>();
        secondChunk.RecordId.Should().Be("run_forwarded:chunk:2");
        secondChunk.Usage!.TotalTokens.Should().Be(12);

        var completed = dispatch.Calls[2].Envelope.Payload!.Unpack<RecordLlmRunCompleted>();
        completed.RecordId.Should().Be("run_forwarded:completed:3");
        completed.ForwardedToolCalls.Should().ContainSingle()
            .Which.CallId.Should().Be("call_1");
        var forwarded = completed.ForwardedToolCallRecords.Should().ContainSingle().Subject;
        forwarded.SchemaHash.Should().Be("schema-1");
        ResponsesJsonValues.ToBoundaryJson(forwarded.Arguments).Should().Be("""{"city":"Singapore"}""");
        AssertDirectEnvelope(dispatch.Calls[2], completed.RecordId, completed.ResponseId, RecordLlmRunCompleted.Descriptor.FullName);
    }

    [Fact]
    public async Task StartAsync_ShouldDispatchLocalToolResultRecordCommand()
    {
        var tool = new RecordingAgentTool("get_weather", """{"temperature":28}""");
        var toolProvider = new StaticResponsesToolProvider(substituteTools: [tool]);
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call_local",
                        Name = "get_weather",
                        ArgumentsJson = """{"city":"Singapore"}""",
                    },
                    IsLast = true,
                },
            ],
            [
                new LLMStreamChunk
                {
                    DeltaContent = "local done",
                    IsLast = true,
                },
            ],
        ]);
        var toolExecutionPort = new RecordingAgentToolExecutionPort();
        var core = new LlmRunCore(
            provider,
            [toolProvider],
            TestToolSetRegistry.Empty,
            NullLogger<LlmRunCore>.Instance,
            toolExecutionPort);
        var observation = new RecordingLlmRunObservation();
        var dispatch = new RecordingDispatchPort(observation);
        var executor = CreateExecutor(core, dispatch);
        var selection = BuildForwardedSelection();
        selection.SubstitutedToolNames.Add("get_weather");

        var request = new LlmRunExecutorRequest(
            "session-actor-3",
            "resp_local",
            "run_local",
            BuildRunRequest("resp_local", selection),
            "ApiKey");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(ToExecutionRequest(request), cts.Token);
        await dispatch.WaitForCallsAsync(4, cts.Token);
        await executeTask;

        toolExecutionPort.Requests.Should().ContainSingle()
            .Which.ExecutionContext.Request.CallId.Should().Be("call_local");
        tool.Executions.Should().ContainSingle().Which.Should().Be("""{"city":"Singapore"}""");
        var localResult = dispatch.Calls[1].Envelope.Payload!.Unpack<RecordLlmToolCallObserved>();
        localResult.RecordId.Should().Be("run_local:tool:2");
        localResult.Forwarded.Should().BeFalse();
        localResult.LocalResultJson.Should().Be("""{"temperature":28}""");
        localResult.LocalResult!.StructValue.Fields["temperature"].NumberValue.Should().Be(28);

        var completed = dispatch.Calls[3].Envelope.Payload!.Unpack<RecordLlmRunCompleted>();
        completed.RecordId.Should().Be("run_local:completed:4");
        completed.OutputText.Should().Be("local done");
    }

    [Fact]
    public async Task StartAsync_WhenProviderFails_ShouldDispatchRunFailedRecordCommand()
    {
        var provider = new ThrowingLlmProviderFactory(new NyxIdAuthenticationRequiredException("nyxid"));
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var observation = new RecordingLlmRunObservation();
        var dispatch = new RecordingDispatchPort(observation);
        var executor = CreateExecutor(core, dispatch);

        var request = new LlmRunExecutorRequest(
            "session-actor-4",
            "resp_failed",
            "run_failed",
            BuildRunRequest("resp_failed"),
            "ApiKey");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(ToExecutionRequest(request), cts.Token);
        await dispatch.WaitForCallsAsync(1, cts.Token);
        await executeTask;

        var failed = dispatch.Calls[0].Envelope.Payload!.Unpack<RecordLlmRunFailed>();
        failed.RecordId.Should().Be("run_failed:failed:1");
        failed.RunId.Should().Be("run_failed");
        failed.FailureCode.Should().Be("authentication_required");
        failed.FailureMessage.Should().Contain("NyxID authentication required");
        AssertDirectEnvelope(dispatch.Calls[0], failed.RecordId, failed.ResponseId, RecordLlmRunFailed.Descriptor.FullName);
    }

    [Fact]
    public async Task StartAsync_WhenProviderCancels_ShouldDispatchRunCancelledRecordCommand()
    {
        var provider = new ThrowingLlmProviderFactory(new OperationCanceledException());
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var observation = new RecordingLlmRunObservation();
        var dispatch = new RecordingDispatchPort(observation);
        var executor = CreateExecutor(core, dispatch);

        var request = new LlmRunExecutorRequest(
            "session-actor-5",
            "resp_cancelled",
            "run_cancelled",
            BuildRunRequest("resp_cancelled"),
            "ApiKey");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(ToExecutionRequest(request), cts.Token);
        await dispatch.WaitForCallsAsync(1, cts.Token);
        await executeTask;

        var cancelled = dispatch.Calls[0].Envelope.Payload!.Unpack<RecordLlmRunCancelled>();
        cancelled.RecordId.Should().Be("run_cancelled:cancelled:1");
        cancelled.RunId.Should().Be("run_cancelled");
        cancelled.CancelledAt.Should().NotBeNull();
        AssertDirectEnvelope(dispatch.Calls[0], cancelled.RecordId, cancelled.ResponseId, RecordLlmRunCancelled.Descriptor.FullName);
    }

    [Fact]
    public async Task StartAsync_WhenSinkDispatchFails_ShouldDispatchExecutorFailureRecordCommand()
    {
        var provider = new ScriptedLlmProviderFactory([
            [
                new LLMStreamChunk
                {
                    DeltaContent = "lost",
                    IsLast = true,
                },
            ],
        ]);
        var core = new LlmRunCore(provider, [], TestToolSetRegistry.Empty, NullLogger<LlmRunCore>.Instance, new RecordingAgentToolExecutionPort());
        var observation = new RecordingLlmRunObservation();
        var dispatch = new FailingThenRecordingDispatchPort(observation, failuresBeforeSuccess: 2);
        var executor = CreateExecutor(core, dispatch);

        var request = new LlmRunExecutorRequest(
            "session-actor-6",
            "resp_executor_failed",
            "run_executor_failed",
            BuildRunRequest("resp_executor_failed"),
            "ApiKey");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(ToExecutionRequest(request), cts.Token);
        await dispatch.WaitForAttemptsAsync(3, cts.Token);
        await executeTask;

        dispatch.Calls.Should().ContainSingle();
        var failed = dispatch.Calls[0].Envelope.Payload!.Unpack<RecordLlmRunFailed>();
        failed.RecordId.Should().Be("run_executor_failed:executor-failed");
        failed.FailureCode.Should().Be("executor_failed");
        failed.FailureMessage.Should().Contain("Synthetic dispatch failure");
    }

    // The dispatch-only executor takes no observation ports: the run loop dispatches Record*
    // commands and the facade's single long-lived observation serves the client. Constructing
    // it here without any observation port is itself the proof that the run no longer does a
    // per-record observation round-trip.
    private static LlmRunExecutor CreateExecutor(
        ILlmRunCore core,
        IActorDispatchPort dispatch) =>
        new(core, dispatch, NullLogger<LlmRunExecutor>.Instance);

    private static LlmRunRequested BuildRunRequest(
        string responseId,
        LlmSessionRuntimeToolSelection? selection = null) =>
        new()
        {
            ResponseId = responseId,
            RunId = "run_1",
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            BearerToken = "token-1",
            Model = "test-model",
            ToolSelection = selection,
            Messages =
            {
                new LlmSessionRuntimeChatMessage
                {
                    Role = "user",
                    Content = "Run",
                },
            },
        };

    private static LlmRunExecutionRequest ToExecutionRequest(LlmRunExecutorRequest request) =>
        new(
            request.SessionActorId,
            request.ResponseId,
            request.RunId,
            request.Command.Clone(),
            request.OriginPlatform);

    private static LlmRunExecutionRequest BuildExecutionRequest(string responseId, string runId) =>
        new(
            $"session-actor-{responseId}",
            responseId,
            runId,
            BuildRunRequest(responseId),
            "ApiKey");

    private static async Task<List<LlmRunExecutionRequest>> DrainAsync(
        ILlmRunExecutionQueue queue,
        int count,
        CancellationToken ct)
    {
        var drained = new List<LlmRunExecutionRequest>();
        await foreach (var request in queue.DequeueAllAsync(ct).ConfigureAwait(false))
        {
            drained.Add(request);
            if (drained.Count >= count)
                break;
        }

        return drained;
    }

    private static LlmSessionRuntimeToolSelection BuildForwardedSelection() =>
        new()
        {
            ForwardedTools =
            {
                new LlmSessionRuntimeToolDeclaration
                {
                    ToolName = "get_weather",
                    Description = "Get weather",
                    ParametersJson = """{"type":"object"}""",
                    Parameters = new Struct
                    {
                        Fields =
                        {
                            ["type"] = Google.Protobuf.WellKnownTypes.Value.ForString("object"),
                        },
                    },
                    SchemaHash = "schema-1",
                },
            },
        };

    private static void AssertDirectEnvelope(
        (string ActorId, EventEnvelope Envelope) call,
        string recordId,
        string responseId,
        string payloadFullName)
    {
        call.Envelope.Id.Should().Be(recordId);
        call.Envelope.Payload!.TypeUrl.Should().EndWith(payloadFullName);
        call.Envelope.Route!.PublisherActorId.Should().Be("gagent-service.llm-run-executor");
        call.Envelope.Route.GetTargetActorId().Should().Be(call.ActorId);
        // CorrelationId is the response/session id (not the record id): the observation
        // projector only fans a committed event out to the client hub when CorrelationId
        // equals the session id.
        call.Envelope.Propagation!.CorrelationId.Should().Be(responseId);
    }

    private sealed class GateControlledLlmProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            await Release.Task.WaitAsync(ct);
            yield return new LLMStreamChunk
            {
                DeltaContent = "done",
                IsLast = true,
            };
        }
    }

    private sealed class ScriptedLlmProviderFactory(
        IReadOnlyList<IReadOnlyList<LLMStreamChunk>> responses) : ILLMProviderFactory, ILLMProvider
    {
        private int _nextResponseIndex;

        public string Name => "test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            var response = responses[Math.Min(_nextResponseIndex, responses.Count - 1)];
            _nextResponseIndex++;
            foreach (var chunk in response)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class ThrowingLlmProviderFactory(Exception exception) : ILLMProviderFactory, ILLMProvider
    {
        public string Name => "test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            throw exception;
            #pragma warning disable CS0162
            yield return new LLMStreamChunk();
            #pragma warning restore CS0162
        }
    }

    private sealed class StaticResponsesToolProvider(
        IReadOnlyList<IAgentTool>? substituteTools = null,
        IReadOnlyList<IAgentTool>? additiveTools = null) : IResponsesToolProvider
    {
        public ValueTask<IReadOnlyList<IAgentTool>> GetSubstituteToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(substituteTools ?? []);

        public ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
            ResponsesToolProviderContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(additiveTools ?? []);
    }

    private sealed class RecordingAgentTool(string name, string resultJson) : IAgentTool
    {
        public List<string> Executions { get; } = [];

        public string Name { get; } = name;

        public string Description => "test tool";

        public string ParametersSchema => """{"type":"object"}""";

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            Executions.Add(argumentsJson);
            return Task.FromResult(resultJson);
        }
    }

    private sealed class RecordingDispatchPort(RecordingLlmRunObservation? observation = null) : IActorDispatchPort
    {
        private readonly RecordingLlmRunObservation _observation = observation ?? new RecordingLlmRunObservation();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = [];

        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            lock (Calls)
            {
                Calls.Add((actorId, envelope.Clone()));
                _observation.PublishCommittedRecord(envelope);
                foreach (var waiter in _waiters.Where(waiter => Calls.Count >= waiter.Count).ToArray())
                {
                    waiter.Signal.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }

            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public Task WaitForCallsAsync(int count, CancellationToken ct)
        {
            lock (Calls)
            {
                if (Calls.Count >= count)
                    return Task.CompletedTask;

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), signal);
                _waiters.Add((count, signal));
                return signal.Task;
            }
        }
    }

    private sealed class FailingThenRecordingDispatchPort(
        RecordingLlmRunObservation observation,
        int failuresBeforeSuccess) : IActorDispatchPort
    {
        private readonly List<(int Count, TaskCompletionSource Signal)> _attemptWaiters = [];
        private int _attempts;

        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            var attempts = Interlocked.Increment(ref _attempts);
            if (attempts <= failuresBeforeSuccess)
            {
                SignalAttemptWaiters(attempts);
                throw new InvalidOperationException($"Synthetic dispatch failure {attempts}.");
            }

            lock (Calls)
            {
                Calls.Add((actorId, envelope.Clone()));
                observation.PublishCommittedRecord(envelope);
            }

            SignalAttemptWaiters(attempts);
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public Task WaitForAttemptsAsync(int count, CancellationToken ct)
        {
            if (Volatile.Read(ref _attempts) >= count)
                return Task.CompletedTask;

            lock (_attemptWaiters)
            {
                if (_attempts >= count)
                    return Task.CompletedTask;

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), signal);
                _attemptWaiters.Add((count, signal));
                return signal.Task;
            }
        }

        private void SignalAttemptWaiters(int attempts)
        {
            lock (_attemptWaiters)
            {
                foreach (var waiter in _attemptWaiters.Where(waiter => attempts >= waiter.Count).ToArray())
                {
                    waiter.Signal.TrySetResult();
                    _attemptWaiters.Remove(waiter);
                }
            }
        }
    }

    private sealed class RecordingLlmRunObservation
    {
        public RecordingScopeLeasePreparationPort ScopePreparationPort { get; }

        public RecordingProjectionPort ProjectionPort { get; }

        public RecordingLlmRunObservation()
        {
            ScopePreparationPort = new RecordingScopeLeasePreparationPort();
            ProjectionPort = new RecordingProjectionPort();
        }

        public void PublishCommittedRecord(EventEnvelope commandEnvelope)
        {
            if (ProjectionPort.Sink == null)
                return;

            if (!TryBuildCommittedRecord(commandEnvelope.Payload, out var committedPayload))
                return;

            ProjectionPort.Sink.Push(new EventEnvelope
            {
                Id = $"{commandEnvelope.Id}:committed",
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventData = committedPayload,
                    },
                }),
                Propagation = commandEnvelope.Propagation?.Clone(),
            });
        }

        private static bool TryBuildCommittedRecord(Any? commandPayload, out Any committedPayload)
        {
            if (commandPayload?.Is(RecordLlmStreamChunkObserved.Descriptor) == true)
            {
                var command = commandPayload.Unpack<RecordLlmStreamChunkObserved>();
                committedPayload = Any.Pack(new LlmStreamChunkObserved
                {
                    ResponseId = command.ResponseId,
                    RunId = command.RunId,
                    Round = command.Round,
                    DeltaText = command.DeltaText,
                    ToolCallDelta = command.ToolCallDelta?.Clone(),
                    Usage = command.Usage?.Clone(),
                    ObservedAt = command.ObservedAt?.Clone(),
                    RecordId = command.RecordId,
                });
                return true;
            }

            if (commandPayload?.Is(RecordLlmToolCallObserved.Descriptor) == true)
            {
                var command = commandPayload.Unpack<RecordLlmToolCallObserved>();
                committedPayload = Any.Pack(new LlmToolCallObserved
                {
                    ResponseId = command.ResponseId,
                    RunId = command.RunId,
                    Round = command.Round,
                    ToolCall = command.ToolCall?.Clone(),
                    Forwarded = command.Forwarded,
                    LocalResultJson = command.LocalResultJson,
                    ObservedAt = command.ObservedAt?.Clone(),
                    LocalResult = command.LocalResult?.Clone(),
                    RecordId = command.RecordId,
                });
                return true;
            }

            if (commandPayload?.Is(RecordLlmRunCompleted.Descriptor) == true)
            {
                var command = commandPayload.Unpack<RecordLlmRunCompleted>();
                var completed = new LlmRunCompleted
                {
                    ResponseId = command.ResponseId,
                    RunId = command.RunId,
                    OutputText = command.OutputText,
                    Usage = command.Usage?.Clone(),
                    CompletedAt = command.CompletedAt?.Clone(),
                    RecordId = command.RecordId,
                };
                completed.ForwardedToolCalls.AddRange(command.ForwardedToolCalls.Select(static call => call.Clone()));
                completed.ForwardedToolCallRecords.AddRange(command.ForwardedToolCallRecords.Select(static call => call.Clone()));
                committedPayload = Any.Pack(completed);
                return true;
            }

            if (commandPayload?.Is(RecordLlmRunFailed.Descriptor) == true)
            {
                var command = commandPayload.Unpack<RecordLlmRunFailed>();
                committedPayload = Any.Pack(new LlmRunFailed
                {
                    ResponseId = command.ResponseId,
                    RunId = command.RunId,
                    FailureCode = command.FailureCode,
                    FailureMessage = command.FailureMessage,
                    FailedAt = command.FailedAt?.Clone(),
                    RecordId = command.RecordId,
                });
                return true;
            }

            if (commandPayload?.Is(RecordLlmRunCancelled.Descriptor) == true)
            {
                var command = commandPayload.Unpack<RecordLlmRunCancelled>();
                committedPayload = Any.Pack(new LlmRunCancelled
                {
                    ResponseId = command.ResponseId,
                    RunId = command.RunId,
                    CancelledAt = command.CancelledAt?.Clone(),
                    RecordId = command.RecordId,
                });
                return true;
            }

            committedPayload = default!;
            return false;
        }

        public sealed class RecordingScopeLeasePreparationPort : ILlmSessionObservationScopeLeasePreparationPort
        {
            public Task<LlmSessionObservationScopeLeasePreparation?> PrepareAsync(
                string actorId,
                string responseId,
                CancellationToken ct = default) =>
                Task.FromResult<LlmSessionObservationScopeLeasePreparation?>(
                    new LlmSessionObservationScopeLeasePreparation(actorId, responseId));

            public Task ReleaseAsync(
                LlmSessionObservationScopeLeasePreparation preparation,
                CancellationToken ct = default) =>
                Task.CompletedTask;
        }

        public sealed class RecordingProjectionPort : ILlmSessionObservationProjectionPort
        {
            public IEventSink<EventEnvelope>? Sink { get; private set; }

            public bool ProjectionEnabled => true;

            public Task<EventSinkProjectionAttachment<ILlmSessionObservationProjectionLease>?> AttachExistingResponseProjectionAsync(
                string actorId,
                string responseId,
                IEventSink<EventEnvelope> sink,
                CancellationToken ct = default)
            {
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
                Sink = null;
                return Task.CompletedTask;
            }

            public Task ReleaseActorProjectionAsync(
                ILlmSessionObservationProjectionLease lease,
                CancellationToken ct = default) =>
                Task.CompletedTask;
        }

        private sealed record ObservationLease(string ActorId, string ResponseId)
            : ILlmSessionObservationProjectionLease;

        private sealed class NoOpAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLlmRunExecutor : ILlmRunExecutionService
    {
        private readonly TaskCompletionSource _executeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<LlmRunExecutionRequest> ExecuteRequests { get; } = [];

        public Task ExecuteAsync(
            LlmRunExecutionRequest request,
            CancellationToken ct = default)
        {
            ExecuteRequests.Add(request);
            _executeStarted.SetResult();
            return Task.CompletedTask;
        }

        public async Task WaitForExecuteAsync(CancellationToken ct)
        {
            using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), _executeStarted);
            await _executeStarted.Task.ConfigureAwait(false);
        }
    }

    private sealed class RecordingExecutionQueue : ILlmRunExecutionQueue
    {
        public List<LlmRunExecutionRequest> Enqueued { get; } = [];

        public void Enqueue(LlmRunExecutionRequest request) => Enqueued.Add(request);

        public IAsyncEnumerable<LlmRunExecutionRequest> DequeueAllAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

}
