using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class NoOpCommandTargetBinder<TCommand, TTarget, TError>
    : ICommandTargetBinder<TCommand, TTarget, TError>
    where TTarget : class, ICommandDispatchTarget
{
    public Task<CommandTargetBindingResult<TError>> BindAsync(
        TCommand command,
        TTarget target,
        CommandContext context,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: command preparation could attach projection/session leases and mix read-side observation into dispatch admission.
        //   New principle: live observation is an explicit interaction phase that starts before dispatch; PrepareAsync and dispatch-only callers stay free of read-side lifecycle work
        _ = command;
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CommandTargetBindingResult<TError>.Success());
    }
}
