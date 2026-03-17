namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ServiceCatalogSnapshot(
    string ServiceKey,
    string TenantId,
    string AppId,
    string Namespace,
    string ServiceId,
    string DisplayName,
    string DefaultServingRevisionId,
    string ActiveServingRevisionId,
    string DeploymentId,
    string PrimaryActorId,
    string DeploymentStatus,
    IReadOnlyList<ServiceEndpointSnapshot> Endpoints,
    IReadOnlyList<string> PolicyIds,
    DateTimeOffset UpdatedAt);

public sealed record ServiceEndpointSnapshot(
    string EndpointId,
    string DisplayName,
    string Kind,
    string RequestTypeUrl,
    string ResponseTypeUrl,
    string Description);
