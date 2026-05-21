namespace Aevatar.CQRS.Core.Abstractions.Commands;

public interface ICommandTargetBinder<in TCommand, in TTarget, TError>
    where TTarget : class, ICommandDispatchTarget
{
    // Refactor (iter25/cluster-002-observation-lifecycle-core):
    //   Old pattern: DefaultCommandDispatchPipeline.PrepareAsync 内 attach projection/session binder(混 read-side 关注到 pre-dispatch command 准备)
    //   New principle: 新 CQRS Core ObservationLifecycle port/phase:streaming observation attachment 移到 post-accepted dispatch 之后或独立 lifecycle;PrepareAsync 不再持有 projection/session 关注
    Task<CommandTargetBindingResult<TError>> BindAsync(
        TCommand command,
        TTarget target,
        CommandContext context,
        CancellationToken ct = default);
}
