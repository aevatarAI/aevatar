using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class LlmRunExecutorTests
{
    [Fact]
    public async Task StartAsync_ShouldDispatchRunStartedCommand_AndReturnBeforeStreamingLoopDispatchesRecordCommands()
    {
        var provider = new GateControlledLlmProviderFactory();
        var core = new LlmRunCore(provider, [], NullLogger<LlmRunCore>.Instance);
        var dispatch = new RecordingDispatchPort();
        var executor = new LlmRunExecutor(core, dispatch, NullLogger<LlmRunExecutor>.Instance);

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
        var executeTask = executor.ExecuteAsync(request, cts.Token);
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
        dispatch.Calls[1].Envelope.Propagation!.CorrelationId.Should().Be(chunk.RecordId);

        var completed = dispatch.Calls[2].Envelope.Payload!.Unpack<RecordLlmRunCompleted>();
        completed.RecordId.Should().Be("run_1:completed:2");
        completed.OutputText.Should().Be("done");
        AssertDirectEnvelope(dispatch.Calls[2], completed.RecordId, RecordLlmRunCompleted.Descriptor.FullName);
    }

    [Fact]
    public async Task RunExecutionReadyHook_ShouldExecuteRunOnlyAfterCommittedReadyEvent()
    {
        var scheduler = new RecordingLlmRunExecutionScheduler();
        var hook = new LlmRunExecutionReadyHook(scheduler);
        var sourceCommand = BuildRunRequest("resp_hook");
        sourceCommand.RunId = "stale-run";
        var context = new CommittedStatePublicationContext
        {
            ActorId = "session-actor-hook",
            ActorType = typeof(object),
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventData = Any.Pack(new LlmRunExecutionReadyEvent
                    {
                        ResponseId = "resp_hook",
                        RunId = "run_hook",
                        ReadyAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        ExecutionRequest = sourceCommand,
                    }),
                },
                StateRoot = Any.Pack(new LlmSessionState
                {
                    Record = new LlmSessionRecord
                    {
                        ResponseId = "resp_hook",
                        OriginKind = LlmSessionOriginKind.ApiKey,
                    },
                }),
            },
        };

        await hook.BeforePublishAsync(context, CancellationToken.None);

        var request = scheduler.ScheduledRequests.Should().ContainSingle().Subject;
        request.SessionActorId.Should().Be("session-actor-hook");
        request.ResponseId.Should().Be("resp_hook");
        request.RunId.Should().Be("run_hook");
        request.Command.ResponseId.Should().Be("resp_hook");
        request.Command.RunId.Should().Be("run_hook");
        request.OriginPlatform.Should().Be(LlmSessionOriginKind.ApiKey.ToString());
    }

    [Fact]
    public async Task RunExecutionReadyHook_WhenExecutionRequestComesFromLegacyLlmRunRequested_ShouldScheduleRun()
    {
        var scheduler = new RecordingLlmRunExecutionScheduler();
        var hook = new LlmRunExecutionReadyHook(scheduler);
        var executionRequest = BuildRunRequest("resp_legacy_hook");
        executionRequest.RunId = "stale-run";
        executionRequest.ToolContext = new Aevatar.AI.Abstractions.AgentToolExecutionContextPayload
        {
            Channel = new Aevatar.AI.Abstractions.AgentToolChannelContextPayload
            {
                Platform = "LegacyPlatform",
            },
        };
        var context = new CommittedStatePublicationContext
        {
            ActorId = "session-actor-legacy-hook",
            ActorType = typeof(object),
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventData = Any.Pack(new LlmRunExecutionReadyEvent
                    {
                        ResponseId = "resp_legacy_hook",
                        RunId = "run_legacy_hook",
                        ReadyAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        ExecutionRequest = executionRequest,
                    }),
                },
                StateRoot = Any.Pack(new LlmSessionState
                {
                    Record = new LlmSessionRecord
                    {
                        ResponseId = "resp_legacy_hook",
                        OriginKind = LlmSessionOriginKind.Channel,
                    },
                }),
            },
        };

        await hook.BeforePublishAsync(context, CancellationToken.None);

        var request = scheduler.ScheduledRequests.Should().ContainSingle().Subject;
        request.SessionActorId.Should().Be("session-actor-legacy-hook");
        request.ResponseId.Should().Be("resp_legacy_hook");
        request.RunId.Should().Be("run_legacy_hook");
        request.Command.ResponseId.Should().Be("resp_legacy_hook");
        request.Command.RunId.Should().Be("run_legacy_hook");
        request.Command.ToolContext.Should().BeEquivalentTo(executionRequest.ToolContext);
        request.OriginPlatform.Should().Be(LlmSessionOriginKind.Channel.ToString());
    }

    [Fact]
    public async Task RunExecutionScheduler_ShouldDispatchExecutionCommandToProvisionedActor()
    {
        var provisioner = new RecordingExecutionTargetProvisioner("llm-run-execution:resp_scheduler:run_1");
        var dispatch = new RecordingDispatchPort();
        var scheduler = new LlmRunExecutionScheduler(provisioner, dispatch);
        var request = new LlmRunExecutorRequest(
            " session-actor-scheduler ",
            " resp_scheduler ",
            " run_1 ",
            BuildRunRequest("resp_scheduler"),
            "ApiKey");

        await scheduler.ScheduleAsync(request, CancellationToken.None);

        provisioner.Requests.Should().ContainSingle().Which.Should().Be(request);
        var call = dispatch.Calls.Should().ContainSingle().Subject;
        call.ActorId.Should().Be("llm-run-execution:resp_scheduler:run_1");
        call.Envelope.Id.Should().Be("execute-resp_scheduler-run_1");
        call.Envelope.Route!.PublisherActorId.Should().Be("gagent-service.llm-run-executor");
        call.Envelope.Route.GetTargetActorId().Should().Be("llm-run-execution:resp_scheduler:run_1");
        call.Envelope.Propagation!.CorrelationId.Should().Be("resp_scheduler");
        var command = call.Envelope.Payload!.Unpack<ExecuteLlmRunRequested>();
        command.SessionActorId.Should().Be("session-actor-scheduler");
        command.ResponseId.Should().Be("resp_scheduler");
        command.RunId.Should().Be("run_1");
        command.Command.ResponseId.Should().Be("resp_scheduler");
        command.OriginPlatform.Should().Be("ApiKey");
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
        var core = new LlmRunCore(provider, [], NullLogger<LlmRunCore>.Instance);
        var dispatch = new RecordingDispatchPort();
        var executor = new LlmRunExecutor(core, dispatch, NullLogger<LlmRunExecutor>.Instance);

        var request = new LlmRunExecutorRequest(
            "session-actor-2",
            "resp_forwarded",
            " run_forwarded ",
            BuildRunRequest("resp_forwarded", BuildForwardedSelection()),
            "ApiKey");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(request, cts.Token);
        await dispatch.WaitForCallsAsync(5, cts.Token);
        await executeTask;

        dispatch.Calls.Select(call => call.ActorId).Should().OnlyContain(actorId => actorId == "session-actor-2");
        var firstChunk = dispatch.Calls[0].Envelope.Payload!.Unpack<RecordLlmStreamChunkObserved>();
        firstChunk.RecordId.Should().Be("run_forwarded:chunk:1");
        firstChunk.ToolCallDelta!.CallId.Should().Be("call_1");

        var secondChunk = dispatch.Calls[1].Envelope.Payload!.Unpack<RecordLlmStreamChunkObserved>();
        secondChunk.RecordId.Should().Be("run_forwarded:chunk:2");
        secondChunk.Usage!.TotalTokens.Should().Be(12);

        var observedTool = dispatch.Calls[2].Envelope.Payload!.Unpack<RecordLlmToolCallObserved>();
        observedTool.RecordId.Should().Be("run_forwarded:tool:3");
        observedTool.RunId.Should().Be("run_forwarded");
        observedTool.Forwarded.Should().BeTrue();
        observedTool.ToolCall!.Arguments.Fields["city"].StringValue.Should().Be("Singapore");

        var forwarded = dispatch.Calls[3].Envelope.Payload!.Unpack<RecordLlmForwardedToolCallEmitted>();
        forwarded.RecordId.Should().Be("run_forwarded:forwarded-tool:4");
        forwarded.RunId.Should().Be("run_forwarded");
        forwarded.Call!.SchemaHash.Should().Be("schema-1");
        ResponsesJsonValues.ToBoundaryJson(forwarded.Call.Arguments).Should().Be("""{"city":"Singapore"}""");

        var completed = dispatch.Calls[4].Envelope.Payload!.Unpack<RecordLlmRunCompleted>();
        completed.RecordId.Should().Be("run_forwarded:completed:5");
        completed.ForwardedToolCalls.Should().ContainSingle()
            .Which.CallId.Should().Be("call_1");
        AssertDirectEnvelope(dispatch.Calls[3], forwarded.RecordId, RecordLlmForwardedToolCallEmitted.Descriptor.FullName);
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
        var core = new LlmRunCore(provider, [toolProvider], NullLogger<LlmRunCore>.Instance);
        var dispatch = new RecordingDispatchPort();
        var executor = new LlmRunExecutor(core, dispatch, NullLogger<LlmRunExecutor>.Instance);
        var selection = BuildForwardedSelection();
        selection.SubstitutedToolNames.Add("get_weather");

        var request = new LlmRunExecutorRequest(
            "session-actor-3",
            "resp_local",
            "run_local",
            BuildRunRequest("resp_local", selection),
            "ApiKey");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(request, cts.Token);
        await dispatch.WaitForCallsAsync(4, cts.Token);
        await executeTask;

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
        var core = new LlmRunCore(provider, [], NullLogger<LlmRunCore>.Instance);
        var dispatch = new RecordingDispatchPort();
        var executor = new LlmRunExecutor(core, dispatch, NullLogger<LlmRunExecutor>.Instance);

        var request = new LlmRunExecutorRequest(
            "session-actor-4",
            "resp_failed",
            "run_failed",
            BuildRunRequest("resp_failed"),
            "ApiKey");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(request, cts.Token);
        await dispatch.WaitForCallsAsync(1, cts.Token);
        await executeTask;

        var failed = dispatch.Calls[0].Envelope.Payload!.Unpack<RecordLlmRunFailed>();
        failed.RecordId.Should().Be("run_failed:failed:1");
        failed.RunId.Should().Be("run_failed");
        failed.FailureCode.Should().Be("authentication_required");
        failed.FailureMessage.Should().Contain("NyxID authentication required");
        AssertDirectEnvelope(dispatch.Calls[0], failed.RecordId, RecordLlmRunFailed.Descriptor.FullName);
    }

    [Fact]
    public async Task StartAsync_WhenProviderCancels_ShouldDispatchRunCancelledRecordCommand()
    {
        var provider = new ThrowingLlmProviderFactory(new OperationCanceledException());
        var core = new LlmRunCore(provider, [], NullLogger<LlmRunCore>.Instance);
        var dispatch = new RecordingDispatchPort();
        var executor = new LlmRunExecutor(core, dispatch, NullLogger<LlmRunExecutor>.Instance);

        var request = new LlmRunExecutorRequest(
            "session-actor-5",
            "resp_cancelled",
            "run_cancelled",
            BuildRunRequest("resp_cancelled"),
            "ApiKey");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(request, cts.Token);
        await dispatch.WaitForCallsAsync(1, cts.Token);
        await executeTask;

        var cancelled = dispatch.Calls[0].Envelope.Payload!.Unpack<RecordLlmRunCancelled>();
        cancelled.RecordId.Should().Be("run_cancelled:cancelled:1");
        cancelled.RunId.Should().Be("run_cancelled");
        cancelled.CancelledAt.Should().NotBeNull();
        AssertDirectEnvelope(dispatch.Calls[0], cancelled.RecordId, RecordLlmRunCancelled.Descriptor.FullName);
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
        var core = new LlmRunCore(provider, [], NullLogger<LlmRunCore>.Instance);
        var dispatch = new FailingThenRecordingDispatchPort(failuresBeforeSuccess: 2);
        var executor = new LlmRunExecutor(core, dispatch, NullLogger<LlmRunExecutor>.Instance);

        var request = new LlmRunExecutorRequest(
            "session-actor-6",
            "resp_executor_failed",
            "run_executor_failed",
            BuildRunRequest("resp_executor_failed"),
            "ApiKey");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = executor.ExecuteAsync(request, cts.Token);
        await dispatch.WaitForAttemptsAsync(3, cts.Token);
        await executeTask;

        dispatch.Calls.Should().ContainSingle();
        var failed = dispatch.Calls[0].Envelope.Payload!.Unpack<RecordLlmRunFailed>();
        failed.RecordId.Should().Be("run_executor_failed:executor-failed");
        failed.FailureCode.Should().Be("executor_failed");
        failed.FailureMessage.Should().Contain("Synthetic dispatch failure");
    }

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
        string payloadFullName)
    {
        call.Envelope.Id.Should().Be(recordId);
        call.Envelope.Payload!.TypeUrl.Should().EndWith(payloadFullName);
        call.Envelope.Route!.PublisherActorId.Should().Be("gagent-service.llm-run-executor");
        call.Envelope.Route.GetTargetActorId().Should().Be(call.ActorId);
        call.Envelope.Propagation!.CorrelationId.Should().Be(recordId);
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

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
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

    private sealed class FailingThenRecordingDispatchPort(int failuresBeforeSuccess) : IActorDispatchPort
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

    private sealed class RecordingLlmRunExecutor : ILlmRunExecutionService
    {
        private readonly TaskCompletionSource _executeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<LlmRunExecutorRequest> ExecuteRequests { get; } = [];

        public Task ExecuteAsync(
            LlmRunExecutorRequest request,
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

    private sealed class RecordingLlmRunExecutionScheduler : ILlmRunExecutionScheduler
    {
        public List<LlmRunExecutorRequest> ScheduledRequests { get; } = [];

        public ValueTask ScheduleAsync(
            LlmRunExecutorRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            ScheduledRequests.Add(request);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingExecutionTargetProvisioner(string actorId) : ILlmRunExecutionTargetProvisioner
    {
        public List<LlmRunExecutorRequest> Requests { get; } = [];

        public Task<string> EnsureExecutionTargetAsync(
            LlmRunExecutorRequest request,
            CancellationToken ct = default)
        {
            _ = ct;
            Requests.Add(request);
            return Task.FromResult(actorId);
        }
    }
}
