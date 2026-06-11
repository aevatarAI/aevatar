using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Abstractions.Interactions;

public interface ICommandObservationLifecycle<TCommand, TTarget, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    // Refactor (iter25/cluster-002-observation-lifecycle-core):
    //   Old pattern: command target binders could attach projection/session leases inside command preparation.
    //   New principle: live observation is an explicit interaction phase that starts before dispatch and stays separate from dispatch-only command admission.
    Task<CommandObservationBindingResult<TError>> BindAsync(
        TCommand command,
        CommandDispatchExecution<TTarget, TReceipt> execution,
        CancellationToken ct = default);
}
