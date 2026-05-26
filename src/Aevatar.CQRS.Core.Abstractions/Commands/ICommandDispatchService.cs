namespace Aevatar.CQRS.Core.Abstractions.Commands;

using Google.Protobuf;

public interface ICommandDispatchService<in TCommand, TReceipt, TError>
{
    Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default);
}

public interface ICommandOutcomeDispatchService<in TCommand, TReceipt, TError, TOutcome>
    where TOutcome : IMessage, new()
{
    Task<CommandOutcomeDispatchResult<TReceipt, TError, TOutcome>> DispatchAndAwaitOutcomeAsync(
        TCommand command,
        CancellationToken ct = default);
}
