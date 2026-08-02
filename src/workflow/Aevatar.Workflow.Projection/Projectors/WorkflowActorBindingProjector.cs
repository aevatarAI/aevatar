using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowActorBindingProjector
    : IProjectionArtifactMaterializer<WorkflowBindingProjectionContext>
{
    private readonly IProjectionWriteDispatcher<WorkflowActorBindingDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public WorkflowActorBindingProjector(
        IProjectionWriteDispatcher<WorkflowActorBindingDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // Refactor (iter355/issue1436-first):
    //   Old pattern: WorkflowActorBindingProjector reads target document during materialization to merge fields
    //   New principle: Projector constructs full document from committed event payload only; overwrites without reading prior readmodel
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
            var document = CreateDefinitionDocument(context.RootActorId, evt);
            ApplyProjectionMetadata(document, eventId, stateVersion, updatedAt);
            await _writeDispatcher.UpsertAsync(document, ct);
            return;
        }

        if (!payload.Is(BindWorkflowRunDefinitionEvent.Descriptor))
            return;

        // A run actor's BindWorkflowRunDefinitionEvent is materialized into the run's OWN binding
        // projection scope (RootActorId == the run actor id). The committed-observation relay also
        // forwards that same committed event UP to the parent definition actor's stream, where it
        // would arrive in the definition's binding scope (RootActorId == the definition actor id).
        // Writing a Run-kind document keyed on the definition actor id there clobbers the Definition
        // document at that _id. Only the run's own scope owns the run binding document, so a relayed
        // delivery (origin actor id != projecting scope root) must be skipped; a workflow-definition
        // _id can never receive a Run-kind document this way.
        var originActorId = CommittedStateEventEnvelope.GetOriginActorId(envelope);
        if (!string.IsNullOrWhiteSpace(originActorId) &&
            !string.Equals(originActorId, context.RootActorId, StringComparison.Ordinal))
        {
            return;
        }

        var bindRun = payload.Unpack<BindWorkflowRunDefinitionEvent>();
        var runDocument = CreateRunDocument(context.RootActorId, bindRun);
        ApplyProjectionMetadata(runDocument, eventId, stateVersion, updatedAt);
        await _writeDispatcher.UpsertAsync(runDocument, ct);
    }

    private static WorkflowActorBindingDocument CreateDefinitionDocument(
        string actorId,
        BindWorkflowDefinitionEvent evt)
    {
        var document = new WorkflowActorBindingDocument
        {
            Id = actorId,
            ActorId = actorId,
            ActorKind = WorkflowActorKind.Definition,
            DefinitionActorId = actorId,
            RunId = string.Empty,
            WorkflowName = NormalizeWorkflowName(evt.WorkflowName),
            WorkflowYaml = evt.WorkflowYaml ?? string.Empty,
            ScopeId = evt.ScopeId?.Trim() ?? string.Empty,
            SourceKind = evt.SourceKind?.Trim() ?? string.Empty,
            WorkflowId = evt.WorkflowId ?? string.Empty,
            RevisionId = evt.RevisionId ?? string.Empty,
            ExpectedExecutionMode = evt.ExpectedExecutionMode,
        };
        if (evt.CapabilityAdmissionPlan is not null)
            document.CapabilityAdmissionPlan = evt.CapabilityAdmissionPlan.Clone();
        ReplaceInlineWorkflowYamls(document.InlineWorkflowYamls, evt.InlineWorkflowYamls);
        return document;
    }

    private static WorkflowActorBindingDocument CreateRunDocument(
        string actorId,
        BindWorkflowRunDefinitionEvent evt)
    {
        var document = new WorkflowActorBindingDocument
        {
            Id = actorId,
            ActorId = actorId,
            ActorKind = WorkflowActorKind.Run,
            DefinitionActorId = evt.DefinitionActorId?.Trim() ?? string.Empty,
            RunId = ResolveRunId(evt.RunId, actorId),
            WorkflowName = NormalizeWorkflowName(evt.WorkflowName),
            WorkflowYaml = evt.WorkflowYaml ?? string.Empty,
            ScopeId = evt.ScopeId?.Trim() ?? string.Empty,
            ExpectedExecutionMode = evt.ExpectedExecutionMode,
        };
        if (evt.CapabilityAdmissionPlan is not null)
            document.CapabilityAdmissionPlan = evt.CapabilityAdmissionPlan.Clone();
        ReplaceInlineWorkflowYamls(document.InlineWorkflowYamls, evt.InlineWorkflowYamls);
        return document;
    }

    private static void ApplyProjectionMetadata(
        WorkflowActorBindingDocument document,
        string? eventId,
        long stateVersion,
        DateTimeOffset updatedAt)
    {
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
}
