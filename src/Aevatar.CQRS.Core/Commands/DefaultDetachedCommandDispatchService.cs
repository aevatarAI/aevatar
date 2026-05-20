using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>
    : ICommandDispatchService<TCommand, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    private readonly ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> _dispatchPipeline;

    public DefaultDetachedCommandDispatchService(
        ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> dispatchPipeline,
        IEventOutputStream<TEvent, TFrame> outputStream,
        ICommandCompletionPolicy<TEvent, TCompletion> completionPolicy,
        ICommandDispatchShutdownSignal? shutdownSignal = null,
        ILogger<DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>>? logger = null)
    {
        _dispatchPipeline = dispatchPipeline ?? throw new ArgumentNullException(nameof(dispatchPipeline));
        ArgumentNullException.ThrowIfNull(outputStream);
        ArgumentNullException.ThrowIfNull(completionPolicy);
        _ = shutdownSignal;
        _ = logger ?? NullLogger<DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>>.Instance;
    }

    public async Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        // Refactor (iter18/cluster-005):
        //   Old pattern: DefaultDetachedCommandDispatchService 在 accepted-only path 持有 live sink
        //   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
        ArgumentNullException.ThrowIfNull(command);

        var dispatch = await _dispatchPipeline.DispatchAsync(command, ct);
        if (!dispatch.Succeeded || dispatch.Target == null)
            return CommandDispatchResult<TReceipt, TError>.Failure(dispatch.Error);

        return CommandDispatchResult<TReceipt, TError>.Success(dispatch.Target.Receipt);
    }
}
