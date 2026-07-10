namespace Aevatar.Studio.Application.Provisioning;

/// <summary>
/// Request to create a Studio team through a local agent tool.
/// The caller scope is not model-controlled: the tool reads it from
/// AgentToolRequestContext and passes it through this narrow port.
/// </summary>
public sealed record StudioTeamProvisioningRequest(
    string ScopeId,
    string DisplayName)
{
    public string? Description { get; init; }

    public string? TeamId { get; init; }
}

/// <summary>
/// Result returned by the local Studio team create capability.
/// </summary>
public sealed record StudioTeamProvisioningResult(
    bool Success,
    string ScopeId,
    string TeamId,
    string DisplayName,
    string Description,
    string LifecycleStage,
    int MemberCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string? EntryMemberId { get; init; }
}
