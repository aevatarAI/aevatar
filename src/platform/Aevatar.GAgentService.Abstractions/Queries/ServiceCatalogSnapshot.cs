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
    ServiceExternalExposureSnapshot? ExternalExposure = null)
{
    /// <summary>
    /// Committed source version of the service-catalog actor replica.
    /// Query consumers use this watermark as evidence; they must not invent a
    /// local projection counter when the producer has not exposed one.
    /// </summary>
    public long StateVersion { get; init; }

    public string LastEventId { get; init; } = string.Empty;
}

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
