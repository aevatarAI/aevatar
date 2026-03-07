using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunContextFactory : IWorkflowRunContextFactory
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowRunActorPort _actorPort;
    private readonly IWorkflowExecutionProjectionLifecyclePort _projectionPort;
    private readonly ICommandContextPolicy _commandContextPolicy;
    private readonly ILogger<WorkflowRunContextFactory> _logger;

    public WorkflowRunContextFactory(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowRunActorPort actorPort,
        IWorkflowExecutionProjectionLifecyclePort projectionPort,
        ICommandContextPolicy commandContextPolicy,
        ILogger<WorkflowRunContextFactory>? logger = null)
    {
        _actorResolver = actorResolver;
        _actorPort = actorPort;
        _projectionPort = projectionPort;
        _commandContextPolicy = commandContextPolicy;
        _logger = logger ?? NullLogger<WorkflowRunContextFactory>.Instance;
    }

    public async Task<WorkflowRunContextCreateResult> CreateAsync(
        WorkflowChatRunRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
        var (sink, projectionLease) = await TryAttachLiveDeliveryAsync(
            runActor.Id,
            workflowNameForRun,
            request.Prompt,
            commandContext.CommandId,
            ct);

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

    private async Task<(IEventSink<WorkflowRunEvent>? Sink, IWorkflowExecutionProjectionLease? Lease)> TryAttachLiveDeliveryAsync(
        string runActorId,
        string workflowName,
        string prompt,
        string commandId,
        CancellationToken ct)
    {
        if (!_projectionPort.ProjectionEnabled)
            return (null, null);

        var sink = new EventChannel<WorkflowRunEvent>();
        IWorkflowExecutionProjectionLease? lease = null;
        try
        {
            lease = await _projectionPort.EnsureAndAttachAsync(
                token => _projectionPort.EnsureActorProjectionAsync(
                    runActorId,
                    workflowName,
                    prompt,
                    commandId,
                    token),
                sink,
                ct);
            if (lease == null)
            {
                await DisposeSinkAsync(sink);
                return (null, null);
            }

            return (sink, lease);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow live delivery attach failed. run_actor_id={RunActorId} workflow_name={WorkflowName} command_id={CommandId}",
                runActorId,
                workflowName,
                commandId);
            await CleanupFailedProjectionAttachAsync(lease, sink);
            return (null, null);
        }
    }

    private async Task CleanupFailedProjectionAttachAsync(
        IWorkflowExecutionProjectionLease? lease,
        IEventSink<WorkflowRunEvent> sink)
    {
        try
        {
            if (lease != null)
                await _projectionPort.ReleaseActorProjectionAsync(lease, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Workflow live delivery release failed after attach error.");
        }
        finally
        {
            await DisposeSinkAsync(sink);
        }
    }

    private static async Task DisposeSinkAsync(IEventSink<WorkflowRunEvent> sink)
    {
        sink.Complete();
        await sink.DisposeAsync();
    }
}
