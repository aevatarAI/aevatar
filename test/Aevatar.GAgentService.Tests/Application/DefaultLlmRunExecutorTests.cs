using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class DefaultLlmRunExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldDispatchStartedChunksAndCompletedInOrder()
    {
        var core = new ScriptedRunCore(async (sink, ct) =>
        {
            await sink.RecordStreamChunkObservedAsync(Chunk("a"), ct);
            await sink.RecordStreamChunkObservedAsync(Chunk("b"), ct);
            await sink.RecordRunCompletedAsync(Completed("ab"), ct);
        });
        var dispatch = new RecordingDispatchPort();
        var executor = CreateExecutor(core, dispatch);

        var request = Request();
        await executor.StartAsync(request);
        await executor.ExecuteAsync(request);

        Payloads(dispatch).Should().HaveCount(3);
        Payloads(dispatch)[0].Is(RecordRunStartedRequested.Descriptor).Should().BeTrue();
        var chunks = Payloads(dispatch)[1].Unpack<RecordStreamChunksObservedRequested>();
        chunks.Chunks.Select(static chunk => chunk.DeltaText).Should().Equal("a", "b");
        Payloads(dispatch)[2].Is(RecordRunCompletedRequested.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFlushEverySixteenChunks()
    {
        var core = new ScriptedRunCore(async (sink, ct) =>
        {
            for (var index = 0; index < 17; index++)
                await sink.RecordStreamChunkObservedAsync(Chunk(index.ToString()), ct);
            await sink.RecordRunCompletedAsync(Completed(string.Empty), ct);
        });
        var dispatch = new RecordingDispatchPort();
        var executor = CreateExecutor(core, dispatch);

        await executor.ExecuteAsync(Request());

        var chunkBatches = Payloads(dispatch)
            .Where(static payload => payload.Is(RecordStreamChunksObservedRequested.Descriptor))
            .Select(static payload => payload.Unpack<RecordStreamChunksObservedRequested>())
            .ToArray();
        chunkBatches.Should().HaveCount(2);
        chunkBatches[0].Chunks.Should().HaveCount(16);
        chunkBatches[1].Chunks.Should().ContainSingle()
            .Which.DeltaText.Should().Be("16");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFlushTimerWindowWithoutSleep()
    {
        var firstChunkRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var core = new ScriptedRunCore(async (sink, ct) =>
        {
            await sink.RecordStreamChunkObservedAsync(Chunk("timer"), ct);
            firstChunkRecorded.SetResult();
            await WaitForCompletionAsync(allowCompletion, ct);
            await sink.RecordRunCompletedAsync(Completed("timer"), ct);
        });
        var dispatch = new RecordingDispatchPort();
        var clock = new ManualExecutorClock();
        var executor = CreateExecutor(core, dispatch, clock);

        var runTask = executor.ExecuteAsync(Request());
        await firstChunkRecorded.Task;

        dispatch.Calls.Should().BeEmpty();
        clock.FireNextDelay();
        await dispatch.FirstCallRecorded.Task;
        dispatch.Calls.Should().ContainSingle();
        Payloads(dispatch)[0].Unpack<RecordStreamChunksObservedRequested>()
            .Chunks.Should().ContainSingle()
            .Which.DeltaText.Should().Be("timer");

        allowCompletion.SetResult();
        await runTask;
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFlushPendingChunksBeforeTerminalAndError()
    {
        var completedDispatch = new RecordingDispatchPort();
        var completedExecutor = CreateExecutor(
            new ScriptedRunCore(async (sink, ct) =>
            {
                await sink.RecordStreamChunkObservedAsync(Chunk("pending"), ct);
                await sink.RecordRunCompletedAsync(Completed("pending"), ct);
            }),
            completedDispatch);

        await completedExecutor.ExecuteAsync(Request());

        Payloads(completedDispatch)[0].Is(RecordStreamChunksObservedRequested.Descriptor).Should().BeTrue();
        Payloads(completedDispatch)[1].Is(RecordRunCompletedRequested.Descriptor).Should().BeTrue();

        var failedDispatch = new RecordingDispatchPort();
        var failedExecutor = CreateExecutor(
            new ScriptedRunCore(async (sink, ct) =>
            {
                await sink.RecordStreamChunkObservedAsync(Chunk("pending"), ct);
                await sink.RecordRunFailedAsync(Failed("provider_error"), ct);
            }),
            failedDispatch);

        await failedExecutor.ExecuteAsync(Request());

        Payloads(failedDispatch)[0].Is(RecordStreamChunksObservedRequested.Descriptor).Should().BeTrue();
        Payloads(failedDispatch)[1].Is(RecordRunFailedRequested.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenCoreThrows_ShouldDispatchFailed()
    {
        var core = new ScriptedRunCore((_, _) => throw new InvalidOperationException("provider broke"));
        var dispatch = new RecordingDispatchPort();
        var executor = CreateExecutor(core, dispatch);

        await executor.ExecuteAsync(Request());

        Payloads(dispatch).Should().ContainSingle();
        var failed = Payloads(dispatch)[0].Unpack<RecordRunFailedRequested>().Failed;
        failed.FailureCode.Should().Be("execution_failed");
        failed.FailureMessage.Should().Be("provider broke");
    }

    private static DefaultLlmRunExecutor CreateExecutor(
        ILlmRunCore core,
        RecordingDispatchPort dispatch,
        ILlmRunExecutorClock? clock = null) =>
        new(core, dispatch, clock ?? new ManualExecutorClock(), NullLogger<DefaultLlmRunExecutor>.Instance);

    private static LlmRunExecutorRequest Request() =>
        new(
            "actor-resp-1",
            "resp-1",
            "resp-1:llm-run",
            new LlmRunRequested
            {
                ResponseId = "resp-1",
                RunId = "resp-1:llm-run",
                RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00")),
            },
            "ApiKey");

    private static LlmStreamChunkObserved Chunk(string text) =>
        new()
        {
            ResponseId = "resp-1",
            RunId = "resp-1:llm-run",
            DeltaText = text,
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private static LlmRunCompleted Completed(string outputText) =>
        new()
        {
            ResponseId = "resp-1",
            RunId = "resp-1:llm-run",
            OutputText = outputText,
            CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private static LlmRunFailed Failed(string code) =>
        new()
        {
            ResponseId = "resp-1",
            RunId = "resp-1:llm-run",
            FailureCode = code,
            FailureMessage = "failed",
            FailedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private static IReadOnlyList<Any> Payloads(RecordingDispatchPort dispatch) =>
        dispatch.Calls.Select(static call => call.Envelope.Payload!).ToArray();

    private static async Task WaitForCompletionAsync(TaskCompletionSource completion, CancellationToken ct)
    {
        using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), completion);
        await completion.Task;
    }

    private sealed class ScriptedRunCore(
        Func<ILlmRunSink, CancellationToken, Task> execute) : ILlmRunCore
    {
        public Task RunAsync(
            LlmRunCoreRequest request,
            ILlmRunSink sink,
            CancellationToken ct = default) =>
            execute(sink, ct);
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public TaskCompletionSource FirstCallRecorded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope.Clone()));
            FirstCallRecorded.TrySetResult();
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class ManualExecutorClock : ILlmRunExecutorClock
    {
        private readonly Queue<TaskCompletionSource> _delays = [];

        public DateTimeOffset UtcNow { get; set; } =
            DateTimeOffset.Parse("2026-06-19T00:00:00+00:00");

        public Task DelayAsync(TimeSpan delay, CancellationToken ct = default)
        {
            var delayCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ct.CanBeCanceled)
                ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), delayCompletion);
            _delays.Enqueue(delayCompletion);
            return delayCompletion.Task;
        }

        public void FireNextDelay()
        {
            _delays.Dequeue().SetResult();
        }
    }
}
