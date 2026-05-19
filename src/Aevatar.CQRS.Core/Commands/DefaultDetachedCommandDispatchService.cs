using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>
    : ICommandDispatchService<TCommand, TReceipt, TError>, IAsyncDisposable
    where TTarget : class, ICommandEventTarget<TEvent>, ICommandDetachedContinuationTarget<TReceipt, TCompletion>
{
    private readonly ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> _dispatchPipeline;
    private readonly IEventOutputStream<TEvent, TFrame> _outputStream;
    private readonly ICommandCompletionPolicy<TEvent, TCompletion> _completionPolicy;
    private readonly ILogger<DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>> _logger;
    private readonly CancellationToken _shutdownToken;
    private int _inflightCount;
    private volatile TaskCompletionSource _drainComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DefaultDetachedCommandDispatchService(
        ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> dispatchPipeline,
        IEventOutputStream<TEvent, TFrame> outputStream,
        ICommandCompletionPolicy<TEvent, TCompletion> completionPolicy,
        ICommandDispatchShutdownSignal? shutdownSignal = null,
        ILogger<DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>>? logger = null)
    {
        _dispatchPipeline = dispatchPipeline ?? throw new ArgumentNullException(nameof(dispatchPipeline));
        _outputStream = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
        _completionPolicy = completionPolicy ?? throw new ArgumentNullException(nameof(completionPolicy));
        _shutdownToken = shutdownSignal?.ShutdownToken ?? CancellationToken.None;
        _logger = logger ?? NullLogger<DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>>.Instance;
        // Start in signaled state since there are no inflight tasks initially.
        _drainComplete.TrySetResult();
    }

    public async Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dispatch = await _dispatchPipeline.DispatchAsync(command, ct);
        if (!dispatch.Succeeded || dispatch.Target == null)
            return CommandDispatchResult<TReceipt, TError>.Failure(dispatch.Error);

        var execution = dispatch.Target;
        StartDetachedDrain(execution.Target, execution.Receipt);
        return CommandDispatchResult<TReceipt, TError>.Success(execution.Receipt);
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _inflightCount) == 0)
            return;

        try
        {
            await _drainComplete.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            // Drain did not complete within timeout; swallow.
        }
    }

    private void StartDetachedDrain(
        TTarget target,
        TReceipt receipt)
    {
        // Refactor (iter17/cluster-036):
        // Old pattern: detached workers drained live events, resolved durable completion, and ran business cleanup.
        // New principle: detached workers only publish typed completion signals; target-owned continuations finalize.
        // Reset drain signal if transitioning from 0 to 1.
        if (Interlocked.Increment(ref _inflightCount) == 1)
            Interlocked.Exchange(ref _drainComplete, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        _ = DrainAndSignalAsync(target, receipt, _shutdownToken);
    }

    private async Task DrainAndSignalAsync(
        TTarget target,
        TReceipt receipt,
        CancellationToken ct)
    {
        var observedCompleted = false;
        var observedCompletion = _completionPolicy.IncompleteCompletion;

        try
        {
            await _outputStream.PumpAsync(
                target.RequireLiveSink().ReadAllAsync(ct),
                static (_, _) => ValueTask.CompletedTask,
                evt =>
                {
                    if (!_completionPolicy.TryResolve(evt, out var completion))
                        return false;

                    observedCompleted = true;
                    observedCompletion = completion;
                    return true;
                },
                ct);

            DetachedCommandSignal<TReceipt, TCompletion> signal = observedCompleted
                ? new DetachedCommandCompleted<TReceipt, TCompletion>(receipt, observedCompletion)
                : new DetachedCommandTimeout<TReceipt, TCompletion>(receipt, observedCompletion);

            await target.PublishDetachedCommandSignalAsync(signal, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Detached command monitoring cancelled during shutdown. command={CommandType}, target={TargetType}, targetId={TargetId}",
                typeof(TCommand).FullName,
                typeof(TTarget).FullName,
                target.TargetId);

            await PublishTimeoutSignalBestEffortAsync(target, receipt, observedCompletion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Detached command monitoring failed. command={CommandType}, target={TargetType}, targetId={TargetId}",
                typeof(TCommand).FullName,
                typeof(TTarget).FullName,
                target.TargetId);

            if (!observedCompleted)
                await PublishTimeoutSignalBestEffortAsync(target, receipt, observedCompletion);
        }
        finally
        {
            if (Interlocked.Decrement(ref _inflightCount) == 0)
                _drainComplete.TrySetResult();
        }
    }

    private async Task PublishTimeoutSignalBestEffortAsync(
        TTarget target,
        TReceipt receipt,
        TCompletion completion)
    {
        try
        {
            await target.PublishDetachedCommandSignalAsync(
                new DetachedCommandTimeout<TReceipt, TCompletion>(receipt, completion),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Detached command timeout signal publish failed. command={CommandType}, target={TargetType}, targetId={TargetId}",
                typeof(TCommand).FullName,
                typeof(TTarget).FullName,
                target.TargetId);
        }
    }
}
