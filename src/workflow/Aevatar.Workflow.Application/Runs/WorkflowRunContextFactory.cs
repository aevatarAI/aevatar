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
        var metadata = new Dictionary<string, string>(baseContext.Metadata, StringComparer.Ordinal);
        MergeRequestMetadata(metadata, request.Metadata);
        if (!metadata.TryGetValue(WorkflowRunCommandMetadataKeys.SessionId, out var sessionId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            metadata[WorkflowRunCommandMetadataKeys.SessionId] = baseContext.CorrelationId;
        }
        var commandContext = new CommandContext(
            baseContext.TargetId,
            baseContext.CommandId,
            baseContext.CorrelationId,
            metadata);
        var sink = new EventChannel<WorkflowRunEvent>();
        var projectionLease = await _projectionPort.EnsureAndAttachAsync(
            token => _projectionPort.EnsureActorProjectionAsync(
                actor.Id,
                workflowNameForRun,
                request.Prompt,
                commandContext.CommandId,
                token),
            sink,
            ct);
        if (projectionLease == null)
            return new WorkflowRunContextCreateResult(WorkflowChatRunStartError.ProjectionDisabled, null);

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

    private static void MergeRequestMetadata(
        IDictionary<string, string> target,
        IReadOnlyDictionary<string, string>? requestMetadata)
    {
        if (requestMetadata == null || requestMetadata.Count == 0)
            return;

        foreach (var (key, value) in requestMetadata)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;
            target[normalizedKey] = normalizedValue;
        }
    }
}
