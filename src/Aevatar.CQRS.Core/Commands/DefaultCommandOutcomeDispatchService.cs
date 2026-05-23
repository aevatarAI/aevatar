using Aevatar.CQRS.Core.Abstractions.Commands;
using Google.Protobuf;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultCommandOutcomeDispatchService<TCommand, TTarget, TReceipt, TError, TOutcome>
    : ICommandOutcomeDispatchService<TCommand, TReceipt, TError, TOutcome>
    where TTarget : class, ICommandDispatchTarget
    where TOutcome : IMessage, new()
{
    private readonly ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> _pipeline;
    private readonly IActorOutcomeChannel<TOutcome> _outcomeChannel;

    public DefaultCommandOutcomeDispatchService(
        ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError> pipeline,
        IActorOutcomeChannel<TOutcome> outcomeChannel)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _outcomeChannel = outcomeChannel ?? throw new ArgumentNullException(nameof(outcomeChannel));
    }

    public async Task<CommandOutcomeDispatchResult<TReceipt, TError, TOutcome>> DispatchAndAwaitOutcomeAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        var prepared = await _pipeline.PrepareAsync(command, ct);
        if (!prepared.Succeeded || prepared.Target is null)
            return CommandOutcomeDispatchResult<TReceipt, TError, TOutcome>.Failure(prepared.Error);

        await using var subscription = await _outcomeChannel.SubscribeAsync(
            prepared.Target.Context.CommandId,
            ct);
        await _pipeline.DispatchPreparedAsync(prepared.Target, ct);
        var outcome = await subscription.Outcome.WaitAsync(ct);
        return CommandOutcomeDispatchResult<TReceipt, TError, TOutcome>.Success(
            prepared.Target.Receipt,
            outcome);
    }
}
