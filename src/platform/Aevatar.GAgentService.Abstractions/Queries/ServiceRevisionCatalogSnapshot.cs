namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceRevisionCatalogSnapshot(
    string ServiceKey,
    IReadOnlyList<ServiceRevisionSnapshot> Revisions,
    DateTimeOffset UpdatedAt);

public sealed record ServiceRevisionSnapshot(
    string RevisionId,
    string ImplementationKind,
    string Status,
    string ArtifactHash,
    string FailureReason,
    IReadOnlyList<ServiceEndpointSnapshot> Endpoints,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? PreparedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? RetiredAt);
