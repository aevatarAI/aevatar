using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;

namespace Aevatar.CQRS.Core.Interactions;

internal sealed class NoOpCommandObservationScopeLeasePreparation<TCommand, TTarget, TReceipt, TError>
    : ICommandObservationScopeLeasePreparation<TCommand, TTarget, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    public Task<CommandObservationScopeLeasePreparationResult<TError>> PrepareAsync(
        TCommand command,
        CommandDispatchExecution<TTarget, TReceipt> execution,
        CancellationToken ct = default)
    {
        _ = command;
        _ = execution;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CommandObservationScopeLeasePreparationResult<TError>.Success());
    }
}
