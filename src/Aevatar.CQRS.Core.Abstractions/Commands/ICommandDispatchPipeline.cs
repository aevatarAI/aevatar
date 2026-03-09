namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandDispatchPipeline<in TCommand, TTarget, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    Task<CommandTargetResolution<CommandDispatchExecution<TTarget, TReceipt>, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default);
}
