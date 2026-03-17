using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Queries;

public sealed record ServiceConfigurationSnapshot(
    string ServiceKey,
    ServiceIdentity Identity,
    IReadOnlyList<ServiceBindingSnapshot> Bindings,
    IReadOnlyList<ServiceEndpointExposureSnapshot> Endpoints,
    IReadOnlyList<ServicePolicySnapshot> Policies,
    DateTimeOffset UpdatedAt);
