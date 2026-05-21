namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandTargetBinder<in TCommand, in TTarget, TError>
    where TTarget : class, ICommandDispatchTarget
{
    // Refactor (iter25/cluster-002-observation-lifecycle-core):
    //   Old pattern: command preparation could attach projection/session leases and mix read-side observation into dispatch admission.
    //   New principle: live observation is an explicit interaction phase that starts before dispatch; PrepareAsync and dispatch-only callers stay free of read-side lifecycle work
    Task<CommandTargetBindingResult<TError>> BindAsync(
        TCommand command,
        TTarget target,
        CommandContext context,
        CancellationToken ct = default);
}
