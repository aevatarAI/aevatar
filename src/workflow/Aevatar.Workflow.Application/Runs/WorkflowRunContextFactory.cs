using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using System.Runtime.ExceptionServices;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunContextFactory : IWorkflowRunContextFactory
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowRunActorPort _actorPort;
    private readonly IWorkflowExecutionProjectionLifecyclePort _projectionPort;
    private readonly ICommandContextPolicy _commandContextPolicy;

    public WorkflowRunContextFactory(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowRunActorPort actorPort,
        IWorkflowExecutionProjectionLifecyclePort projectionPort,
        ICommandContextPolicy commandContextPolicy)
    {
        _actorResolver = actorResolver;
        _actorPort = actorPort;
        _projectionPort = projectionPort;
        _commandContextPolicy = commandContextPolicy;
    }

    public async Task<WorkflowRunContextCreateResult> CreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actorResolution = await _actorResolver.ResolveOrCreateAsync(request, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Actor == null)
            return new WorkflowRunContextCreateResult(actorResolution.Error, null);

        var executionActor = actorResolution.Actor;
        var workflowNameForRun = actorResolution.WorkflowNameForRun;
        if (!_projectionPort.ProjectionEnabled)
        {
            await ThrowIfRollbackFailedAsync(actorResolution.CreatedActorIds);
            return new WorkflowRunContextCreateResult(WorkflowChatRunStartError.ProjectionDisabled, null);
        }

        try
        {
            var baseContext = _commandContextPolicy.Create(executionActor.Id);
            var metadata = new Dictionary<string, string>(baseContext.Metadata, StringComparer.Ordinal)
            {
                [WorkflowRunCommandMetadataKeys.SessionId] = baseContext.CorrelationId,
            };
            var commandContext = new CommandContext(
                baseContext.TargetId,
                baseContext.CommandId,
                baseContext.CorrelationId,
                metadata);
            var sink = new EventChannel<WorkflowRunEvent>();
            var projectionLease = await _projectionPort.EnsureAndAttachAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    executionActor.Id,
                    workflowNameForRun,
                    request.Prompt,
                    commandContext.CommandId,
                    token),
                sink,
                ct);
            if (projectionLease == null)
            {
                await ThrowIfRollbackFailedAsync(actorResolution.CreatedActorIds);
                return new WorkflowRunContextCreateResult(WorkflowChatRunStartError.ProjectionDisabled, null);
            }

            return new WorkflowRunContextCreateResult(
                WorkflowChatRunStartError.None,
                new WorkflowRunContext
                {
                    Actor = executionActor,
                    WorkflowName = workflowNameForRun,
                    Sink = sink,
                    CommandId = commandContext.CommandId,
                    CommandContext = commandContext,
                    ProjectionLease = projectionLease!,
                });
        }
        catch (Exception ex)
        {
            var rollbackError = await TryRollbackCreatedActorsAsync(actorResolution.CreatedActorIds);
            if (rollbackError == null)
                throw;

            ExceptionDispatchInfo.Capture(
                new AggregateException(
                    "Workflow run context creation failed and rollback also failed.",
                    ex,
                    rollbackError)).Throw();
            throw;
        }
    }

    private async Task ThrowIfRollbackFailedAsync(IReadOnlyList<string>? actorIds)
    {
        var rollbackError = await TryRollbackCreatedActorsAsync(actorIds);
        if (rollbackError != null)
            throw rollbackError;
    }

    private async Task<Exception?> TryRollbackCreatedActorsAsync(IReadOnlyList<string>? actorIds)
    {
        if (actorIds == null || actorIds.Count == 0)
            return null;

        List<Exception>? failures = null;
        foreach (var actorId in actorIds
                     .Where(static x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal)
                     .Reverse())
        {
            try
            {
                await _actorPort.DestroyAsync(actorId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(new InvalidOperationException(
                    $"Failed to rollback workflow actor '{actorId}'.",
                    ex));
            }
        }

        return failures switch
        {
            null => null,
            { Count: 1 } => failures[0],
            _ => new AggregateException("Workflow actor rollback failed.", failures),
        };
    }
}
