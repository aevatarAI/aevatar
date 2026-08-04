using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;

namespace Aevatar.CQRS.Core.Interactions;

public sealed class DefaultCommandInteractionService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>
    : ICommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion>
    where TTarget : class, ICommandEventTarget<TEvent>, ICommandInteractionCleanupTarget<TReceipt, TCompletion>
{
    private readonly ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> _dispatchPipeline;
    private readonly ICommandObservationScopeLeasePreparation<TCommand, TTarget, TReceipt, TError> _observationScopePreparation;
    private readonly ICommandObservationLifecycle<TCommand, TTarget, TReceipt, TError> _observationLifecycle;
    private readonly ICommandReceiptFactory<TTarget, TReceipt>? _receiptFactory;
    private readonly IEventOutputStream<TEvent, TFrame> _outputStream;
    private readonly ICommandCompletionPolicy<TEvent, TCompletion> _completionPolicy;
    private readonly ICommandFinalizeEmitter<TReceipt, TCompletion, TFrame> _finalizeEmitter;
    private readonly ICommandDurableCompletionResolver<TReceipt, TCompletion> _durableCompletionResolver;
    private readonly bool _probeDurableCompletionWhileLive;
    private readonly ILogger<DefaultCommandInteractionService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>> _logger;

    public DefaultCommandInteractionService(
        ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> dispatchPipeline,
        IEventOutputStream<TEvent, TFrame> outputStream,
        ICommandCompletionPolicy<TEvent, TCompletion> completionPolicy,
        ICommandFinalizeEmitter<TReceipt, TCompletion, TFrame> finalizeEmitter,
        ICommandDurableCompletionResolver<TReceipt, TCompletion> durableCompletionResolver,
        ILogger<DefaultCommandInteractionService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>>? logger = null,
        ICommandObservationLifecycle<TCommand, TTarget, TReceipt, TError>? observationLifecycle = null,
        ICommandReceiptFactory<TTarget, TReceipt>? receiptFactory = null,
        ICommandObservationScopeLeasePreparation<TCommand, TTarget, TReceipt, TError>? observationScopePreparation = null,
        bool probeDurableCompletionWhileLive = false)
    {
        _dispatchPipeline = dispatchPipeline ?? throw new ArgumentNullException(nameof(dispatchPipeline));
        _observationScopePreparation = observationScopePreparation
            ?? new NoOpCommandObservationScopeLeasePreparation<TCommand, TTarget, TReceipt, TError>();
        _observationLifecycle = observationLifecycle ?? new NoOpCommandObservationLifecycle<TCommand, TTarget, TReceipt, TError>();
        _receiptFactory = receiptFactory;
        _outputStream = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
        _completionPolicy = completionPolicy ?? throw new ArgumentNullException(nameof(completionPolicy));
        _finalizeEmitter = finalizeEmitter ?? throw new ArgumentNullException(nameof(finalizeEmitter));
        _durableCompletionResolver = durableCompletionResolver ?? throw new ArgumentNullException(nameof(durableCompletionResolver));
        _probeDurableCompletionWhileLive = probeDurableCompletionWhileLive;
        _logger = logger ?? NullLogger<DefaultCommandInteractionService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>>.Instance;
    }

    public async Task<CommandInteractionResult<TReceipt, TError, TCompletion>> ExecuteAsync(
        TCommand command,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        Func<TReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(emitAsync);

        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: interaction execution called DispatchAsync and projection/live-sink binding could fail after the command had entered the actor inbox.
        //   New principle: interaction observation starts before dispatch; failure prevents dispatch, and accepted is emitted only after mailbox admission.
        var prepared = await _dispatchPipeline.PrepareAsync(command, ct);
        if (!prepared.Succeeded || prepared.Target == null)
            return CommandInteractionResult<TReceipt, TError, TCompletion>.Failure(prepared.Error);

        var execution = prepared.Target;
        var target = execution.Target;
        var accepted = false;
        var interactionCleanupStarted = false;
        ICommandObservationScopeLeasePreparationHandle? preparedObservationScope = null;

        try
        {
            var preparation = await _observationScopePreparation.PrepareAsync(command, execution, ct);
            if (!preparation.Succeeded)
                return CommandInteractionResult<TReceipt, TError, TCompletion>.Failure(preparation.Error);

            preparedObservationScope = preparation.Handle;

            var observation = await _observationLifecycle.BindAsync(command, execution, ct);
            if (!observation.Succeeded)
            {
                var scopeToRelease = preparedObservationScope;
                preparedObservationScope = null;
                await ReleasePreparedObservationScopeAsync(scopeToRelease).ConfigureAwait(false);
                return CommandInteractionResult<TReceipt, TError, TCompletion>.Failure(observation.Error);
            }

            var receipt = _receiptFactory == null
                ? execution.Receipt
                : _receiptFactory.Create(target, execution.Context);
            var observedCompleted = false;
            var observedCompletion = _completionPolicy.IncompleteCompletion;
            var durableCompletion = CommandDurableCompletionObservation<TCompletion>.Incomplete;
            var durableCompletionAttempted = false;
            Exception? executionException = null;
            using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var bufferedFrames = Channel.CreateUnbounded<TFrame>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

            async ValueTask BufferFrameAsync(TFrame frame, CancellationToken frameCt)
            {
                await bufferedFrames.Writer.WriteAsync(frame, frameCt).ConfigureAwait(false);
            }

            var liveEvents = ObserveCompletionBeforeEmissionAsync(
                target.RequireLiveSink().ReadAllAsync(pumpCancellation.Token),
                evt =>
                {
                    if (!_completionPolicy.TryResolve(evt, out var completion))
                        return false;

                    observedCompleted = true;
                    observedCompletion = completion;
                    return true;
                },
                pumpCancellation.Token);
            var pumpTask = _outputStream.PumpAsync(
                liveEvents,
                BufferFrameAsync,
                shouldStop: null,
                pumpCancellation.Token);

            if (_probeDurableCompletionWhileLive)
            {
                try
                {
                    durableCompletion = await _durableCompletionResolver.ResolveAsync(receipt, ct).ConfigureAwait(false);
                    durableCompletionAttempted = durableCompletion.HasTerminalCompletion;
                    if (durableCompletion.HasTerminalCompletion)
                    {
                        observedCompleted = true;
                        observedCompletion = durableCompletion.Completion;
                        await CancelAndObservePumpAsync(
                            pumpTask,
                            pumpCancellation,
                            expectedCancellation: true).ConfigureAwait(false);
                        bufferedFrames.Writer.TryComplete();
                    }
                }
                catch (Exception ex)
                {
                    executionException = ex;
                    interactionCleanupStarted = true;
                    await CancelAndObservePumpAsync(
                        pumpTask,
                        pumpCancellation).ConfigureAwait(false);
                    try
                    {
                        await target.ReleaseAfterInteractionAsync(
                            receipt,
                            new CommandInteractionCleanupContext<TCompletion>(
                                false,
                                _completionPolicy.IncompleteCompletion,
                                CommandDurableCompletionObservation<TCompletion>.Incomplete),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception cleanupException)
                    {
                        _logger.LogWarning(
                            cleanupException,
                            "Command interaction cleanup failed after durable completion probe failure. command={CommandType}, target={TargetType}",
                            typeof(TCommand).FullName,
                            typeof(TTarget).FullName);
                    }
                    throw;
                }
            }

            try
            {
                if (!durableCompletion.HasTerminalCompletion)
                {
                    _ = await _dispatchPipeline.DispatchPreparedAsync(execution, ct).ConfigureAwait(false);
                    accepted = true;
                }
            }
            catch
            {
                await CancelAndObservePumpAsync(pumpTask, pumpCancellation).ConfigureAwait(false);
                throw;
            }

            try
            {
                interactionCleanupStarted = true;
                if (onAcceptedAsync != null)
                    await onAcceptedAsync(receipt, ct).ConfigureAwait(false);

                if (!durableCompletion.HasTerminalCompletion)
                {
                    await PumpAndFlushAcceptedFramesAsync(
                        pumpTask,
                        pumpCancellation,
                        bufferedFrames,
                        emitAsync,
                        ct).ConfigureAwait(false);
                }

                if (!observedCompleted && !durableCompletionAttempted)
                {
                    durableCompletionAttempted = true;
                    durableCompletion = await _durableCompletionResolver.ResolveAsync(receipt, ct).ConfigureAwait(false);
                    if (durableCompletion.HasTerminalCompletion)
                    {
                        observedCompleted = true;
                        observedCompletion = durableCompletion.Completion;
                    }
                }

                await _finalizeEmitter.EmitAsync(
                    receipt,
                    observedCompletion,
                    observedCompleted,
                    emitAsync,
                    ct).ConfigureAwait(false);

                return CreateSuccessResult(receipt, observedCompletion, observedCompleted);
            }
            catch (Exception ex)
            {
                executionException = ex;
                bufferedFrames.Writer.TryComplete(ex);
                await CancelAndObservePumpAsync(pumpTask, pumpCancellation).ConfigureAwait(false);
                throw;
            }
            finally
            {
                try
                {
                    if (!observedCompleted && !durableCompletionAttempted)
                    {
                        durableCompletion = await _durableCompletionResolver.ResolveAsync(
                            receipt,
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    await target.ReleaseAfterInteractionAsync(
                        receipt,
                        new CommandInteractionCleanupContext<TCompletion>(
                            observedCompleted,
                            observedCompletion,
                            durableCompletion),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    if (executionException != null)
                    {
                        _logger.LogWarning(
                            cleanupException,
                            "Command interaction cleanup failed after execution failure. command={CommandType}, target={TargetType}",
                            typeof(TCommand).FullName,
                            typeof(TTarget).FullName);
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }
        catch
        {
            if (!accepted && !interactionCleanupStarted)
                await ReleasePreparedObservationScopeAsync(preparedObservationScope).ConfigureAwait(false);

            throw;
        }
    }

    private static CommandInteractionResult<TReceipt, TError, TCompletion> CreateSuccessResult(
        TReceipt receipt,
        TCompletion completion,
        bool completed) =>
        CommandInteractionResult<TReceipt, TError, TCompletion>.Success(
            receipt,
            new CommandInteractionFinalizeResult<TCompletion>(completion, completed));

    private static async IAsyncEnumerable<TEvent> ObserveCompletionBeforeEmissionAsync(
        IAsyncEnumerable<TEvent> events,
        Func<TEvent, bool> shouldStop,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in events.WithCancellation(ct).ConfigureAwait(false))
        {
            var stop = shouldStop(evt);
            yield return evt;

            if (stop)
                yield break;
        }
    }

    private async Task PumpAndFlushAcceptedFramesAsync(
        Task pumpTask,
        CancellationTokenSource pumpCancellation,
        Channel<TFrame> frames,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct)
    {
        var flushTask = FlushBufferedFramesAsync(frames.Reader, emitAsync, ct);

        try
        {
            await pumpTask.ConfigureAwait(false);
            frames.Writer.TryComplete();
            await flushTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            frames.Writer.TryComplete(ex);
            await CancelAndObservePumpAsync(pumpTask, pumpCancellation).ConfigureAwait(false);
            await ObserveFlushFailureAsync(flushTask).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task FlushBufferedFramesAsync(
        ChannelReader<TFrame> frames,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct)
    {
        await foreach (var frame in frames.ReadAllAsync(ct).ConfigureAwait(false))
            await emitAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task ObserveFlushFailureAsync(Task flushTask)
    {
        try
        {
            await flushTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Observed buffered-frame flush failure after command interaction pump failure. command={CommandType}, target={TargetType}",
                typeof(TCommand).FullName,
                typeof(TTarget).FullName);
        }
    }

    private async Task CancelAndObservePumpAsync(
        Task pumpTask,
        CancellationTokenSource pumpCancellation,
        bool expectedCancellation = false)
    {
        await pumpCancellation.CancelAsync().ConfigureAwait(false);

        try
        {
            await pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expectedCancellation)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Observed command interaction pump completion after cancellation. command={CommandType}, target={TargetType}",
                typeof(TCommand).FullName,
                typeof(TTarget).FullName);
        }
    }

    private static async Task ReleasePreparedObservationScopeAsync(
        ICommandObservationScopeLeasePreparationHandle? preparedObservationScope)
    {
        if (preparedObservationScope == null)
            return;

        await preparedObservationScope.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
    }

    async Task<RealtimeSessionResult<TReceipt, TError, TCompletion>> IRealtimeSession<TCommand, TReceipt, TError, TFrame, TCompletion>.ExecuteAsync(
        TCommand inbound,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        Func<TReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
        CancellationToken ct)
    {
        return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
    }
}
