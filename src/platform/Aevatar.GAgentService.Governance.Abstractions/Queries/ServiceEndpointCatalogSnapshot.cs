namespace Aevatar.GAgentService.Governance.Abstractions.Queries;

public sealed record ServiceEndpointCatalogSnapshot(
    string ServiceKey,
    IReadOnlyList<ServiceEndpointExposureSnapshot> Endpoints,
    DateTimeOffset UpdatedAt);

public sealed record ServiceEndpointExposureSnapshot(
    string EndpointId,
    string DisplayName,
    string Kind,
    string RequestTypeUrl,
    string ResponseTypeUrl,
    string Description,
    string ExposureKind,
    IReadOnlyList<string> PolicyIds);
