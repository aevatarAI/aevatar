using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using FluentAssertions;
using System.Reflection;

namespace Aevatar.CQRS.Core.Tests;

// Test-add (test-coverage/cluster-036):
//   Covers refactor-introduced behavior in DefaultDetachedCommandDispatchService.cs:58-64,105-107,121-132,151-159.
//   Cluster intent: detached monitoring publishes typed continuation signals while target-owned continuations finalize.
public sealed class DefaultDetachedCommandDispatchServiceTests
{
    [Fact]
    public async Task DispatchAsync_ShouldReturnFailure_WhenPipelineFails()
    {
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Failure("dispatch_failed")),
            new DetachedOutputStream(),
            new DetachedCompletionPolicy());

        var result = await service.DispatchAsync("command-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("dispatch_failed");
    }

    [Fact]
    public async Task DispatchAsync_ShouldPublishCompletionSignal_WithoutReleasingTarget()
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
            new DetachedCompletionPolicy());

        var result = await service.DispatchAsync("command-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
        await outputStream.PumpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await target.SignalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        target.Signals.Should().ContainSingle();
        target.Signals[0].Should().Be(new DetachedCommandCompleted<DetachedReceipt, string>(receipt, "completed"));
        target.ReleaseCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldPublishTimeoutSignal_WhenLiveStreamEndsWithoutCompletion()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Complete();

        var target = new DetachedTestTarget("target-timeout", sink);
        var receipt = new DetachedReceipt("target-timeout", "receipt-timeout");
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(SuccessExecution(target, receipt, "cmd-timeout", "corr-timeout", "env-timeout")),
            new DetachedOutputStream(),
            new DetachedCompletionPolicy());

        var result = await service.DispatchAsync("command-timeout", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        await target.SignalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        target.Signals.Should().ContainSingle()
            .Which.Should().Be(new DetachedCommandTimeout<DetachedReceipt, string>(receipt, string.Empty));
        target.ReleaseCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldPublishTimeoutSignal_WhenMonitorFailsBeforeCompletion()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Complete();

        var target = new DetachedTestTarget("target-fault", sink);
        var receipt = new DetachedReceipt("target-fault", "receipt-fault");
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(SuccessExecution(target, receipt, "cmd-fault", "corr-fault", "env-fault")),
            new DetachedOutputStream(throwAfterEvents: 1),
            new DetachedCompletionPolicy());

        var result = await service.DispatchAsync("command-fault", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        await target.SignalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        target.Signals.Should().ContainSingle()
            .Which.Should().Be(new DetachedCommandTimeout<DetachedReceipt, string>(receipt, string.Empty));
        target.ReleaseCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldNotPublishTimeout_WhenMonitorFailsAfterCompletionWasObserved()
    {
        var sink = new EventChannel<string>();
        sink.Push("done:completed");
        sink.Complete();

        var target = new DetachedTestTarget("target-after-complete-fault", sink);
        var receipt = new DetachedReceipt("target-after-complete-fault", "receipt-after-complete-fault");
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(SuccessExecution(target, receipt, "cmd-after-complete-fault", "corr-after-complete-fault", "env-after-complete-fault")),
            new DetachedOutputStream(throwAfterShouldStop: true),
            new DetachedCompletionPolicy());

        var result = await service.DispatchAsync("command-after-complete-fault", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        await service.DisposeAsync();
        target.Signals.Should().BeEmpty();
        target.ReleaseCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldSwallowBestEffortTimeoutPublishFailure_WhenMonitorFails()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Complete();

        var target = new DetachedTestTarget("target-publish-failure", sink)
        {
            PublishSignalException = new InvalidOperationException("signal publish failed"),
        };
        var receipt = new DetachedReceipt("target-publish-failure", "receipt-publish-failure");
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(SuccessExecution(target, receipt, "cmd-publish-failure", "corr-publish-failure", "env-publish-failure")),
            new DetachedOutputStream(throwAfterEvents: 1),
            new DetachedCompletionPolicy());

        var result = await service.DispatchAsync("command-publish-failure", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        await service.DisposeAsync();
        target.PublishSignalCalls.Should().Be(1);
        target.Signals.Should().BeEmpty();
        target.ReleaseCalls.Should().BeEmpty();
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
            shutdownSignal: new TestShutdownSignal(cts.Token));

        await service.DispatchAsync("command-2", CancellationToken.None);
        await pumpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();

        await target.SignalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        target.Signals.Should().ContainSingle();
        target.Signals[0].Should().BeOfType<DetachedCommandTimeout<DetachedReceipt, string>>();
        target.ReleaseCalls.Should().BeEmpty();
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
            new DetachedCompletionPolicy());

        await service.DispatchAsync("command-3", CancellationToken.None);

        await service.DisposeAsync();

        target.Signals.Should().ContainSingle();
        target.Signals[0].Should().Be(new DetachedCommandCompleted<DetachedReceipt, string>(receipt, "ok"));
        target.ReleaseCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_ShouldWaitForInflightDrainUntilStreamPublishesTimeoutSignal()
    {
        var sink = new EventChannel<string>();
        var target = new DetachedTestTarget("target-dispose-wait", sink);
        var receipt = new DetachedReceipt("target-dispose-wait", "receipt-dispose-wait");
        var pumpStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(SuccessExecution(target, receipt, "cmd-dispose-wait", "corr-dispose-wait", "env-dispose-wait")),
            new DetachedOutputStream(onPumpStarted: pumpStarted),
            new DetachedCompletionPolicy());

        await service.DispatchAsync("command-dispose-wait", CancellationToken.None);
        await pumpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposeTask = service.DisposeAsync().AsTask();

        disposeTask.IsCompleted.Should().BeFalse("dispose must honor the inflight drain instead of returning accepted-only");

        sink.Complete();

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        target.Signals.Should().ContainSingle()
            .Which.Should().Be(new DetachedCommandTimeout<DetachedReceipt, string>(receipt, string.Empty));
        target.ReleaseCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_ShouldSwallowDrainTimeout()
    {
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            new DetachedPipeline(CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Failure("unused")),
            new DetachedOutputStream(),
            new DetachedCompletionPolicy());
        var drainComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        drainComplete.TrySetException(new TimeoutException("drain timeout"));
        SetPrivateField(service, "_inflightCount", 1);
        SetPrivateField(service, "_drainComplete", drainComplete);

        var act = async () => await service.DisposeAsync();

        await act.Should().NotThrowAsync("dispose is a best-effort drain and must not fail command callers on timeout");
    }

    [Fact]
    public void Source_ShouldNotUseTaskRunForDetachedBusinessProgression()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs"));

        source.Should().NotContain("Task.Run");
        source.Should().Contain("Refactor (iter17/cluster-036):");
        source.Should().Contain("Old pattern: detached workers drained live events, resolved durable completion, and ran business cleanup.");
        source.Should().Contain("New principle: detached workers only publish typed completion signals; target-owned continuations finalize.");
    }

    private sealed record TestShutdownSignal(CancellationToken ShutdownToken) : ICommandDispatchShutdownSignal;

    private sealed record DetachedReceipt(string TargetId, string ReceiptId);

    private sealed class DetachedTestTarget(string targetId, IEventSink<string> sink)
        : ICommandEventTarget<string>,
          ICommandInteractionCleanupTarget<DetachedReceipt, string>,
          ICommandDetachedContinuationTarget<DetachedReceipt, string>
    {
        public string TargetId { get; } = targetId;

        public List<(DetachedReceipt Receipt, CommandInteractionCleanupContext<string> Cleanup)> ReleaseCalls { get; } = [];
        public TaskCompletionSource<bool> ReleaseObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<DetachedCommandSignal<DetachedReceipt, string>> Signals { get; } = [];
        public TaskCompletionSource<bool> SignalObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? PublishSignalException { get; init; }
        public int PublishSignalCalls { get; private set; }

        public IEventSink<string> RequireLiveSink() => sink;

        public Task ReleaseAfterInteractionAsync(
            DetachedReceipt receipt,
            CommandInteractionCleanupContext<string> cleanup,
            CancellationToken ct = default)
        {
            ReleaseCalls.Add((receipt, cleanup));
            ReleaseObserved.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task PublishDetachedCommandSignalAsync(
            DetachedCommandSignal<DetachedReceipt, string> signal,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PublishSignalCalls++;
            if (PublishSignalException != null)
                throw PublishSignalException;

            Signals.Add(signal);
            SignalObserved.TrySetResult(true);
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
        private readonly int? _throwAfterEvents;
        private readonly bool _throwAfterShouldStop;

        public DetachedOutputStream(
            TaskCompletionSource<bool>? onPumpStarted = null,
            int? throwAfterEvents = null,
            bool throwAfterShouldStop = false)
        {
            _onPumpStarted = onPumpStarted;
            _throwAfterEvents = throwAfterEvents;
            _throwAfterShouldStop = throwAfterShouldStop;
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
            var observedEvents = 0;
            await foreach (var evt in events.WithCancellation(ct))
            {
                observedEvents++;
                await emitAsync(evt, ct);
                if (shouldStop?.Invoke(evt) == true)
                {
                    if (_throwAfterShouldStop)
                        throw new InvalidOperationException("pump failed after completion");

                    return;
                }

                if (_throwAfterEvents == observedEvents)
                    throw new InvalidOperationException("pump failed before completion");
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root could not be found.");
    }

    private static CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string> SuccessExecution(
        DetachedTestTarget target,
        DetachedReceipt receipt,
        string commandId,
        string correlationId,
        string envelopeId) =>
        CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Success(
            new CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>
            {
                Target = target,
                Context = new CommandContext(target.TargetId, commandId, correlationId, new Dictionary<string, string>()),
                Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = envelopeId },
                Receipt = receipt,
            });

    private static void SetPrivateField<TService, TValue>(
        TService service,
        string fieldName,
        TValue value)
    {
        var field = typeof(TService).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull();
        field!.SetValue(service, value);
    }
}
