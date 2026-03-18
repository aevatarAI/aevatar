using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError>
    : ICommandDispatchService<TCommand, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    private readonly ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> _dispatchPipeline;

    public DefaultDetachedCommandDispatchService(
        ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> dispatchPipeline)
    {
        _dispatchPipeline = dispatchPipeline ?? throw new ArgumentNullException(nameof(dispatchPipeline));
    }

    public async Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var dispatch = await _dispatchPipeline.DispatchAsync(command, ct);
        if (!dispatch.Succeeded || dispatch.Target == null)
            return CommandDispatchResult<TReceipt, TError>.Failure(dispatch.Error);

        return CommandDispatchResult<TReceipt, TError>.Success(dispatch.Target.Receipt);
    }
}
