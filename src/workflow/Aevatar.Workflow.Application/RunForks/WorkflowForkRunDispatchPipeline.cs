using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.RunForks;

internal sealed class WorkflowForkRunDispatchPipeline
    : ICommandDispatchPipeline<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>
{
    private readonly ICommandTargetResolver<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunStartError> _targetResolver;
    private readonly ICommandContextPolicy _contextPolicy;
    private readonly ICommandEnvelopeFactory<WorkflowChatRunRequest> _chatEnvelopeFactory;
    private readonly ICommandTargetDispatcher<WorkflowForkRunCommandTarget> _targetDispatcher;
    private readonly ICommandReceiptFactory<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt> _receiptFactory;

    public WorkflowForkRunDispatchPipeline(
        ICommandTargetResolver<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunStartError> targetResolver,
        ICommandContextPolicy contextPolicy,
        ICommandEnvelopeFactory<WorkflowChatRunRequest> chatEnvelopeFactory,
        ICommandTargetDispatcher<WorkflowForkRunCommandTarget> targetDispatcher,
        ICommandReceiptFactory<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt> receiptFactory)
    {
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        _contextPolicy = contextPolicy ?? throw new ArgumentNullException(nameof(contextPolicy));
        _chatEnvelopeFactory = chatEnvelopeFactory ?? throw new ArgumentNullException(nameof(chatEnvelopeFactory));
        _targetDispatcher = targetDispatcher ?? throw new ArgumentNullException(nameof(targetDispatcher));
        _receiptFactory = receiptFactory ?? throw new ArgumentNullException(nameof(receiptFactory));
    }

    public async Task<CommandTargetResolution<CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunStartError>> DispatchAsync(
        WorkflowForkRunCommand command,
        CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(command, ct).ConfigureAwait(false);
        if (!prepared.Succeeded || prepared.Target == null)
            return prepared;

        var execution = prepared.Target;
        var admission = await DispatchPreparedAsync(execution, ct).ConfigureAwait(false);
        return CommandTargetResolution<CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunStartError>.Success(
            execution with { Admission = admission });
    }

    public async Task<CommandTargetResolution<CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunStartError>> PrepareAsync(
        WorkflowForkRunCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var resolution = await _targetResolver.ResolveAsync(command, ct).ConfigureAwait(false);
        if (!resolution.Succeeded || resolution.Target == null)
            return CommandTargetResolution<CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunStartError>.Failure(resolution.Error);

        var target = resolution.Target;
        ICommandContextSeed seed = command;
        var context = _contextPolicy.Create(
            target.TargetId,
            seed.Headers,
            seed.CommandId,
            seed.CorrelationId);
        var envelope = _chatEnvelopeFactory.CreateEnvelope(target.PreparedRequest, context);
        var receipt = _receiptFactory.Create(target, context);
        return CommandTargetResolution<CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunStartError>.Success(
            new CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>
            {
                Target = target,
                Context = context,
                Envelope = envelope,
                Receipt = receipt,
            });
    }

    public async Task<DispatchAdmission> DispatchPreparedAsync(
        CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt> execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        try
        {
            return await _targetDispatcher.DispatchAsync(execution.Target, execution.Envelope, ct).ConfigureAwait(false);
        }
        catch
        {
            await execution.Target.CleanupAfterDispatchFailureAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
