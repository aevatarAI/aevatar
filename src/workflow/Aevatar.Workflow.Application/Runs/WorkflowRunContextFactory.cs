using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

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

        if (!_projectionPort.ProjectionEnabled)
            return new WorkflowRunContextCreateResult(WorkflowChatRunStartError.ProjectionDisabled, null);

        var actorResolution = await _actorResolver.ResolveOrCreateAsync(request, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.RunActor == null)
            return new WorkflowRunContextCreateResult(actorResolution.Error, null);

        var runActor = actorResolution.RunActor;
        var workflowNameForRun = actorResolution.WorkflowNameForRun;

        var baseContext = _commandContextPolicy.Create(runActor.Id);
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
        try
        {
            var projectionLease = await _projectionPort.EnsureAndAttachAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    runActor.Id,
                    workflowNameForRun,
                    request.Prompt,
                    commandContext.CommandId,
                    token),
                sink,
                ct);
            if (projectionLease == null)
            {
                await DisposeSinkAsync(sink);
                await _actorPort.DestroyRunActorAsync(runActor.Id, CancellationToken.None);
                return new WorkflowRunContextCreateResult(WorkflowChatRunStartError.ProjectionDisabled, null);
            }

            return new WorkflowRunContextCreateResult(
                WorkflowChatRunStartError.None,
                new WorkflowRunContext
                {
                    RunActor = runActor,
                    DefinitionActorId = actorResolution.DefinitionActorId,
                    WorkflowName = workflowNameForRun,
                    Sink = sink,
                    CommandId = commandContext.CommandId,
                    CommandContext = commandContext,
                    ProjectionLease = projectionLease,
                });
        }
        catch
        {
            await DisposeSinkAsync(sink);
            await _actorPort.DestroyRunActorAsync(runActor.Id, CancellationToken.None);
            throw;
        }
    }

    private static async Task DisposeSinkAsync(IEventSink<WorkflowRunEvent> sink)
    {
        sink.Complete();
        await sink.DisposeAsync();
    }
}
