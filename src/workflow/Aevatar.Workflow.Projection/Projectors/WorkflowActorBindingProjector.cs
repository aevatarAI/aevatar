using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowActorBindingProjector
    : IProjectionProjector<WorkflowBindingProjectionContext, IReadOnlyList<string>>
{
    private readonly IProjectionWriteDispatcher<WorkflowActorBindingDocument> _writeDispatcher;
    private readonly IProjectionDocumentReader<WorkflowActorBindingDocument, string> _documentReader;
    private readonly IProjectionClock _clock;

    public WorkflowActorBindingProjector(
        IProjectionWriteDispatcher<WorkflowActorBindingDocument> writeDispatcher,
        IProjectionDocumentReader<WorkflowActorBindingDocument, string> documentReader,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
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
            var document = await GetOrCreateAsync(context.RootActorId, ct);
            document.Id = context.RootActorId;
            document.ActorId = context.RootActorId;
            document.ActorKind = WorkflowActorKind.Definition;
            document.DefinitionActorId = context.RootActorId;
            document.RunId = string.Empty;
            document.WorkflowName = NormalizeWorkflowName(evt.WorkflowName);
            document.WorkflowYaml = evt.WorkflowYaml ?? string.Empty;
            ReplaceInlineWorkflowYamls(document.InlineWorkflowYamls, evt.InlineWorkflowYamls);
            ApplyProjectionMetadata(document, normalized.Id, updatedAt);
            await _writeDispatcher.UpsertAsync(document, ct);
            return;
        }

        if (!normalized.Payload.Is(BindWorkflowRunDefinitionEvent.Descriptor))
            return;

        var bindRun = normalized.Payload.Unpack<BindWorkflowRunDefinitionEvent>();
        var runDocument = await GetOrCreateAsync(context.RootActorId, ct);
        runDocument.Id = context.RootActorId;
        runDocument.ActorId = context.RootActorId;
        runDocument.ActorKind = WorkflowActorKind.Run;
        runDocument.DefinitionActorId = bindRun.DefinitionActorId?.Trim() ?? string.Empty;
        runDocument.RunId = ResolveRunId(bindRun.RunId, context.RootActorId);
        runDocument.WorkflowName = NormalizeWorkflowName(bindRun.WorkflowName);
        runDocument.WorkflowYaml = bindRun.WorkflowYaml ?? string.Empty;
        ReplaceInlineWorkflowYamls(runDocument.InlineWorkflowYamls, bindRun.InlineWorkflowYamls);
        ApplyProjectionMetadata(runDocument, normalized.Id, updatedAt);
        await _writeDispatcher.UpsertAsync(runDocument, ct);
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
        IDictionary<string, string> target,
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

    private async Task<WorkflowActorBindingDocument> GetOrCreateAsync(string actorId, CancellationToken ct)
    {
        return await _documentReader.GetAsync(actorId, ct) ?? new WorkflowActorBindingDocument
        {
            Id = actorId,
            ActorId = actorId,
        };
    }
}
