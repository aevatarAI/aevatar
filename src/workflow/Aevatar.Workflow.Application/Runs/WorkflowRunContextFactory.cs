using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunContextFactory : IWorkflowRunContextFactory
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowExecutionProjectionLifecyclePort _projectionPort;
    private readonly ICommandContextPolicy _commandContextPolicy;

    public WorkflowRunContextFactory(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionProjectionLifecyclePort projectionPort,
        ICommandContextPolicy commandContextPolicy)
    {
        _actorResolver = actorResolver;
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

        var actor = actorResolution.Actor;
        var workflowNameForRun = actorResolution.WorkflowNameForRun;
        if (!_projectionPort.ProjectionEnabled)
            return new WorkflowRunContextCreateResult(WorkflowChatRunStartError.ProjectionDisabled, null);

        var baseContext = _commandContextPolicy.Create(actor.Id);
        var metadata = new Dictionary<string, string>(baseContext.Metadata, StringComparer.Ordinal)
        {
            [WorkflowRunCommandMetadataKeys.SessionId] = baseContext.CorrelationId,
        };
        var commandContext = new CommandContext(
            baseContext.TargetId,
            baseContext.CommandId,
            baseContext.CorrelationId,
            metadata);
        var sink = new WorkflowRunEventChannel();
        IWorkflowExecutionProjectionLease? projectionLease = null;

        try
        {
            projectionLease = await _projectionPort.EnsureActorProjectionAsync(
                actor.Id,
                workflowNameForRun,
                request.Prompt,
                commandContext.CommandId,
                ct);
            if (projectionLease == null)
                return new WorkflowRunContextCreateResult(WorkflowChatRunStartError.ProjectionDisabled, null);

            await _projectionPort.AttachLiveSinkAsync(projectionLease, sink, ct);
        }
        catch
        {
            await sink.DisposeAsync();
            throw;
        }

        return new WorkflowRunContextCreateResult(
            WorkflowChatRunStartError.None,
            new WorkflowRunContext
            {
                Actor = actor,
                WorkflowName = workflowNameForRun,
                Sink = sink,
                CommandId = commandContext.CommandId,
                CommandContext = commandContext,
                ProjectionLease = projectionLease!,
            });
    }
}
