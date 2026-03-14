namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandTargetBinder<in TCommand, in TTarget, TError>
    where TTarget : class, ICommandDispatchTarget
{
    Task<CommandTargetBindingResult<TError>> BindAsync(
        TCommand command,
        TTarget target,
        CommandContext context,
        CancellationToken ct = default);
}
