namespace Aevatar.CQRS.Core.Abstractions.Commands;

// Refactor (iter158/cluster-001-stream-actor-outcome-rpc):
// Old: outcome dispatch used stream subscribe + TCS to treat the first outcome as an RPC reply, violating stream request-reply boundaries and honest ACK semantics.
// New: Delete the outcome-dispatch contract with no replacement; callers use accepted receipts plus readmodel queries or typed continuation events.
public interface ICommandDispatchService<in TCommand, TReceipt, TError>
{
    Task<CommandDispatchResult<TReceipt, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default);
}
