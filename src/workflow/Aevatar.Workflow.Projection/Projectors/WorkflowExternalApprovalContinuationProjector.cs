using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowExternalApprovalContinuationProjector
    : IProjectionArtifactMaterializer<WorkflowExecutionMaterializationContext>
{
    private readonly IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string> _reader;
    private readonly IProjectionWriteDispatcher<WorkflowExternalApprovalContinuationDocument> _writer;
    private readonly IProjectionClock _clock;

    public WorkflowExternalApprovalContinuationProjector(
        IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string> reader,
        IProjectionWriteDispatcher<WorkflowExternalApprovalContinuationDocument> writer,
        IProjectionClock clock)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        WorkflowExecutionMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryGetObservedPayload(
                envelope,
                out var payload,
                out var eventId,
                out var stateVersion) ||
            payload == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        if (payload.Is(WorkflowExternalApprovalContinuationRegisteredEvent.Descriptor))
        {
            var registered = payload.Unpack<WorkflowExternalApprovalContinuationRegisteredEvent>();
            if (!TryBuildDocumentId(registered.SourceId, registered.ExternalIdKind, registered.ExternalId, out var documentId))
                return;

            var existing = await _reader.GetAsync(documentId, ct);
            if (ShouldSkip(existing, stateVersion, eventId))
                return;

            await _writer.UpsertAsync(new WorkflowExternalApprovalContinuationDocument
            {
                Id = documentId,
                ActorId = context.RootActorId,
                RunId = Normalize(registered.RunId),
                StepId = Normalize(registered.StepId),
                SignalName = NormalizeSignalName(registered.SignalName),
                SourceId = NormalizeIdentity(registered.SourceId),
                ExternalIdKind = NormalizeIdentity(registered.ExternalIdKind),
                ExternalId = NormalizeIdentity(registered.ExternalId),
                CallbackIdempotencyKey = Normalize(registered.CallbackIdempotencyKey),
                RequestId = Normalize(registered.RequestId),
                Active = true,
                StateVersion = stateVersion,
                LastEventId = eventId ?? string.Empty,
                UpdatedAt = updatedAt,
            }, ct);
            return;
        }

        if (!payload.Is(WorkflowExternalApprovalContinuationClearedEvent.Descriptor))
            return;

        var cleared = payload.Unpack<WorkflowExternalApprovalContinuationClearedEvent>();
        if (!TryBuildDocumentId(cleared.SourceId, cleared.ExternalIdKind, cleared.ExternalId, out var clearDocumentId))
            return;

        var current = await _reader.GetAsync(clearDocumentId, ct);
        if (current == null || ShouldSkip(current, stateVersion, eventId))
            return;

        if (!MatchesActiveBinding(current, cleared))
            return;

        current.Active = false;
        current.StateVersion = stateVersion;
        current.LastEventId = eventId ?? string.Empty;
        current.UpdatedAt = updatedAt;
        await _writer.UpsertAsync(current, ct);
    }

    internal static bool TryBuildDocumentId(
        string? sourceId,
        string? externalIdKind,
        string? externalId,
        out string documentId)
    {
        var normalizedSourceId = NormalizeIdentity(sourceId);
        var normalizedKind = NormalizeIdentity(externalIdKind);
        var normalizedExternalId = NormalizeIdentity(externalId);
        if (string.IsNullOrWhiteSpace(normalizedSourceId) ||
            string.IsNullOrWhiteSpace(normalizedKind) ||
            string.IsNullOrWhiteSpace(normalizedExternalId))
        {
            documentId = string.Empty;
            return false;
        }

        documentId = $"external-approval:{normalizedSourceId}:{normalizedKind}:{normalizedExternalId}";
        return true;
    }

    private static bool ShouldSkip(
        WorkflowExternalApprovalContinuationDocument? existing,
        long stateVersion,
        string? eventId)
    {
        if (existing == null)
            return false;

        if (existing.StateVersion > stateVersion)
            return true;

        return existing.StateVersion == stateVersion &&
               string.Equals(existing.LastEventId, eventId ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool MatchesActiveBinding(
        WorkflowExternalApprovalContinuationDocument current,
        WorkflowExternalApprovalContinuationClearedEvent cleared)
    {
        return current.Active &&
               string.Equals(current.RunId, Normalize(cleared.RunId), StringComparison.Ordinal) &&
               string.Equals(current.StepId, Normalize(cleared.StepId), StringComparison.Ordinal) &&
               string.Equals(current.SignalName, NormalizeSignalName(cleared.SignalName), StringComparison.Ordinal) &&
               string.Equals(current.CallbackIdempotencyKey, Normalize(cleared.CallbackIdempotencyKey), StringComparison.Ordinal) &&
               string.Equals(current.RequestId, Normalize(cleared.RequestId), StringComparison.Ordinal);
    }

    private static string NormalizeSignalName(string? value)
    {
        var normalized = Normalize(value);
        return string.IsNullOrWhiteSpace(normalized) ? "default" : normalized.ToLowerInvariant();
    }

    private static string NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
