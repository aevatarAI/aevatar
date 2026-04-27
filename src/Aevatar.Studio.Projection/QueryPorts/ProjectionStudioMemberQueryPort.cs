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
/// </summary>
public sealed class ProjectionStudioMemberQueryPort : IStudioMemberQueryPort
{
    private const int DefaultRosterPageSize = 200;

    private readonly IProjectionDocumentReader<StudioMemberCurrentStateDocument, string> _documentReader;

    public ProjectionStudioMemberQueryPort(
        IProjectionDocumentReader<StudioMemberCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<StudioMemberRosterResponse> ListAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioMemberConventions.NormalizeScopeId(scopeId);

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
            Take = DefaultRosterPageSize,
        };

        var result = await _documentReader.QueryAsync(query, ct);
        var summaries = result.Items
            .Where(item => string.Equals(item.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            .Select(ToSummary)
            .ToList();

        return new StudioMemberRosterResponse(
            ScopeId: normalizedScopeId,
            Members: summaries);
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
        var implementationKind = (StudioMemberImplementationKind)document.ImplementationKind;
        var lifecycleStage = (StudioMemberLifecycleStage)document.LifecycleStage;
        return new StudioMemberSummaryResponse(
            MemberId: document.MemberId,
            ScopeId: document.ScopeId,
            DisplayName: document.DisplayName,
            Description: document.Description,
            ImplementationKind: MemberImplementationKindMapper.ToWireName(implementationKind),
            LifecycleStage: MemberImplementationKindMapper.ToWireName(lifecycleStage),
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
        StudioMemberImplementationRefResponse? implementationRef = null;
        StudioMemberBindingContractResponse? lastBinding = null;

        if (document.StateRoot != null && document.StateRoot.Is(StudioMemberState.Descriptor))
        {
            var state = document.StateRoot.Unpack<StudioMemberState>();
            implementationRef = ToImplementationRefResponse(state.ImplementationRef, summary.ImplementationKind);
            if (state.LastBinding != null && !string.IsNullOrEmpty(state.LastBinding.PublishedServiceId))
            {
                lastBinding = new StudioMemberBindingContractResponse(
                    PublishedServiceId: state.LastBinding.PublishedServiceId,
                    RevisionId: state.LastBinding.RevisionId,
                    ImplementationKind: MemberImplementationKindMapper.ToWireName(
                        state.LastBinding.ImplementationKind),
                    BoundAt: state.LastBinding.BoundAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);
            }
        }

        return new StudioMemberDetailResponse(summary, implementationRef, lastBinding);
    }

    private static StudioMemberImplementationRefResponse? ToImplementationRefResponse(
        StudioMemberImplementationRef? implementationRef,
        string implementationKindWire)
    {
        if (implementationRef == null)
            return null;

        if (implementationRef.Workflow != null && !string.IsNullOrEmpty(implementationRef.Workflow.WorkflowId))
        {
            return new StudioMemberImplementationRefResponse(
                ImplementationKind: implementationKindWire,
                WorkflowId: implementationRef.Workflow.WorkflowId,
                WorkflowRevision: string.IsNullOrEmpty(implementationRef.Workflow.WorkflowRevision)
                    ? null
                    : implementationRef.Workflow.WorkflowRevision);
        }

        if (implementationRef.Script != null && !string.IsNullOrEmpty(implementationRef.Script.ScriptId))
        {
            return new StudioMemberImplementationRefResponse(
                ImplementationKind: implementationKindWire,
                ScriptId: implementationRef.Script.ScriptId,
                ScriptRevision: string.IsNullOrEmpty(implementationRef.Script.ScriptRevision)
                    ? null
                    : implementationRef.Script.ScriptRevision);
        }

        if (implementationRef.Gagent != null && !string.IsNullOrEmpty(implementationRef.Gagent.ActorTypeName))
        {
            return new StudioMemberImplementationRefResponse(
                ImplementationKind: implementationKindWire,
                ActorTypeName: implementationRef.Gagent.ActorTypeName);
        }

        return null;
    }
}
