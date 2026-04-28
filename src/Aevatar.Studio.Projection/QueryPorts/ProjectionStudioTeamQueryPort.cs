using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.StudioTeam;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.QueryPorts;

/// <summary>
/// Reads StudioTeam roster and detail from the projection document store
/// (ADR-0017). Pure query semantics — never replays events, never calls the
/// actor runtime. Roster scans are constrained to the requested
/// <c>scope_id</c>.
/// </summary>
public sealed class ProjectionStudioTeamQueryPort : IStudioTeamQueryPort
{
    public const int MaxRosterPageSize = 200;

    private readonly IProjectionDocumentReader<StudioTeamCurrentStateDocument, string> _documentReader;

    public ProjectionStudioTeamQueryPort(
        IProjectionDocumentReader<StudioTeamCurrentStateDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<StudioTeamRosterResponse> ListAsync(
        string scopeId,
        StudioTeamRosterPageRequest? page = null,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioTeamConventions.NormalizeScopeId(scopeId);
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
            Cursor = string.IsNullOrWhiteSpace(page?.PageToken) ? null : page!.PageToken,
        };

        var result = await _documentReader.QueryAsync(query, ct);
        var summaries = result.Items
            .Where(item => string.Equals(item.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            .Select(ToSummary)
            .ToList();

        return new StudioTeamRosterResponse(
            ScopeId: normalizedScopeId,
            Teams: summaries,
            NextPageToken: string.IsNullOrWhiteSpace(result.NextCursor) ? null : result.NextCursor);
    }

    public async Task<StudioTeamSummaryResponse?> GetAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioTeamConventions.NormalizeScopeId(scopeId);
        var normalizedTeamId = StudioTeamConventions.NormalizeTeamId(teamId);
        var actorId = StudioTeamConventions.BuildActorId(normalizedScopeId, normalizedTeamId);

        var document = await _documentReader.GetAsync(actorId, ct);
        if (document == null)
            return null;

        if (!string.Equals(document.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            return null;

        return ToSummary(document);
    }

    private static StudioTeamSummaryResponse ToSummary(StudioTeamCurrentStateDocument document)
    {
        return new StudioTeamSummaryResponse(
            TeamId: document.TeamId,
            ScopeId: document.ScopeId,
            DisplayName: document.DisplayName,
            Description: document.Description,
            LifecycleStage: NormalizeLifecycleStageWire(document.LifecycleStage),
            MemberCount: document.MemberCount,
            CreatedAt: document.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
            UpdatedAt: document.UpdatedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);
    }

    private static string NormalizeLifecycleStageWire(string? wire) => wire switch
    {
        TeamLifecycleStageNames.Active => TeamLifecycleStageNames.Active,
        TeamLifecycleStageNames.Archived => TeamLifecycleStageNames.Archived,
        _ => string.Empty,
    };
}
