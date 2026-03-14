namespace Aevatar.GAgentService.Governance.Abstractions.Queries;

public sealed record ServiceBindingCatalogSnapshot(
    string ServiceKey,
    IReadOnlyList<ServiceBindingSnapshot> Bindings,
    DateTimeOffset UpdatedAt);

public sealed record ServiceBindingSnapshot(
    string BindingId,
    string DisplayName,
    string BindingKind,
    IReadOnlyList<string> PolicyIds,
    bool Retired,
    string? TargetServiceKey,
    string? TargetEndpointId,
    string? ConnectorType,
    string? ConnectorId,
    string? SecretName);
