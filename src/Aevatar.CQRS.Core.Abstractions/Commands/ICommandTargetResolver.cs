namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandTargetResolver<in TCommand, TTarget, TError>
    where TTarget : class, ICommandDispatchTarget
{
    Task<CommandTargetResolution<TTarget, TError>> ResolveAsync(
        TCommand command,
        CancellationToken ct = default);
}
