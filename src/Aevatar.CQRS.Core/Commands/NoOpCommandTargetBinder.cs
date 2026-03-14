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
        _ = command;
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CommandTargetBindingResult<TError>.Success());
    }
}
