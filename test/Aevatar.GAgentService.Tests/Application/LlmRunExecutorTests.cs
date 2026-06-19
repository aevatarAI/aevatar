using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
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
    public async Task StartAsync_ShouldReturnBeforeStreamingLoopDispatchesRecordCommands()
    {
        var provider = new GateControlledLlmProviderFactory();
        var core = new LlmRunCore(provider, [], NullLogger<LlmRunCore>.Instance);
        var dispatch = new RecordingDispatchPort();
        var executor = new LlmRunExecutor(core, dispatch, NullLogger<LlmRunExecutor>.Instance);

        await executor.StartAsync(new LlmRunExecutionRequest(
            "session-actor-1",
            BuildRunRequest("resp_executor"),
            "run_1",
            "ApiKey"));

        dispatch.Calls.Should().BeEmpty();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        provider.Release.SetResult();
        await dispatch.WaitForCallsAsync(2, cts.Token);

        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls.Select(call => call.ActorId).Should().OnlyContain(actorId => actorId == "session-actor-1");
        var chunk = dispatch.Calls[0].Envelope.Payload!.Unpack<RecordLlmStreamChunkObserved>();
        chunk.ResponseId.Should().Be("resp_executor");
        chunk.RunId.Should().Be("run_1");
        chunk.RecordId.Should().Be("run_1:chunk:1");
        chunk.DeltaText.Should().Be("done");
        dispatch.Calls[0].Envelope.Propagation!.CorrelationId.Should().Be(chunk.RecordId);

        var completed = dispatch.Calls[1].Envelope.Payload!.Unpack<RecordLlmRunCompleted>();
        completed.RecordId.Should().Be("run_1:completed:2");
        completed.OutputText.Should().Be("done");
    }

    private static LlmRunRequested BuildRunRequest(string responseId) =>
        new()
        {
            ResponseId = responseId,
            RunId = "run_1",
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            BearerToken = "token-1",
            Model = "test-model",
            Messages =
            {
                new LlmSessionRuntimeChatMessage
                {
                    Role = "user",
                    Content = "Run",
                },
            },
        };

    private sealed class GateControlledLlmProviderFactory : ILLMProviderFactory, ILLMProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            Entered.SetResult();
            await Release.Task.WaitAsync(ct);
            yield return new LLMStreamChunk
            {
                DeltaContent = "done",
                IsLast = true,
            };
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
}
