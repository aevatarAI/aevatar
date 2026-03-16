namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceDeploymentCatalogSnapshot(
    string ServiceKey,
    IReadOnlyList<ServiceDeploymentSnapshot> Deployments,
    DateTimeOffset UpdatedAt);

public sealed record ServiceDeploymentSnapshot(
    string DeploymentId,
    string RevisionId,
    string PrimaryActorId,
    string Status,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset UpdatedAt);
