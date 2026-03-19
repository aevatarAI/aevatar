using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>
    : ICommandDispatchService<TCommand, TReceipt, TError>
    where TTarget : class, ICommandEventTarget<TEvent>, ICommandInteractionCleanupTarget<TReceipt, TCompletion>
{
    private readonly ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> _dispatchPipeline;
    private readonly IEventOutputStream<TEvent, TFrame> _outputStream;
    private readonly ICommandCompletionPolicy<TEvent, TCompletion> _completionPolicy;
    private readonly ICommandDurableCompletionResolver<TReceipt, TCompletion> _durableCompletionResolver;
    private readonly ILogger<DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>> _logger;

    public DefaultDetachedCommandDispatchService(
        ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> dispatchPipeline,
        IEventOutputStream<TEvent, TFrame> outputStream,
        ICommandCompletionPolicy<TEvent, TCompletion> completionPolicy,
        ICommandDurableCompletionResolver<TReceipt, TCompletion> durableCompletionResolver,
        ILogger<DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>>? logger = null)
    {
        _dispatchPipeline = dispatchPipeline ?? throw new ArgumentNullException(nameof(dispatchPipeline));
        _outputStream = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
        _completionPolicy = completionPolicy ?? throw new ArgumentNullException(nameof(completionPolicy));
        _durableCompletionResolver = durableCompletionResolver ?? throw new ArgumentNullException(nameof(durableCompletionResolver));
        _logger = logger ?? NullLogger<DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>>.Instance;
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

    private void StartDetachedDrain(
        TTarget target,
        TReceipt receipt)
    {
        _ = Task.Run(
            async () =>
            {
                var observedCompleted = false;
                var observedCompletion = _completionPolicy.IncompleteCompletion;
                var durableCompletion = CommandDurableCompletionObservation<TCompletion>.Incomplete;

                try
                {
                    await _outputStream.PumpAsync(
                        target.RequireLiveSink().ReadAllAsync(CancellationToken.None),
                        static (_, _) => ValueTask.CompletedTask,
                        evt =>
                        {
                            if (!_completionPolicy.TryResolve(evt, out var completion))
                                return false;

                            observedCompleted = true;
                            observedCompletion = completion;
                            return true;
                        },
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Detached command monitoring failed. command={CommandType}, target={TargetType}, targetId={TargetId}",
                        typeof(TCommand).FullName,
                        typeof(TTarget).FullName,
                        target.TargetId);
                }
                finally
                {
                    if (!observedCompleted)
                    {
                        try
                        {
                            durableCompletion = await _durableCompletionResolver.ResolveAsync(
                                receipt,
                                CancellationToken.None);
                            if (durableCompletion.HasTerminalCompletion)
                            {
                                observedCompleted = true;
                                observedCompletion = durableCompletion.Completion;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "Detached command durable completion resolve failed. command={CommandType}, target={TargetType}, targetId={TargetId}",
                                typeof(TCommand).FullName,
                                typeof(TTarget).FullName,
                                target.TargetId);
                        }
                    }

                    try
                    {
                        await target.ReleaseAfterInteractionAsync(
                            receipt,
                            new CommandInteractionCleanupContext<TCompletion>(
                                observedCompleted,
                                observedCompletion,
                                durableCompletion),
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Detached command cleanup failed. command={CommandType}, target={TargetType}, targetId={TargetId}",
                            typeof(TCommand).FullName,
                            typeof(TTarget).FullName,
                            target.TargetId);
                    }
                }
            },
            CancellationToken.None);
    }
}
