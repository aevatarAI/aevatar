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
        ILogger<DefaultCommandInteractionService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>>? logger = null)
    {
        _dispatchPipeline = dispatchPipeline ?? throw new ArgumentNullException(nameof(dispatchPipeline));
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

        var dispatch = await _dispatchPipeline.DispatchAsync(command, ct);
        if (!dispatch.Succeeded || dispatch.Target == null)
            return CommandInteractionResult<TReceipt, TError, TCompletion>.Failure(dispatch.Error);

        var execution = dispatch.Target;
        var target = execution.Target;
        var receipt = execution.Receipt;
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
}
