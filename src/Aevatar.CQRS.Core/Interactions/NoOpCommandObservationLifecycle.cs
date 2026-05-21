using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;

namespace Aevatar.CQRS.Core.Interactions;

public sealed class NoOpCommandObservationLifecycle<TCommand, TTarget, TReceipt, TError>
    : ICommandObservationLifecycle<TCommand, TTarget, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    public Task<CommandObservationBindingResult<TError>> BindAsync(
        TCommand command,
        CommandDispatchExecution<TTarget, TReceipt> execution,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: every command path depended on binder timing even when no live observation existed.
        //   New principle: dispatch-only commands use an explicit no-op observation phase.
        _ = command;
        ArgumentNullException.ThrowIfNull(execution);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CommandObservationBindingResult<TError>.Success());
    }
}
