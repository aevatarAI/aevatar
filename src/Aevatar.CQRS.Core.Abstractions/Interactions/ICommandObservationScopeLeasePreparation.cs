using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Abstractions.Interactions;

// Refactor (issue-1687):
//   Old pattern: command-specific application services prepared observation scope leases before dispatch.
//   New principle: command interaction owns one explicit observation-scope preparation seam.
//   This seam prepares projection-owned observation scope leases for one interaction attempt; it is not query priming or a read-model freshness guarantee.
public interface ICommandObservationScopeLeasePreparation<TCommand, TTarget, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    Task<CommandObservationScopeLeasePreparationResult<TError>> PrepareAsync(
        TCommand command,
        CommandDispatchExecution<TTarget, TReceipt> execution,
        CancellationToken ct = default);
}
