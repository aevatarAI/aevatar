using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowActorBindingProjector
    : IProjectionProjector<WorkflowBindingProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionStoreDispatcher<WorkflowActorBindingDocument, string> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public WorkflowActorBindingProjector(
        IProjectionStoreDispatcher<WorkflowActorBindingDocument, string> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(WorkflowBindingProjectionContext context, CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        WorkflowBindingProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        var normalized = ProjectionEnvelopeNormalizer.Normalize(envelope);
        if (normalized?.Payload == null)
            return;

        var updatedAt = ProjectionEnvelopeTimestampResolver.Resolve(normalized, _clock.UtcNow);
        if (normalized.Payload.Is(BindWorkflowDefinitionEvent.Descriptor))
        {
            var evt = normalized.Payload.Unpack<BindWorkflowDefinitionEvent>();
            await _storeDispatcher.MutateAsync(context.RootActorId, document =>
            {
                document.Id = context.RootActorId;
                document.ActorId = context.RootActorId;
                document.ActorKind = WorkflowActorKind.Definition;
                document.DefinitionActorId = context.RootActorId;
                document.RunId = string.Empty;
                document.WorkflowName = NormalizeWorkflowName(evt.WorkflowName);
                document.WorkflowYaml = evt.WorkflowYaml ?? string.Empty;
                ReplaceInlineWorkflowYamls(document.InlineWorkflowYamls, evt.InlineWorkflowYamls);
                ApplyProjectionMetadata(document, normalized.Id, updatedAt);
            }, ct);
            return;
        }

        if (!normalized.Payload.Is(BindWorkflowRunDefinitionEvent.Descriptor))
            return;

        var bindRun = normalized.Payload.Unpack<BindWorkflowRunDefinitionEvent>();
        await _storeDispatcher.MutateAsync(context.RootActorId, document =>
        {
            document.Id = context.RootActorId;
            document.ActorId = context.RootActorId;
            document.ActorKind = WorkflowActorKind.Run;
            document.DefinitionActorId = bindRun.DefinitionActorId?.Trim() ?? string.Empty;
            document.RunId = ResolveRunId(bindRun.RunId, context.RootActorId);
            document.WorkflowName = NormalizeWorkflowName(bindRun.WorkflowName);
            document.WorkflowYaml = bindRun.WorkflowYaml ?? string.Empty;
            ReplaceInlineWorkflowYamls(document.InlineWorkflowYamls, bindRun.InlineWorkflowYamls);
            ApplyProjectionMetadata(document, normalized.Id, updatedAt);
        }, ct);
    }

    public ValueTask CompleteAsync(
        WorkflowBindingProjectionContext context,
        IReadOnlyList<string> projectionResult,
        CancellationToken ct = default)
    {
        _ = context;
        _ = projectionResult;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    private static void ApplyProjectionMetadata(
        WorkflowActorBindingDocument document,
        string? eventId,
        DateTimeOffset updatedAt)
    {
        if (document.CreatedAt == default)
            document.CreatedAt = updatedAt;
        document.UpdatedAt = updatedAt;
        document.StateVersion += 1;
        document.LastEventId = eventId ?? string.Empty;
    }

    private static string NormalizeWorkflowName(string? workflowName) =>
        string.IsNullOrWhiteSpace(workflowName)
            ? string.Empty
            : workflowName.Trim();

    private static string ResolveRunId(string? runId, string fallbackActorId) =>
        string.IsNullOrWhiteSpace(runId)
            ? WorkflowRunIdNormalizer.Normalize(fallbackActorId)
            : WorkflowRunIdNormalizer.Normalize(runId);

    private static void ReplaceInlineWorkflowYamls(
        Dictionary<string, string> target,
        Google.Protobuf.Collections.MapField<string, string> source)
    {
        target.Clear();
        foreach (var (workflowNameKey, workflowYamlValue) in source)
        {
            var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowNameKey);
            if (string.IsNullOrWhiteSpace(normalizedWorkflowName) ||
                string.IsNullOrWhiteSpace(workflowYamlValue))
            {
                continue;
            }

            target[normalizedWorkflowName] = workflowYamlValue;
        }
    }
}
