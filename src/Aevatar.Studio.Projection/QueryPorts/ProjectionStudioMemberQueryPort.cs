using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

/// <summary>
/// Reads StudioMember roster and detail from the projection document store.
/// Pure query semantics — never replays events, never calls the actor
/// runtime, never falls back to the scope binding read model. Roster scans
/// are constrained to the requested <c>scope_id</c> using the denormalized
/// projector field, so members from other scopes are not visible.
///
/// All fields read from the document are wire-stable strings. The query
/// port does not unpack any <see cref="Google.Protobuf.WellKnownTypes.Any"/>
/// payload — see CLAUDE.md `状态镜像契约面向查询` and the proto comment
/// on <see cref="StudioMemberCurrentStateDocument"/>.
/// </summary>
public sealed class ProjectionStudioMemberQueryPort : IStudioMemberQueryPort
{
    public const int MaxRosterPageSize = 200;

    private readonly IProjectionDocumentReader<StudioMemberCurrentStateDocument, string> _documentReader;

    public ProjectionStudioMemberQueryPort(
        IProjectionDocumentReader<StudioMemberCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<StudioMemberRosterResponse> ListAsync(
        string scopeId,
        StudioMemberRosterPageRequest? page = null,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(scopeId);
        var requestedPageSize = page?.PageSize ?? MaxRosterPageSize;
        if (requestedPageSize <= 0 || requestedPageSize > MaxRosterPageSize)
            requestedPageSize = MaxRosterPageSize;

        var query = new ProjectionDocumentQuery
        {
            Filters =
            [
                new ProjectionDocumentFilter
                {
                    FieldPath = "scope_id",
                    Operator = ProjectionDocumentFilterOperator.Eq,
                    Value = ProjectionDocumentValue.FromString(normalizedScopeId),
                },
            ],
            Take = requestedPageSize,
            Cursor = string.IsNullOrWhiteSpace(page?.Cursor) ? null : page!.Cursor,
        };

        var result = await _documentReader.QueryAsync(query, ct);
        var summaries = result.Items
            .Where(item => string.Equals(item.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            .Select(ToSummary)
            .ToList();

        return new StudioMemberRosterResponse(
            ScopeId: normalizedScopeId,
            Members: summaries,
            NextPageToken: string.IsNullOrWhiteSpace(result.NextCursor) ? null : result.NextCursor);
    }

    public async Task<StudioMemberDetailResponse?> GetAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(scopeId);
        var normalizedMemberId = StudioMemberConventions.NormalizeMemberId(memberId);
        var actorId = StudioMemberConventions.BuildActorId(normalizedScopeId, normalizedMemberId);

        var document = await _documentReader.GetAsync(actorId, ct);
        if (document == null)
            return null;

        if (!string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            return null;

        return ToDetail(document);
    }

    private static StudioMemberSummaryResponse ToSummary(StudioMemberCurrentStateDocument document)
    {
        return new StudioMemberSummaryResponse(
            MemberId: document.MemberId,
            ScopeId: document.ScopeId,
            DisplayName: document.DisplayName,
            Description: document.Description,
            ImplementationKind: NormalizeImplementationKindWire(document.ImplementationKind),
            LifecycleStage: NormalizeLifecycleStageWire(document.LifecycleStage),
            PublishedServiceId: document.PublishedServiceId,
            LastBoundRevisionId: string.IsNullOrEmpty(document.LastBoundRevisionId)
                ? null
                : document.LastBoundRevisionId,
            CreatedAt: document.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            UpdatedAt: document.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);
    }

    private static StudioMemberDetailResponse ToDetail(StudioMemberCurrentStateDocument document)
    {
        var summary = ToSummary(document);
        var implementationRef = ToImplementationRefResponse(document, summary.ImplementationKind);
        var lastBinding = ToLastBindingResponse(document);
        return new StudioMemberDetailResponse(summary, implementationRef, lastBinding);
    }

    private static StudioMemberImplementationRefResponse? ToImplementationRefResponse(
        StudioMemberCurrentStateDocument document,
        string implementationKindWire)
    {
        if (!string.IsNullOrEmpty(document.ImplementationWorkflowId))
        {
            return new StudioMemberImplementationRefResponse(
                ImplementationKind: implementationKindWire,
                WorkflowId: document.ImplementationWorkflowId,
                WorkflowRevision: string.IsNullOrEmpty(document.ImplementationWorkflowRevision)
                    ? null
                    : document.ImplementationWorkflowRevision);
        }

        if (!string.IsNullOrEmpty(document.ImplementationScriptId))
        {
            return new StudioMemberImplementationRefResponse(
                ImplementationKind: implementationKindWire,
                ScriptId: document.ImplementationScriptId,
                ScriptRevision: string.IsNullOrEmpty(document.ImplementationScriptRevision)
                    ? null
                    : document.ImplementationScriptRevision);
        }

        if (!string.IsNullOrEmpty(document.ImplementationActorTypeName))
        {
            return new StudioMemberImplementationRefResponse(
                ImplementationKind: implementationKindWire,
                ActorTypeName: document.ImplementationActorTypeName);
        }

        return null;
    }

    private static StudioMemberBindingContractResponse? ToLastBindingResponse(
        StudioMemberCurrentStateDocument document)
    {
        if (string.IsNullOrEmpty(document.LastBoundPublishedServiceId))
            return null;

        return new StudioMemberBindingContractResponse(
            PublishedServiceId: document.LastBoundPublishedServiceId,
            RevisionId: document.LastBoundRevisionId,
            ImplementationKind: NormalizeImplementationKindWire(document.LastBoundImplementationKind),
            BoundAt: document.LastBoundAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);
    }

    private static string NormalizeImplementationKindWire(string? wire) => wire switch
    {
        MemberImplementationKindNames.Workflow => MemberImplementationKindNames.Workflow,
        MemberImplementationKindNames.Script => MemberImplementationKindNames.Script,
        MemberImplementationKindNames.GAgent => MemberImplementationKindNames.GAgent,
        _ => string.Empty,
    };

    private static string NormalizeLifecycleStageWire(string? wire) => wire switch
    {
        MemberLifecycleStageNames.Created => MemberLifecycleStageNames.Created,
        MemberLifecycleStageNames.BuildReady => MemberLifecycleStageNames.BuildReady,
        MemberLifecycleStageNames.BindReady => MemberLifecycleStageNames.BindReady,
        _ => string.Empty,
    };
}
