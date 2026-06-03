using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Core.Interactions;

public sealed class DefaultCommandInteractionService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>
    : ICommandInteractionService<TCommand, TReceipt, TError, TFrame, TCompletion>
    where TTarget : class, ICommandEventTarget<TEvent>, ICommandInteractionCleanupTarget<TReceipt, TCompletion>
{
    private readonly ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> _dispatchPipeline;
    private readonly ICommandObservationLifecycle<TCommand, TTarget, TReceipt, TError> _observationLifecycle;
    private readonly ICommandReceiptFactory<TTarget, TReceipt>? _receiptFactory;
    private readonly IEventOutputStream<TEvent, TFrame> _outputStream;
    private readonly ICommandCompletionPolicy<TEvent, TCompletion> _completionPolicy;
    private readonly ICommandFinalizeEmitter<TReceipt, TCompletion, TFrame> _finalizeEmitter;
    private readonly ICommandDurableCompletionResolver<TReceipt, TCompletion> _durableCompletionResolver;
    private readonly ILogger<DefaultCommandInteractionService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>> _logger;

    public DefaultCommandInteractionService(
        ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> dispatchPipeline,
        IEventOutputStream<TEvent, TFrame> outputStream,
        ICommandCompletionPolicy<TEvent, TCompletion> completionPolicy,
        ICommandFinalizeEmitter<TReceipt, TCompletion, TFrame> finalizeEmitter,
        ICommandDurableCompletionResolver<TReceipt, TCompletion> durableCompletionResolver,
        ILogger<DefaultCommandInteractionService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>>? logger = null,
        ICommandObservationLifecycle<TCommand, TTarget, TReceipt, TError>? observationLifecycle = null,
        ICommandReceiptFactory<TTarget, TReceipt>? receiptFactory = null)
    {
        _dispatchPipeline = dispatchPipeline ?? throw new ArgumentNullException(nameof(dispatchPipeline));
        _observationLifecycle = observationLifecycle ?? new NoOpCommandObservationLifecycle<TCommand, TTarget, TReceipt, TError>();
        _receiptFactory = receiptFactory;
        _outputStream = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
        _completionPolicy = completionPolicy ?? throw new ArgumentNullException(nameof(completionPolicy));
        _finalizeEmitter = finalizeEmitter ?? throw new ArgumentNullException(nameof(finalizeEmitter));
        _durableCompletionResolver = durableCompletionResolver ?? throw new ArgumentNullException(nameof(durableCompletionResolver));
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

        var observation = await _observationLifecycle.BindAsync(command, execution, ct);
        if (!observation.Succeeded)
            return CommandInteractionResult<TReceipt, TError, TCompletion>.Failure(observation.Error);

        _ = await _dispatchPipeline.DispatchPreparedAsync(execution, ct);

        var receipt = _receiptFactory == null
            ? execution.Receipt
            : _receiptFactory.Create(target, execution.Context);
        var observedCompleted = false;
        var observedCompletion = _completionPolicy.IncompleteCompletion;
        var durableCompletion = CommandDurableCompletionObservation<TCompletion>.Incomplete;
        var durableCompletionAttempted = false;
        CommandInteractionResult<TReceipt, TError, TCompletion>? interactionResult = null;
        Exception? executionException = null;

        try
        {
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, ct);

            await _outputStream.PumpAsync(
                target.RequireLiveSink().ReadAllAsync(ct),
                emitAsync,
                evt =>
                {
                    if (!_completionPolicy.TryResolve(evt, out var completion))
                        return false;

                    observedCompleted = true;
                    observedCompletion = completion;
                    return true;
                },
                ct);

            if (!observedCompleted)
            {
                durableCompletionAttempted = true;
                durableCompletion = await _durableCompletionResolver.ResolveAsync(receipt, ct);
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
                ct);

            interactionResult = CommandInteractionResult<TReceipt, TError, TCompletion>.Success(
                receipt,
                new CommandInteractionFinalizeResult<TCompletion>(observedCompletion, observedCompleted));
            return interactionResult;
        }
        catch (Exception ex)
        {
            executionException = ex;
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
                        CancellationToken.None);
                }

                await target.ReleaseAfterInteractionAsync(
                    receipt,
                    new CommandInteractionCleanupContext<TCompletion>(
                        observedCompleted,
                        observedCompletion,
                        durableCompletion),
                    CancellationToken.None);
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

    async Task<RealtimeSessionResult<TReceipt, TError, TCompletion>> IRealtimeSession<TCommand, TReceipt, TError, TFrame, TCompletion>.ExecuteAsync(
        TCommand inbound,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        Func<TReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
        CancellationToken ct)
    {
        return await ExecuteAsync(inbound, emitAsync, onAcceptedAsync, ct);
    }
}
