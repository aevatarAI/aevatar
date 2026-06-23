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
    DateTimeOffset UpdatedAt,
    ServiceExternalExposureSnapshot? ExternalExposure = null);

public sealed record ServiceEndpointSnapshot(
    string EndpointId,
    string DisplayName,
    string Kind,
    string RequestTypeUrl,
    string ResponseTypeUrl,
    string Description);

public sealed record ServiceExternalExposureSnapshot(
    string NyxidSlug,
    DateTimeOffset? RegisteredAt,
    ServiceRegistrationStatus Status = ServiceRegistrationStatus.Unspecified,
    string NyxidServiceId = "",
    string DesiredSpecHash = "",
    string RegisteredSpecHash = "",
    string LastError = "",
    int Attempt = 0,
    DateTimeOffset? NextAttemptAt = null,
    string CredentialKid = "",
    bool ExposureDesired = false,
    long SourceStateVersion = 0);
