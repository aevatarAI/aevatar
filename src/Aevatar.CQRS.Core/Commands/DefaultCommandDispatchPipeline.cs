using Aevatar.CQRS.Core.Abstractions.Commands;

namespace Aevatar.CQRS.Core.Commands;

public sealed class DefaultCommandDispatchPipeline<TCommand, TTarget, TReceipt, TError>
    : ICommandDispatchPipeline<TCommand, TTarget, TReceipt, TError>
    where TTarget : class, ICommandDispatchTarget
{
    private readonly ICommandTargetResolver<TCommand, TTarget, TError> _targetResolver;
    private readonly ICommandContextPolicy _contextPolicy;
    private readonly ICommandEnvelopeFactory<TCommand>? _envelopeFactory;
    private readonly ICommandTargetEnvelopeFactory<TCommand, TTarget>? _targetEnvelopeFactory;
    private readonly ICommandTargetDispatcher<TTarget> _targetDispatcher;
    private readonly ICommandReceiptFactory<TTarget, TReceipt> _receiptFactory;

    public DefaultCommandDispatchPipeline(
        ICommandTargetResolver<TCommand, TTarget, TError> targetResolver,
        ICommandContextPolicy contextPolicy,
        ICommandEnvelopeFactory<TCommand> envelopeFactory,
        ICommandTargetDispatcher<TTarget> targetDispatcher,
        ICommandReceiptFactory<TTarget, TReceipt> receiptFactory)
        : this(targetResolver, contextPolicy, envelopeFactory, null, targetDispatcher, receiptFactory)
    {
    }

    public DefaultCommandDispatchPipeline(
        ICommandTargetResolver<TCommand, TTarget, TError> targetResolver,
        ICommandContextPolicy contextPolicy,
        ICommandTargetEnvelopeFactory<TCommand, TTarget> targetEnvelopeFactory,
        ICommandTargetDispatcher<TTarget> targetDispatcher,
        ICommandReceiptFactory<TTarget, TReceipt> receiptFactory)
        : this(targetResolver, contextPolicy, null, targetEnvelopeFactory, targetDispatcher, receiptFactory)
    {
    }

    private DefaultCommandDispatchPipeline(
        ICommandTargetResolver<TCommand, TTarget, TError> targetResolver,
        ICommandContextPolicy contextPolicy,
        ICommandEnvelopeFactory<TCommand>? envelopeFactory,
        ICommandTargetEnvelopeFactory<TCommand, TTarget>? targetEnvelopeFactory,
        ICommandTargetDispatcher<TTarget> targetDispatcher,
        ICommandReceiptFactory<TTarget, TReceipt> receiptFactory)
    {
        _targetResolver = targetResolver;
        _contextPolicy = contextPolicy;
        _envelopeFactory = envelopeFactory;
        _targetEnvelopeFactory = targetEnvelopeFactory;
        _targetDispatcher = targetDispatcher;
        _receiptFactory = receiptFactory;
    }

    public async Task<CommandTargetResolution<CommandDispatchExecution<TTarget, TReceipt>, TError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: command preparation could attach projection/session leases and mix read-side observation into dispatch admission.
        //   New principle: live observation is an explicit interaction phase that starts before dispatch; PrepareAsync and dispatch-only callers stay free of read-side lifecycle work
        var prepared = await PrepareAsync(command, ct);
        if (!prepared.Succeeded || prepared.Target == null)
            return prepared;

        var execution = prepared.Target;
        var admission = await DispatchPreparedAsync(execution, ct);
        return CommandTargetResolution<CommandDispatchExecution<TTarget, TReceipt>, TError>.Success(
            execution with { Admission = admission });
    }

    public async Task<CommandTargetResolution<CommandDispatchExecution<TTarget, TReceipt>, TError>> PrepareAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: command preparation could attach projection/session leases and mix read-side observation into dispatch admission.
        //   New principle: live observation is an explicit interaction phase that starts before dispatch; PrepareAsync and dispatch-only callers stay free of read-side lifecycle work
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
        var envelope = _targetEnvelopeFactory != null
            ? _targetEnvelopeFactory.CreateEnvelope(command, target, context)
            : _envelopeFactory!.CreateEnvelope(command, context);
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

    public async Task<DispatchAdmission> DispatchPreparedAsync(
        CommandDispatchExecution<TTarget, TReceipt> execution,
        CancellationToken ct = default)
    {
        // Refactor (iter25/cluster-002-observation-lifecycle-core):
        //   Old pattern: command preparation could attach projection/session leases and mix read-side observation into dispatch admission.
        //   New principle: live observation is an explicit interaction phase that starts before dispatch; PrepareAsync and dispatch-only callers stay free of read-side lifecycle work
        ArgumentNullException.ThrowIfNull(execution);
        var target = execution.Target;
        var cleanupInvoked = false;

        try
        {
            var admission = await _targetDispatcher.DispatchAsync(target, execution.Envelope, ct);
            if (!admission.Accepted && target is ICommandDispatchCleanupAware rejectedCleanup)
            {
                cleanupInvoked = true;
                await rejectedCleanup.CleanupAfterDispatchFailureAsync(CancellationToken.None);
            }

            return admission;
        }
        catch
        {
            if (!cleanupInvoked && target is ICommandDispatchCleanupAware cleanupAware)
                await cleanupAware.CleanupAfterDispatchFailureAsync(CancellationToken.None);

            throw;
        }
    }
}
