using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultCommandDispatchService<TCommand, TTarget, TReceipt, TError>
    : ICommandDispatchService<TCommand, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    private readonly ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> _pipeline;

    public DefaultCommandDispatchService(
        ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> pipeline)
    {
        _pipeline = pipeline;
    }

    public async Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        var dispatch = await _pipeline.DispatchAsync(command, ct);
        if (!dispatch.Succeeded || dispatch.Target == null)
            return CommandDispatchResult<TReceipt, TError>.Failure(dispatch.Error);

        return CommandDispatchResult<TReceipt, TError>.Success(dispatch.Target.Receipt);
    }
}
