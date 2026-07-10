using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.GAgents.StudioTeam;
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
    // Refactor (iter74/cluster-074-studio-team-members-query-fanout):
    //   Old pattern: Host loops scope roster pages + Host-side TeamId filter
    //   New principle: ReadModel query port owns scope_id+team_id filter before pagination
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

        var filters = new List<ProjectionDocumentFilter>
        {
            new()
            {
                FieldPath = "scope_id",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(normalizedScopeId),
            },
        };

        var normalizedTeamId = string.IsNullOrWhiteSpace(page?.TeamId)
            ? null
            : StudioTeamConventions.NormalizeTeamId(page.TeamId);
        if (normalizedTeamId != null)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = "team_id",
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(normalizedTeamId),
            });
        }

        var query = new ProjectionDocumentQuery
        {
            Filters = filters,
            Take = requestedPageSize,
            Cursor = string.IsNullOrWhiteSpace(page?.PageToken) ? null : page!.PageToken,
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
            UpdatedAt: document.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)
        {
            // Optional team assignment from ADR-0017. Mirror the document's
            // proto3 optional `team_id` semantics — absence on the wire means
            // "unassigned" on the application contract.
            TeamId = document.HasTeamId ? document.TeamId : null,
            ImplementationRef = ToImplementationRefResponse(
                document,
                NormalizeImplementationKindWire(document.ImplementationKind)),
        };
    }

    private static StudioMemberDetailResponse ToDetail(StudioMemberCurrentStateDocument document)
    {
        var summary = ToSummary(document);
        var lastBinding = ToLastBindingResponse(document);
        return new StudioMemberDetailResponse(summary, summary.ImplementationRef, lastBinding)
        {
            CurrentBindingRun = ToBindingRunStatusResponse(document),
        };
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
                DiagnosticActorTypeName: document.ImplementationActorTypeName);
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
            BoundAt: document.LastBoundAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            ExpectedActorId: string.IsNullOrEmpty(document.LastBoundExpectedActorId)
                ? null
                : document.LastBoundExpectedActorId);
    }

    private static StudioMemberBindingRunStatusResponse? ToBindingRunStatusResponse(
        StudioMemberCurrentStateDocument document)
    {
        if (string.IsNullOrEmpty(document.BindingCurrentRunId))
            return null;

        // Refactor (iter159/cluster-594-first):
        //   Old pattern: Studio member binding-run status response 未暴露 StateVersion
        //   New principle: 暴露 readmodel 已有的 StateVersion; 前端用 freshness marker 诚实表达 not-yet-materialized 状态
        StudioMemberBindingFailureResponse? failure = null;
        if (!string.IsNullOrEmpty(document.BindingFailureCode))
        {
            failure = new StudioMemberBindingFailureResponse(
                Code: document.BindingFailureCode,
                Message: document.BindingFailureMessage,
                FailedAt: document.BindingFailureAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);
        }

        return new StudioMemberBindingRunStatusResponse(
            BindingRunId: document.BindingCurrentRunId,
            ScopeId: document.ScopeId,
            MemberId: document.MemberId,
            Status: NormalizeBindingRunStatusWire(document.BindingCurrentStatus),
            StateVersion: document.StateVersion,
            Failure: failure,
            UpdatedAt: document.BindingUpdatedAt?.ToDateTimeOffset())
        {
            Result = ToBindingResultResponse(document),
        };
    }

    private static StudioMemberBindingRunResultResponse? ToBindingResultResponse(
        StudioMemberCurrentStateDocument document)
    {
        if (string.IsNullOrEmpty(document.LastBoundPublishedServiceId)
            || string.IsNullOrEmpty(document.LastBoundRevisionId)
            || string.IsNullOrEmpty(document.LastBoundImplementationKind))
        {
            return null;
        }

        return new StudioMemberBindingRunResultResponse(
            PublishedServiceId: document.LastBoundPublishedServiceId,
            RevisionId: document.LastBoundRevisionId,
            ImplementationKind: document.LastBoundImplementationKind,
            ExpectedActorId: string.IsNullOrEmpty(document.LastBoundExpectedActorId)
                ? null
                : document.LastBoundExpectedActorId);
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

    private static string NormalizeBindingRunStatusWire(string? wire) => wire switch
    {
        StudioMemberBindingRunStatusNames.Accepted => StudioMemberBindingRunStatusNames.Accepted,
        StudioMemberBindingRunStatusNames.AdmissionPending => StudioMemberBindingRunStatusNames.AdmissionPending,
        StudioMemberBindingRunStatusNames.Admitted => StudioMemberBindingRunStatusNames.Admitted,
        StudioMemberBindingRunStatusNames.PlatformBindingPending => StudioMemberBindingRunStatusNames.PlatformBindingPending,
        StudioMemberBindingRunStatusNames.MemberNotificationPending => StudioMemberBindingRunStatusNames.MemberNotificationPending,
        StudioMemberBindingRunStatusNames.Succeeded => StudioMemberBindingRunStatusNames.Succeeded,
        StudioMemberBindingRunStatusNames.Failed => StudioMemberBindingRunStatusNames.Failed,
        StudioMemberBindingRunStatusNames.Rejected => StudioMemberBindingRunStatusNames.Rejected,
        _ => string.Empty,
    };
}
