using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Queries;

public sealed record ServiceEndpointCatalogSnapshot(
    string ServiceKey,
    IReadOnlyList<ServiceEndpointExposureSnapshot> Endpoints,
    DateTimeOffset UpdatedAt);

public sealed record ServiceEndpointExposureSnapshot(
    string EndpointId,
    string DisplayName,
    ServiceEndpointKind Kind,
    string RequestTypeUrl,
    string ResponseTypeUrl,
    string Description,
    ServiceEndpointExposureKind ExposureKind,
    IReadOnlyList<string> PolicyIds);
