using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.WorkOrder;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

public sealed class ProjectionWorkOrderQueryPort : IWorkOrderQueryPort
{
    public const int MaxPageSize = 200;

    private static readonly IReadOnlySet<string> KnownStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        WorkOrderLifecycleStatusNames.Accepted,
        WorkOrderLifecycleStatusNames.Ready,
        WorkOrderLifecycleStatusNames.DispatchPending,
        WorkOrderLifecycleStatusNames.Running,
        WorkOrderLifecycleStatusNames.Completed,
        WorkOrderLifecycleStatusNames.Failed,
        WorkOrderLifecycleStatusNames.Stopped,
        WorkOrderLifecycleStatusNames.Cancelled,
        WorkOrderLifecycleStatusNames.TimedOut,
    };

    private readonly IProjectionDocumentReader<WorkOrderCurrentStateDocument, string> _documentReader;

    public ProjectionWorkOrderQueryPort(
        IProjectionDocumentReader<WorkOrderCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<WorkOrderListResponse> ListAsync(
        string scopeId,
        WorkOrderQueryRequest query,
        CancellationToken ct = default)
    {
        var normalizedScopeId = WorkOrderConventions.NormalizeScopeId(scopeId);
        query ??= new WorkOrderQueryRequest();
        if (query.CreatedFromUtc > query.CreatedToUtc)
            throw new InvalidOperationException("createdFromUtc must not be later than createdToUtc.");

        var filters = new List<ProjectionDocumentFilter>
        {
            Equal("scope_id", normalizedScopeId),
        };
        AddOptionalEqual(filters, "lifecycle_status", NormalizeStatus(query.Status));
        AddOptionalEqual(filters, "requester_principal_id", NormalizeOptional(query.RequesterPrincipalId));
        AddOptionalEqual(filters, "team_id", NormalizeOptional(query.TeamId));
        AddOptionalEqual(filters, "member_id", NormalizeOptional(query.MemberId));
        AddOptionalEqual(filters, "published_service_id", NormalizeOptional(query.PublishedServiceId));
        AddOptionalEqual(filters, "workflow_id", NormalizeOptional(query.WorkflowId));
        AddOptionalEqual(filters, "run_id", NormalizeOptional(query.RunId));
        if (query.CreatedFromUtc.HasValue)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = "created_at_unix_ms",
                Operator = ProjectionDocumentFilterOperator.Gte,
                Value = ProjectionDocumentValue.FromInt64(query.CreatedFromUtc.Value.ToUnixTimeMilliseconds()),
            });
        }
        if (query.CreatedToUtc.HasValue)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = "created_at_unix_ms",
                Operator = ProjectionDocumentFilterOperator.Lte,
                Value = ProjectionDocumentValue.FromInt64(query.CreatedToUtc.Value.ToUnixTimeMilliseconds()),
            });
        }

        var pageSize = query.PageSize is > 0 and <= MaxPageSize
            ? query.PageSize.Value
            : MaxPageSize;
        var result = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Filters = filters,
                Take = pageSize,
                Cursor = NormalizeOptional(query.PageToken),
            },
            ct);

        return new WorkOrderListResponse(
            normalizedScopeId,
            result.Items
                .Where(item => string.Equals(item.ScopeId, normalizedScopeId, StringComparison.Ordinal))
                .Select(ToResponse)
                .ToArray(),
            NormalizeOptional(result.NextCursor));
    }

    public async Task<WorkOrderCurrentStateResponse?> GetAsync(
        string scopeId,
        string workOrderId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = WorkOrderConventions.NormalizeScopeId(scopeId);
        var normalizedWorkOrderId = WorkOrderConventions.NormalizeWorkOrderId(workOrderId);
        var document = await _documentReader.GetAsync(
            WorkOrderConventions.BuildActorId(normalizedScopeId, normalizedWorkOrderId),
            ct);
        if (document == null || !string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            return null;
        return ToResponse(document);
    }

    private static WorkOrderCurrentStateResponse ToResponse(WorkOrderCurrentStateDocument document) =>
        new(
            document.WorkOrderId,
            document.ScopeId,
            document.TeamId,
            new WorkOrderPrincipalContract(
                document.RequesterPrincipalId,
                document.RequesterPrincipalKind),
            document.MemberId,
            document.PublishedServiceId,
            NormalizeOptional(document.WorkflowId),
            document.ServiceRevisionId,
            document.ImplementationKind,
            document.EndpointId,
            document.Intent,
            document.DedupKey,
            document.LifecycleStatus,
            document.LifecycleVersion,
            document.StateVersion,
            new WorkOrderServiceInputContract(
                new WorkOrderChatInputContract(document.InputPrompt),
                document.InputArtifacts.Select(ToArtifact).ToArray(),
                document.DeclaredResultArtifacts.Select(ToArtifact).ToArray()),
            ToRun(document),
            ToRunOutcome(document.RunOutcome),
            ToRunOutcome(document.LateRunOutcome),
            ToFailure(document),
            NormalizeOptional(document.TerminalReason),
            FromUnixTimeMilliseconds(document.CreatedAtUnixMs) ?? DateTimeOffset.MinValue,
            document.WorkOrderUpdatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            FromUnixTimeMilliseconds(document.TimeoutAtUnixMs));

    private static WorkOrderRunLinkResponse? ToRun(WorkOrderCurrentStateDocument document) =>
        string.IsNullOrWhiteSpace(document.RunId)
            ? null
            : new WorkOrderRunLinkResponse(
                document.RunId,
                document.RunActorId,
                document.RunCommandId,
                document.RunCorrelationId,
                document.RunRevisionId,
                document.RunDeploymentId,
                FromUnixTimeMilliseconds(document.RunAcceptedAtUnixMs) ?? DateTimeOffset.MinValue);

    private static WorkOrderRunOutcomeReferenceResponse? ToRunOutcome(
        WorkOrderRunOutcomeReferenceDocument? document) =>
        document == null || string.IsNullOrWhiteSpace(document.DeliveryId)
            ? null
            : new WorkOrderRunOutcomeReferenceResponse(
                document.DeliveryId,
                document.RunId,
                document.RunActorId,
                document.CommandId,
                document.CorrelationId,
                document.Outcome,
                FromUnixTimeMilliseconds(document.TerminalAtUnixMs) ?? DateTimeOffset.MinValue);

    private static WorkOrderFailureResponse? ToFailure(WorkOrderCurrentStateDocument document) =>
        string.IsNullOrWhiteSpace(document.FailureCode)
            ? null
            : new WorkOrderFailureResponse(
                document.FailureCode,
                document.FailureMessage,
                document.FailureSource,
                NormalizeOptional(document.FailureReferenceId));

    private static WorkOrderArtifactReferenceContract ToArtifact(
        WorkOrderArtifactReferenceDocument document) =>
        new(
            document.ArtifactId,
            document.ArtifactKind,
            NormalizeOptional(document.Uri),
            NormalizeOptional(document.RevisionId));

    private static ProjectionDocumentFilter Equal(string fieldPath, string value) =>
        new()
        {
            FieldPath = fieldPath,
            Operator = ProjectionDocumentFilterOperator.Eq,
            Value = ProjectionDocumentValue.FromString(value),
        };

    private static void AddOptionalEqual(
        ICollection<ProjectionDocumentFilter> filters,
        string fieldPath,
        string? value)
    {
        if (value != null)
            filters.Add(Equal(fieldPath, value));
    }

    private static string? NormalizeStatus(string? status)
    {
        var normalized = NormalizeOptional(status);
        if (normalized != null && !KnownStatuses.Contains(normalized))
            throw new InvalidOperationException($"Unknown WorkOrder status '{status}'.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset? FromUnixTimeMilliseconds(long value) =>
        value <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
}
