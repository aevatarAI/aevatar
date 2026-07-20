namespace Aevatar.Studio.Application.Provisioning;

public sealed record StudioMemberProvisioningRequest(
    string ScopeId,
    string DisplayName,
    string ImplementationKind)
{
    public string? Description { get; init; }
    public string? MemberId { get; init; }
    public string? TeamId { get; init; }
}

public sealed record StudioMemberProvisioningResult(
    bool Success,
    string ScopeId,
    string MemberId,
    string DisplayName,
    string Description,
    string ImplementationKind,
    string LifecycleStage,
    string PublishedServiceId,
    string? LastBoundRevisionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string? TeamId { get; init; }
}
