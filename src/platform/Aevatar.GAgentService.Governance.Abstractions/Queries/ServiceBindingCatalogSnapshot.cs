using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions;

namespace Aevatar.GAgentService.Governance.Abstractions.Queries;

public sealed record ServiceBindingCatalogSnapshot(
    string ServiceKey,
    IReadOnlyList<ServiceBindingSnapshot> Bindings,
    DateTimeOffset UpdatedAt);

public sealed record BoundServiceReferenceSnapshot(
    ServiceIdentity Identity,
    string EndpointId);

public sealed record BoundConnectorReferenceSnapshot(
    string ConnectorType,
    string ConnectorId);

public sealed record BoundSecretReferenceSnapshot(
    string SecretName);

public sealed record ServiceBindingSnapshot(
    string BindingId,
    string DisplayName,
    ServiceBindingKind BindingKind,
    IReadOnlyList<string> PolicyIds,
    bool Retired,
    BoundServiceReferenceSnapshot? ServiceRef,
    BoundConnectorReferenceSnapshot? ConnectorRef,
    BoundSecretReferenceSnapshot? SecretRef);
