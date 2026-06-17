using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Projectors;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExternalApprovalContinuationLookupPort
    : IWorkflowExternalApprovalContinuationLookupPort
{
    private readonly IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string> _reader;

    public WorkflowExternalApprovalContinuationLookupPort(
        IProjectionDocumentReader<WorkflowExternalApprovalContinuationDocument, string> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<WorkflowExternalApprovalContinuation?> FindActiveAsync(
        string sourceId,
        string externalIdKind,
        string externalId,
        CancellationToken ct = default)
    {
        if (!WorkflowExternalApprovalContinuationProjector.TryBuildDocumentId(
                sourceId,
                externalIdKind,
                externalId,
                out var documentId))
        {
            return null;
        }

        var document = await _reader.GetAsync(documentId, ct);
        if (document == null ||
            !document.Active ||
            !string.Equals(document.SourceId, NormalizeIdentity(sourceId), StringComparison.Ordinal) ||
            !string.Equals(document.ExternalIdKind, NormalizeIdentity(externalIdKind), StringComparison.Ordinal) ||
            !string.Equals(document.ExternalId, NormalizeIdentity(externalId), StringComparison.Ordinal))
        {
            return null;
        }

        return ToContinuation(document);
    }

    private static WorkflowExternalApprovalContinuation ToContinuation(
        WorkflowExternalApprovalContinuationDocument document) =>
        new(
            document.ActorId,
            document.RunId,
            document.StepId,
            document.SignalName,
            document.SourceId,
            document.ExternalIdKind,
            document.ExternalId,
            document.CallbackIdempotencyKey,
            document.RequestId,
            document.StateVersion,
            document.LastEventId,
            document.UpdatedAt);

    private static string NormalizeIdentity(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
