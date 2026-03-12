using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.CQRS.Core.Streaming;
using FluentAssertions;

namespace Aevatar.CQRS.Core.Tests;

public sealed class DefaultCommandInteractionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenDispatchFails_ShouldReturnFailure()
    {
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Failure("dispatch_failed")));

        var result = await service.ExecuteAsync(
            "command-1",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("dispatch_failed");
        result.Receipt.Should().BeNull();
        result.FinalizeResult.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenStreamObservesTerminalEvent_ShouldEmitAcceptedFramesFinalizeAndRelease()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Push("done:completed");
        sink.Push("after_terminal");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        var finalizeEmitter = new RecordingFinalizeEmitter();
        var accepted = new List<TestReceipt>();
        var frames = new List<string>();
        var receipt = new TestReceipt("target-1", "receipt-1");
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                new CommandDispatchExecution<TestTarget, TestReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-1", "corr-1", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-1" },
                    Receipt = receipt,
                })),
            finalizeEmitter: finalizeEmitter);

        var result = await service.ExecuteAsync(
            "command-1",
            (frame, _) =>
            {
                frames.Add(frame);
                return ValueTask.CompletedTask;
            },
            (acceptedReceipt, _) =>
            {
                accepted.Add(acceptedReceipt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<string>("completed", true));
        accepted.Should().ContainSingle().Which.Should().Be(receipt);
        frames.Should().Equal("progress", "done:completed");
        finalizeEmitter.Calls.Should().ContainSingle();
        finalizeEmitter.Calls[0].Receipt.Should().Be(receipt);
        finalizeEmitter.Calls[0].Completion.Should().Be("completed");
        finalizeEmitter.Calls[0].Completed.Should().BeTrue();
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeTrue();
        target.ReleaseCalls[0].Cleanup.ObservedCompletion.Should().Be("completed");
        target.ReleaseCalls[0].Cleanup.DurableCompletion.HasTerminalCompletion.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenLiveStreamNeverCompletes_ShouldUseDurableCompletionForFinalizeAndCleanup()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        var finalizeEmitter = new RecordingFinalizeEmitter();
        var durableResolver = new RecordingDurableResolver(
            new CommandDurableCompletionObservation<string>(true, "durable_completed"));
        var receipt = new TestReceipt("target-1", "receipt-2");
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                new CommandDispatchExecution<TestTarget, TestReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-2", "corr-2", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-2" },
                    Receipt = receipt,
                })),
            finalizeEmitter: finalizeEmitter,
            durableResolver: durableResolver);

        var result = await service.ExecuteAsync(
            "command-2",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<string>("durable_completed", true));
        durableResolver.Calls.Should().Be(1);
        finalizeEmitter.Calls.Should().ContainSingle();
        finalizeEmitter.Calls[0].Completion.Should().Be("durable_completed");
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeTrue();
        target.ReleaseCalls[0].Cleanup.DurableCompletion.HasTerminalCompletion.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenCleanupFailsAfterSuccess_ShouldPreserveSuccess()
    {
        var sink = new EventChannel<string>();
        sink.Push("done:completed");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        target.ReleaseException = new InvalidOperationException("cleanup failed");
        var receipt = new TestReceipt("target-1", "receipt-3");
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                new CommandDispatchExecution<TestTarget, TestReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-3", "corr-3", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-3" },
                    Receipt = receipt,
                })));

        var result = await service.ExecuteAsync(
            "command-3",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.FinalizeResult.Should().Be(new CommandInteractionFinalizeResult<string>("completed", true));
    }

    [Fact]
    public async Task ExecuteAsync_WhenDurableResolutionThrows_ShouldNotRetryDuringCleanup()
    {
        var sink = new EventChannel<string>();
        sink.Push("progress");
        sink.Complete();

        var target = new TestTarget("target-1", sink);
        var receipt = new TestReceipt("target-1", "receipt-4");
        var durableResolver = new ThrowingDurableResolver(new TimeoutException("durable-timeout"));
        var service = CreateService(
            new TestDispatchPipeline(CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>.Success(
                new CommandDispatchExecution<TestTarget, TestReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-4", "corr-4", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-4" },
                    Receipt = receipt,
                })),
            durableResolver: durableResolver);

        var act = () => service.ExecuteAsync(
            "command-4",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("durable-timeout");
        durableResolver.Calls.Should().Be(1);
        target.ReleaseCalls.Should().ContainSingle();
        target.ReleaseCalls[0].Cleanup.ObservedCompleted.Should().BeFalse();
    }

    private static DefaultCommandInteractionService<string, TestTarget, TestReceipt, string, string, string, string> CreateService(
        ICommandDispatchPipeline<string, TestTarget, TestReceipt, string> dispatchPipeline,
        ICommandCompletionPolicy<string, string>? completionPolicy = null,
        ICommandFinalizeEmitter<TestReceipt, string, string>? finalizeEmitter = null,
        ICommandDurableCompletionResolver<TestReceipt, string>? durableResolver = null) =>
        new(
            dispatchPipeline,
            new DefaultEventOutputStream<string, string>(new PassThroughFrameMapper()),
            completionPolicy ?? new TestCompletionPolicy(),
            finalizeEmitter ?? new RecordingFinalizeEmitter(),
            durableResolver ?? new RecordingDurableResolver(CommandDurableCompletionObservation<string>.Incomplete));

    private sealed record TestReceipt(string TargetId, string ReceiptId);

    private sealed class TestTarget(string targetId, IEventSink<string> sink)
        : ICommandEventTarget<string>,
          ICommandInteractionCleanupTarget<TestReceipt, string>
    {
        public string TargetId { get; } = targetId;
        public List<(TestReceipt Receipt, CommandInteractionCleanupContext<string> Cleanup)> ReleaseCalls { get; } = [];
        public Exception? ReleaseException { get; set; }

        public IEventSink<string> RequireLiveSink() => sink;

        public Task ReleaseAfterInteractionAsync(
            TestReceipt receipt,
            CommandInteractionCleanupContext<string> cleanup,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReleaseCalls.Add((receipt, cleanup));
            if (ReleaseException != null)
                throw ReleaseException;

            return Task.CompletedTask;
        }
    }

    private sealed class TestDispatchPipeline(
        CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string> result)
        : ICommandDispatchPipeline<string, TestTarget, TestReceipt, string>
    {
        public Task<CommandTargetResolution<CommandDispatchExecution<TestTarget, TestReceipt>, string>> DispatchAsync(
            string command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class TestCompletionPolicy : ICommandCompletionPolicy<string, string>
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

    private sealed class RecordingFinalizeEmitter : ICommandFinalizeEmitter<TestReceipt, string, string>
    {
        public List<(TestReceipt Receipt, string Completion, bool Completed)> Calls { get; } = [];

        public Task EmitAsync(
            TestReceipt receipt,
            string completion,
            bool completed,
            Func<string, CancellationToken, ValueTask> emitAsync,
            CancellationToken ct = default)
        {
            _ = emitAsync;
            ct.ThrowIfCancellationRequested();
            Calls.Add((receipt, completion, completed));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDurableResolver(
        CommandDurableCompletionObservation<string> observation)
        : ICommandDurableCompletionResolver<TestReceipt, string>
    {
        public int Calls { get; private set; }

        public Task<CommandDurableCompletionObservation<string>> ResolveAsync(
            TestReceipt receipt,
            CancellationToken ct = default)
        {
            _ = receipt;
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(observation);
        }
    }

    private sealed class ThrowingDurableResolver(Exception exception)
        : ICommandDurableCompletionResolver<TestReceipt, string>
    {
        public int Calls { get; private set; }

        public Task<CommandDurableCompletionObservation<string>> ResolveAsync(
            TestReceipt receipt,
            CancellationToken ct = default)
        {
            _ = receipt;
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromException<CommandDurableCompletionObservation<string>>(exception);
        }
    }

    private sealed class PassThroughFrameMapper : IEventFrameMapper<string, string>
    {
        public string Map(string evt) => evt;
    }
}

public sealed class FallbackCommandServiceTests
{
    [Fact]
    public async Task FallbackCommandInteractionService_ShouldRetryWithFallbackCommand_WhenPolicyMatches()
    {
        var service = new FallbackCommandInteractionService<string, string, string, string, string>(
            new RecordingInteractionService
            {
                InteractionException = new InvalidOperationException("primary failed"),
                Result = CommandInteractionResult<string, string, string>.Success("receipt", new CommandInteractionFinalizeResult<string>("done", true)),
            },
            new RetryOnInvalidOperationPolicy());

        var result = await service.ExecuteAsync(
            "primary",
            static (_, _) => ValueTask.CompletedTask,
            ct: CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be("receipt");
    }

    [Fact]
    public async Task FallbackCommandDispatchService_ShouldRetryWithFallbackCommand_WhenPolicyMatches()
    {
        var service = new FallbackCommandDispatchService<string, string, string>(
            new RecordingDispatchService
            {
                DispatchException = new InvalidOperationException("primary failed"),
                Result = CommandDispatchResult<string, string>.Success("receipt"),
            },
            new RetryOnInvalidOperationPolicy());

        var result = await service.DispatchAsync("primary", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be("receipt");
    }

    private sealed class RecordingInteractionService : ICommandInteractionService<string, string, string, string, string>
    {
        public Exception? InteractionException { get; set; }
        public CommandInteractionResult<string, string, string> Result { get; set; } =
            CommandInteractionResult<string, string, string>.Failure("failed");

        private bool _hasThrown;

        public Task<CommandInteractionResult<string, string, string>> ExecuteAsync(
            string command,
            Func<string, CancellationToken, ValueTask> emitAsync,
            Func<string, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            _ = command;
            _ = emitAsync;
            _ = onAcceptedAsync;
            ct.ThrowIfCancellationRequested();
            if (!_hasThrown && InteractionException != null)
            {
                _hasThrown = true;
                throw InteractionException;
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingDispatchService : ICommandDispatchService<string, string, string>
    {
        public Exception? DispatchException { get; set; }
        public CommandDispatchResult<string, string> Result { get; set; } =
            CommandDispatchResult<string, string>.Failure("failed");

        private bool _hasThrown;

        public Task<CommandDispatchResult<string, string>> DispatchAsync(
            string command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            if (!_hasThrown && DispatchException != null)
            {
                _hasThrown = true;
                throw DispatchException;
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class RetryOnInvalidOperationPolicy : ICommandFallbackPolicy<string>
    {
        public bool TryCreateFallbackCommand(string command, Exception exception, out string fallbackCommand)
        {
            if (exception is InvalidOperationException)
            {
                fallbackCommand = command + ":fallback";
                return true;
            }

            fallbackCommand = command;
            return false;
        }
    }
}
