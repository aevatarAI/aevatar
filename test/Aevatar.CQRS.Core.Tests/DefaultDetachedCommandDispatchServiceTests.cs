using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using FluentAssertions;

namespace Aevatar.CQRS.Core.Tests;

public sealed class DefaultDetachedCommandDispatchServiceTests
{
    [Fact]
    public async Task DispatchAsync_ShouldReturnFailure_WhenPipelineFails()
    {
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Failure("dispatch_failed")),
            new DetachedOutputStream(),
            new DetachedCompletionPolicy(),
            new DetachedDurableResolver(CommandDurableCompletionObservation<string>.Incomplete));

        var result = await service.DispatchAsync("command-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("dispatch_failed");
    }

    [Fact]
    public async Task DispatchAsync_ShouldDrainInBackground_AndReleaseTarget()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Push("done:completed");
        sink.Complete();

        var target = new DetachedTestTarget("target-1", sink);
        var receipt = new DetachedReceipt("target-1", "receipt-1");
        var outputStream = new DetachedOutputStream();
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Success(
                new CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-1", "corr-1", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-1" },
                    Receipt = receipt,
                })),
            outputStream,
            new DetachedCompletionPolicy(),
            new DetachedDurableResolver(CommandDurableCompletionObservation<string>.Incomplete));

        var result = await service.DispatchAsync("command-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
        await outputStream.PumpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await target.ReleaseObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeTrue();
        target.ReleaseCalls[0].Cleanup.ObservedCompletion.Should().Be("completed");
    }

    [Fact]
    public async Task ShutdownSignal_ShouldCancelInflightDrain()
    {
        using var cts = new CancellationTokenSource();
        var sink = new EventChannel<string>();
        var pumpStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new DetachedTestTarget("target-2", sink);
        var receipt = new DetachedReceipt("target-2", "receipt-2");
        var outputStream = new DetachedOutputStream(onPumpStarted: pumpStarted);

        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Success(
                new CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-2", "cmd-2", "corr-2", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-2" },
                    Receipt = receipt,
                })),
            outputStream,
            new DetachedCompletionPolicy(),
            new DetachedDurableResolver(CommandDurableCompletionObservation<string>.Incomplete),
            shutdownSignal: new TestShutdownSignal(cts.Token));

        await service.DispatchAsync("command-2", CancellationToken.None);
        await pumpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();

        await target.ReleaseObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_ShouldPassShutdownToken_ToCleanup()
    {
        using var cts = new CancellationTokenSource();
        var sink = new EventChannel<string>();
        sink.Push("done:completed");
        sink.Complete();

        var target = new DetachedTestTarget("target-4", sink);
        var receipt = new DetachedReceipt("target-4", "receipt-4");
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Success(
                new CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-4", "cmd-4", "corr-4", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-4" },
                    Receipt = receipt,
                })),
            new DetachedOutputStream(),
            new DetachedCompletionPolicy(),
            new DetachedDurableResolver(CommandDurableCompletionObservation<string>.Incomplete),
            shutdownSignal: new TestShutdownSignal(cts.Token));

        await service.DispatchAsync("command-4", CancellationToken.None);

        await target.ReleaseObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        target.ReleaseTokens.Should().ContainSingle().Which.Should().Be(cts.Token);
    }

    [Fact]
    public async Task DisposeAsync_ShouldDrainInflightTasks()
    {
        var sink = new EventChannel<string>();
        sink.Push("done:ok");
        sink.Complete();

        var target = new DetachedTestTarget("target-3", sink);
        var receipt = new DetachedReceipt("target-3", "receipt-3");

        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Success(
                new CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-3", "cmd-3", "corr-3", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-3" },
                    Receipt = receipt,
                })),
            new DetachedOutputStream(),
            new DetachedCompletionPolicy(),
            new DetachedDurableResolver(CommandDurableCompletionObservation<string>.Incomplete));

        await service.DispatchAsync("command-3", CancellationToken.None);

        await service.DisposeAsync();

        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeTrue();
    }

    private sealed record TestShutdownSignal(CancellationToken ShutdownToken) : ICommandDispatchShutdownSignal;

    private sealed record DetachedReceipt(string TargetId, string ReceiptId);

    private sealed class DetachedTestTarget(string targetId, IEventSink<string> sink)
        : ICommandEventTarget<string>,
          ICommandInteractionCleanupTarget<DetachedReceipt, string>
    {
        public string TargetId { get; } = targetId;

        public List<(DetachedReceipt Receipt, CommandInteractionCleanupContext<string> Cleanup)> ReleaseCalls { get; } = [];
        public List<CancellationToken> ReleaseTokens { get; } = [];
        public TaskCompletionSource<bool> ReleaseObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IEventSink<string> RequireLiveSink() => sink;

        public Task ReleaseAfterInteractionAsync(
            DetachedReceipt receipt,
            CommandInteractionCleanupContext<string> cleanup,
            CancellationToken ct = default)
        {
            ReleaseCalls.Add((receipt, cleanup));
            ReleaseTokens.Add(ct);
            ReleaseObserved.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class DetachedPipeline(
        CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string> result)
        : ICommandDispatchPipeline<string, DetachedTestTarget, DetachedReceipt, string>
    {
        public Task<CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>> PrepareAsync(
            string command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }

        public Task DispatchPreparedAsync(
            CommandDispatchExecution<DetachedTestTarget, DetachedReceipt> execution,
            CancellationToken ct = default)
        {
            _ = execution;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>> DispatchAsync(
            string command,
            CancellationToken ct = default) =>
            DispatchAsyncCore(command, ct);

        private async Task<CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>> DispatchAsyncCore(
            string command,
            CancellationToken ct)
        {
            var prepared = await PrepareAsync(command, ct);
            if (!prepared.Succeeded || prepared.Target == null)
                return prepared;
            await DispatchPreparedAsync(prepared.Target, ct);
            return prepared;
        }
    }

    private sealed class DetachedOutputStream : IEventOutputStream<string, string>
    {
        private readonly TaskCompletionSource<bool>? _onPumpStarted;

        public DetachedOutputStream(TaskCompletionSource<bool>? onPumpStarted = null)
        {
            _onPumpStarted = onPumpStarted;
            PumpStarted = onPumpStarted ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource<bool> PumpStarted { get; }

        public async Task PumpAsync(
            IAsyncEnumerable<string> events,
            Func<string, CancellationToken, ValueTask> emitAsync,
            Func<string, bool>? shouldStop = null,
            CancellationToken ct = default)
        {
            PumpStarted.TrySetResult(true);
            await foreach (var evt in events.WithCancellation(ct))
            {
                await emitAsync(evt, ct);
                if (shouldStop?.Invoke(evt) == true)
                    return;
            }
        }
    }

    private sealed class DetachedCompletionPolicy : ICommandCompletionPolicy<string, string>
    {
        public string IncompleteCompletion => string.Empty;

        public bool TryResolve(string evt, out string completion)
        {
            if (evt.StartsWith("done:", StringComparison.Ordinal))
            {
                completion = evt["done:".Length..];
                return true;
            }

            completion = string.Empty;
            return false;
        }
    }

    private sealed class DetachedDurableResolver(
        CommandDurableCompletionObservation<string> observation)
        : ICommandDurableCompletionResolver<DetachedReceipt, string>
    {
        public Task<CommandDurableCompletionObservation<string>> ResolveAsync(
            DetachedReceipt receipt,
            CancellationToken ct = default)
        {
            _ = receipt;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(observation);
        }
    }
}
