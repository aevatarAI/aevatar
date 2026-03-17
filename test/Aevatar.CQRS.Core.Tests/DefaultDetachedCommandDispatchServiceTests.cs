using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using FluentAssertions;

namespace Aevatar.CQRS.Core.Tests;

public sealed class DefaultDetachedCommandDispatchServiceTests
{
    [Fact]
    public async Task DispatchAsync_ShouldReturnFailure_WhenPipelineFails()
    {
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string>(
            new DetachedPipeline(CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Failure("dispatch_failed")));

        var result = await service.DispatchAsync("command-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("dispatch_failed");
    }

    [Fact]
    public async Task DispatchAsync_ShouldReturnReceipt_WhenPipelineSucceeds()
    {
        var target = new DetachedTestTarget("target-1");
        var receipt = new DetachedReceipt("target-1", "receipt-1");
        var service = new DefaultDetachedCommandDispatchService<string, DetachedTestTarget, DetachedReceipt, string>(
            new DetachedPipeline(CommandTargetResolution<CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>, string>.Success(
                new CommandDispatchExecution<DetachedTestTarget, DetachedReceipt>
                {
                    Target = target,
                    Context = new CommandContext("target-1", "cmd-1", "corr-1", new Dictionary<string, string>()),
                    Envelope = new Aevatar.Foundation.Abstractions.EventEnvelope { Id = "env-1" },
                    Receipt = receipt,
                })));

        var result = await service.DispatchAsync("command-1", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().Be(receipt);
    }

    private sealed record DetachedReceipt(string TargetId, string ReceiptId);

    private sealed class DetachedTestTarget(string targetId) : ICommandDispatchTarget
    {
        public string TargetId { get; } = targetId;
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
}
