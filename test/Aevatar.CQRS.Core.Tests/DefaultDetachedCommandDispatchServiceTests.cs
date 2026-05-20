using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using FluentAssertions;

namespace Aevatar.CQRS.Core.Tests;

public sealed class DefaultDetachedCommandDispatchServiceTests
{
    [Fact]
    public async Task DispatchAsync_ShouldReturnAcceptedReceipt_FromDispatchPipeline()
    {
        var target = new DetachedTestTarget("target-1");
        var receipt = new DetachedReceipt("target-1", "receipt-1");
        var pipeline = new DetachedPipeline(SuccessExecution(target, receipt, "cmd-1", "corr-1", "env-1"));
        var outputStream = new DetachedOutputStream();
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            pipeline,
            outputStream,
            new DetachedCompletionPolicy());

        var result = await service.DispatchAsync("command-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
        pipeline.DispatchCalls.Should().Be(1);
        outputStream.PumpCalls.Should().Be(0);
        target.RequireLiveSinkCalls.Should().Be(0);
        target.PublishSignalCalls.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturnFailure_AndPerformNoMonitorWork_WhenPipelineFails()
    {
        var pipeline = new DetachedPipeline(
            CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Failure("dispatch_failed"));
        var outputStream = new DetachedOutputStream();
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>(
            pipeline,
            outputStream,
            new DetachedCompletionPolicy());

        var result = await service.DispatchAsync("command-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("dispatch_failed");
        outputStream.PumpCalls.Should().Be(0);
    }

    [Fact]
    public void Type_ShouldNotImplementAsyncDisposable()
    {
        typeof(DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string, string, string, string>)
            .Should().NotBeAssignableTo<IAsyncDisposable>();
    }

    [Fact]
    public void Source_ShouldRejectDetachedMonitorAndDrainArtifacts()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs"));

        source.Should().Contain("Refactor (iter18/cluster-005):");
        source.Should().Contain("Old pattern: DefaultDetachedCommandDispatchService 在 accepted-only path 持有 live sink");
        source.Should().Contain("New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)");
        source.Should().NotContain("DrainAndSignalAsync");
        source.Should().NotContain("RequireLiveSink");
        source.Should().NotContain("PumpAsync");
        source.Should().NotContain("PublishDetachedCommandSignalAsync");
        source.Should().NotContain("Task.Run");
        source.Should().NotContain("ContinueWith");
        source.Should().NotContain("_inflightCount");
        source.Should().NotContain("_drainComplete");
    }

    private sealed record DetachedReceipt(string TargetId, string ReceiptId);

    private sealed class DetachedTestTarget(string targetId) : ICommandDispatchTarget
    {
        public string TargetId { get; } = targetId;
        public int RequireLiveSinkCalls { get; private set; }
        public int PublishSignalCalls { get; private set; }

        public IEventSink<string> RequireLiveSink()
        {
            RequireLiveSinkCalls++;
            throw new InvalidOperationException("Live sink must not be acquired.");
        }

        public Task PublishDetachedCommandSignalAsync(
            DetachedCommandSignal<DetachedReceipt, string> signal,
            CancellationToken ct = default)
        {
            _ = signal;
            ct.ThrowIfCancellationRequested();
            PublishSignalCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class DetachedPipeline(
        CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string> result)
        : ICommandDispatchPipeline<string, DetachedTestTarget, DetachedReceipt, string>
    {
        public int DispatchCalls { get; private set; }

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
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            DispatchCalls++;
            return Task.FromResult(result);
        }
    }

    private sealed class DetachedOutputStream : IEventOutputStream<string, string>
    {
        public int PumpCalls { get; private set; }

        public Task PumpAsync(
            IAsyncEnumerable<string> events,
            Func<string, CancellationToken, ValueTask> emitAsync,
            Func<string, bool>? shouldStop = null,
            CancellationToken ct = default)
        {
            _ = events;
            _ = emitAsync;
            _ = shouldStop;
            ct.ThrowIfCancellationRequested();
            PumpCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class DetachedCompletionPolicy : ICommandCompletionPolicy<string, string>
    {
        public string IncompleteCompletion => string.Empty;

        public bool TryResolve(string evt, out string completion)
        {
            _ = evt;
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
}
