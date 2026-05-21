using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultCommandDispatchPipeline<TCommand, TTarget, TReceipt, TError>
    : ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    private readonly ICommandTargetResolver<TCommand, TTarget, TError> _targetResolver;
    private readonly ICommandContextPolicy _contextPolicy;
    private readonly ICommandTargetBinder<TCommand, TTarget, TError> _targetBinder;
    private readonly ICommandEnvelopeFactory<TCommand> _envelopeFactory;
    private readonly ICommandTargetDispatcher<TTarget> _targetDispatcher;
    private readonly ICommandReceiptFactory<TTarget, TReceipt> _receiptFactory;

    public DefaultCommandDispatchPipeline(
        ICommandTargetResolver<TCommand, TTarget, TError> targetResolver,
        ICommandContextPolicy contextPolicy,
        ICommandTargetBinder<TCommand, TTarget, TError> targetBinder,
        ICommandEnvelopeFactory<TCommand> envelopeFactory,
        ICommandTargetDispatcher<TTarget> targetDispatcher,
        ICommandReceiptFactory<TTarget, TReceipt> receiptFactory)
    {
        _targetResolver = targetResolver;
        _contextPolicy = contextPolicy;
        _targetBinder = targetBinder;
        _envelopeFactory = envelopeFactory;
        _targetDispatcher = targetDispatcher;
        _receiptFactory = receiptFactory;
    }

    public async Task<CommandTargetResolution<CommandDispatchExecution<TTarget, TReceipt>, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: DefaultCommandDispatchPipeline.PrepareAsync 内 attach projection/session binder(混 read-side 关注到 pre-dispatch command 准备)
        //   New principle: 新 CQRS Core ObservationLifecycle port/phase:streaming observation attachment 移到 post-accepted dispatch 之后或独立 lifecycle;PrepareAsync 不再持有 projection/session 关注
        var prepared = await PrepareAsync(command, ct);
        if (!prepared.Succeeded || prepared.Target == null)
            return prepared;

        var execution = prepared.Target;
        await DispatchPreparedAsync(execution, ct);

        var binding = await _targetBinder.BindAsync(command, execution.Target, execution.Context, ct);
        if (!binding.Succeeded)
            return CommandTargetResolution<CommandDispatchExecution<TTarget, TReceipt>, TError>.Failure(binding.Error);

        return CommandTargetResolution<CommandDispatchExecution<TTarget, TReceipt>, TError>.Success(
            execution with { Receipt = _receiptFactory.Create(execution.Target, execution.Context) });
    }

    public async Task<CommandTargetResolution<CommandDispatchExecution<TTarget, TReceipt>, TError>> PrepareAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: DefaultCommandDispatchPipeline.PrepareAsync 内 attach projection/session binder(混 read-side 关注到 pre-dispatch command 准备)
        //   New principle: 新 CQRS Core ObservationLifecycle port/phase:streaming observation attachment 移到 post-accepted dispatch 之后或独立 lifecycle;PrepareAsync 不再持有 projection/session 关注
        var resolution = await _targetResolver.ResolveAsync(command, ct);
        if (!resolution.Succeeded || resolution.Target == null)
            return CommandTargetResolution<CommandDispatchExecution<TTarget, TReceipt>, TError>.Failure(resolution.Error);

        var target = resolution.Target;
        var seed = command is ICommandContextSeed contextSeed
            ? contextSeed
            : null;
        var context = _contextPolicy.Create(
            target.TargetId,
            seed?.Headers,
            seed?.CommandId,
            seed?.CorrelationId);
        var envelope = _envelopeFactory.CreateEnvelope(command, context);
        var receipt = _receiptFactory.Create(target, context);
        return CommandTargetResolution<CommandDispatchExecution<TTarget, TReceipt>, TError>.Success(
            new CommandDispatchExecution<TTarget, TReceipt>
            {
                Target = target,
                Context = context,
                Envelope = envelope,
                Receipt = receipt,
            });
    }

    public async Task DispatchPreparedAsync(
        CommandDispatchExecution<TTarget, TReceipt> execution,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: DefaultCommandDispatchPipeline.PrepareAsync 内 attach projection/session binder(混 read-side 关注到 pre-dispatch command 准备)
        //   New principle: 新 CQRS Core ObservationLifecycle port/phase:streaming observation attachment 移到 post-accepted dispatch 之后或独立 lifecycle;PrepareAsync 不再持有 projection/session 关注
        ArgumentNullException.ThrowIfNull(execution);
        var target = execution.Target;

        try
        {
            await _targetDispatcher.DispatchAsync(target, execution.Envelope, ct);
        }
        catch
        {
            if (target is ICommandDispatchCleanupAware cleanupAware)
                await cleanupAware.CleanupAfterDispatchFailureAsync(CancellationToken.None);

            throw;
        }
    }
}
