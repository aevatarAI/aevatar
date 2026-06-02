using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowChatRunInteractionService : IWorkflowChatRunInteractionPort
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly IWorkflowRunProvisioningPort _runProvisioningPort;
    private readonly IWorkflowChatRunObservationScopeActivationPort _activationPort;
    private readonly ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> _inner;
    private readonly WorkflowDirectFallbackPolicy _fallbackPolicy;

    public WorkflowChatRunInteractionService(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionProjectionPort projectionPort,
        IWorkflowRunProvisioningPort runProvisioningPort,
        IWorkflowChatRunObservationScopeActivationPort activationPort,
        ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus> inner,
        WorkflowDirectFallbackPolicy fallbackPolicy)
    {
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
        _activationPort = activationPort ?? throw new ArgumentNullException(nameof(activationPort));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _fallbackPolicy = fallbackPolicy ?? throw new ArgumentNullException(nameof(fallbackPolicy));
    }

    public async Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAsync(
        WorkflowChatRunRequest request,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var commandId = CreateInteractionId();
        var correlationId = CreateInteractionId();
        var currentRequest = request with
        {
            CommandIdSeed = commandId,
            CorrelationIdSeed = correlationId,
            TargetSeed = null,
        };

        while (true)
        {
            var attempt = await StartAttemptAsync(currentRequest, ct).ConfigureAwait(false);
            if (!attempt.Succeeded)
                return CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>.Failure(attempt.Error);

            try
            {
                return await ExecuteAttemptAsync(
                    attempt.Value!,
                    emitAsync,
                    onAcceptedAsync,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!attempt.Value!.Accepted &&
                                       _fallbackPolicy.TryCreateFallbackCommand(currentRequest, ex, out var fallbackRequest))
            {
                await CleanupAttemptAsync(attempt.Value!, CancellationToken.None).ConfigureAwait(false);
                currentRequest = fallbackRequest with
                {
                    CommandIdSeed = commandId,
                    CorrelationIdSeed = correlationId,
                    Headers = request.Headers,
                    TargetSeed = null,
                };
            }
            catch
            {
                if (!attempt.Value!.Accepted)
                    await CleanupAttemptAsync(attempt.Value!, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task<AttemptStartResult> StartAttemptAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct)
    {
        if (!_projectionPort.ProjectionEnabled)
            return AttemptStartResult.Failure(WorkflowChatRunStartError.ProjectionDisabled);

        var actorResolution = await _actorResolver.ResolveOrCreateAsync(request, ct).ConfigureAwait(false);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Target == null)
            return AttemptStartResult.Failure(actorResolution.Error);

        var activation = await _activationPort.ActivateAsync(
            actorResolution.Target.ActorId,
            request.CommandIdSeed ?? string.Empty,
            ct).ConfigureAwait(false);
        if (activation == null)
        {
            await RollbackCreatedActorsAsync(actorResolution.Target.CreatedActorIds, CancellationToken.None).ConfigureAwait(false);
            return AttemptStartResult.Failure(WorkflowChatRunStartError.ProjectionUnavailable);
        }

        var seededRequest = request with
        {
            TargetSeed = new WorkflowRunTargetSeed(
                actorResolution.Target.ActorId,
                actorResolution.WorkflowNameForRun,
                actorResolution.Target.CreatedActorIds,
                request.Source),
        };

        return AttemptStartResult.Success(new WorkflowChatRunInteractionAttempt(
            seededRequest,
            activation,
            actorResolution.Target.CreatedActorIds));
    }

    private async Task<CommandInteractionResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowProjectionCompletionStatus>> ExecuteAttemptAsync(
        WorkflowChatRunInteractionAttempt attempt,
        Func<WorkflowRunEventEnvelope, CancellationToken, ValueTask> emitAsync,
        Func<WorkflowChatRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync,
        CancellationToken ct)
    {
        async ValueTask OnAcceptedAsync(WorkflowChatRunAcceptedReceipt receipt, CancellationToken token)
        {
            attempt.MarkAccepted();
            if (onAcceptedAsync != null)
                await onAcceptedAsync(receipt, token).ConfigureAwait(false);
        }

        var result = await _inner.ExecuteAsync(
            attempt.Request,
            emitAsync,
            OnAcceptedAsync,
            ct).ConfigureAwait(false);
        if (!result.Succeeded && !attempt.Accepted)
            await CleanupAttemptAsync(attempt, CancellationToken.None).ConfigureAwait(false);

        return result;
    }

    private async Task CleanupAttemptAsync(
        WorkflowChatRunInteractionAttempt attempt,
        CancellationToken ct)
    {
        await _activationPort.ReleaseAsync(attempt.Activation, ct).ConfigureAwait(false);
        await RollbackCreatedActorsAsync(attempt.CreatedActorIds, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RollbackCreatedActorsAsync(
        IReadOnlyList<string>? createdActorIds,
        CancellationToken ct)
    {
        if (createdActorIds is not { Count: > 0 })
            return;

        foreach (var actorId in createdActorIds
                     .Where(static x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal)
                     .Reverse())
        {
            await _runProvisioningPort.DestroyAsync(actorId, ct).ConfigureAwait(false);
        }
    }

    private static string CreateInteractionId() => Guid.NewGuid().ToString("N");

    private sealed record WorkflowChatRunInteractionAttempt(
        WorkflowChatRunRequest Request,
        WorkflowChatRunObservationScopeActivation Activation,
        IReadOnlyList<string> CreatedActorIds)
    {
        public bool Accepted { get; private set; }

        public void MarkAccepted() => Accepted = true;
    }

    private sealed record AttemptStartResult(
        WorkflowChatRunInteractionAttempt? Value,
        WorkflowChatRunStartError Error)
    {
        public bool Succeeded => Error == WorkflowChatRunStartError.None && Value != null;

        public static AttemptStartResult Success(WorkflowChatRunInteractionAttempt attempt) =>
            new(attempt, WorkflowChatRunStartError.None);

        public static AttemptStartResult Failure(WorkflowChatRunStartError error) =>
            new(null, error);
    }
}
