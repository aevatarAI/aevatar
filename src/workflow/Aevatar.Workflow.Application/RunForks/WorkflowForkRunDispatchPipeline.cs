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

    public async Task<CommandTargetResolution<CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunStartError>> PrepareAsync(
        WorkflowForkRunCommand command,
        CancellationToken ct = default)
    {
        var resolution = await _targetResolver.ResolveAsync(command, ct).ConfigureAwait(false);
        if (!resolution.Succeeded || resolution.Target == null)
            return CommandTargetResolution<CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunStartError>.Failure(resolution.Error);

        var target = resolution.Target;
        var context = _contextPolicy.Create(
            target.TargetId,
            command.Headers,
            command.CommandId,
            command.CorrelationId);
        var envelope = _chatEnvelopeFactory.CreateEnvelope(target.Request, context);
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

    public async Task<CommandTargetResolution<CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunStartError>> DispatchAsync(
        WorkflowForkRunCommand command,
        CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(command, ct).ConfigureAwait(false);
        if (!prepared.Succeeded || prepared.Target == null)
            return prepared;

        var execution = prepared.Target;
        try
        {
            var admission = await DispatchPreparedAsync(execution, ct).ConfigureAwait(false);
            return CommandTargetResolution<CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunStartError>.Success(
                execution with { Admission = admission });
        }
        catch (Exception ex)
        {
            return CommandTargetResolution<CommandDispatchExecution<WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt>, WorkflowForkRunStartError>.Failure(
                WorkflowForkRunStartError.DispatchFailed(command.SourceRunId, command.StartAtStepId, ex.Message));
        }
    }
}
