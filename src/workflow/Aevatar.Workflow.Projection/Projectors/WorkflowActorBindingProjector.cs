using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowActorBindingProjector
    : IProjectionMaterializer<WorkflowBindingProjectionContext>
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

    public async ValueTask ProjectAsync(
        WorkflowBindingProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var payload, out var eventId, out var stateVersion) ||
            payload == null)
            return;

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        if (payload.Is(BindWorkflowDefinitionEvent.Descriptor))
        {
            var evt = payload.Unpack<BindWorkflowDefinitionEvent>();
            var document = await GetOrCreateAsync(context.RootActorId, ct);
            document.Id = context.RootActorId;
            document.ActorId = context.RootActorId;
            document.ActorKind = WorkflowActorKind.Definition;
            document.DefinitionActorId = context.RootActorId;
            document.RunId = string.Empty;
            document.WorkflowName = NormalizeWorkflowName(evt.WorkflowName);
            document.WorkflowYaml = evt.WorkflowYaml ?? string.Empty;
            ReplaceInlineWorkflowYamls(document.InlineWorkflowYamls, evt.InlineWorkflowYamls);
            ApplyProjectionMetadata(document, eventId, stateVersion, updatedAt);
            await _writeDispatcher.UpsertAsync(document, ct);
            return;
        }

        if (!payload.Is(BindWorkflowRunDefinitionEvent.Descriptor))
            return;

        var bindRun = payload.Unpack<BindWorkflowRunDefinitionEvent>();
        var runDocument = await GetOrCreateAsync(context.RootActorId, ct);
        runDocument.Id = context.RootActorId;
        runDocument.ActorId = context.RootActorId;
        runDocument.ActorKind = WorkflowActorKind.Run;
        runDocument.DefinitionActorId = bindRun.DefinitionActorId?.Trim() ?? string.Empty;
        runDocument.RunId = ResolveRunId(bindRun.RunId, context.RootActorId);
        runDocument.WorkflowName = NormalizeWorkflowName(bindRun.WorkflowName);
        runDocument.WorkflowYaml = bindRun.WorkflowYaml ?? string.Empty;
        ReplaceInlineWorkflowYamls(runDocument.InlineWorkflowYamls, bindRun.InlineWorkflowYamls);
        ApplyProjectionMetadata(runDocument, eventId, stateVersion, updatedAt);
        await _writeDispatcher.UpsertAsync(runDocument, ct);
    }

    private static void ApplyProjectionMetadata(
        WorkflowActorBindingDocument document,
        string? eventId,
        long stateVersion,
        DateTimeOffset updatedAt)
    {
        if (document.CreatedAt == default)
            document.CreatedAt = updatedAt;
        document.UpdatedAt = updatedAt;
        if (stateVersion > 0)
            document.StateVersion = stateVersion;
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
