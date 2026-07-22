namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public sealed record AgentProfileCallerContext(
    AgentProfileUserOwnerIdentity Owner,
    string ScopeId,
    string? Username,
    string? NyxIdAccessToken);

public sealed record AgentProfileAcceptedReceipt(
    bool Accepted,
    string AckStage,
    string OperationId,
    string CommandId,
    string CorrelationId,
    string ActorId,
    string ProfileId,
    string ResourceUrl);

public interface IAgentProfileCommandService
{
    Task<AgentProfileAcceptedReceipt> CreateAsync(
        AgentProfileCallerContext caller,
        CreateAgentProfileRequest request,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<AgentProfileAcceptedReceipt> UpdateDraftAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        long expectedAuthorityStateVersion,
        UpdateAgentProfileDraftRequest request,
        string? idempotencyKey,
        CancellationToken ct = default);

    Task<AgentProfileAcceptedReceipt> UpsertSkillBindingAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        string bindingId,
        long expectedAuthorityStateVersion,
        UpsertAgentProfileSkillBindingRequest request,
        string? idempotencyKey,
        CancellationToken ct = default);

    Task<AgentProfileAcceptedReceipt> RemoveSkillBindingAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        string bindingId,
        long expectedAuthorityStateVersion,
        string? idempotencyKey,
        CancellationToken ct = default);

    Task<AgentProfileValidationReport> ValidateAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        CancellationToken ct = default);

    Task<AgentProfileAcceptedReceipt> PublishAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        long expectedAuthorityStateVersion,
        string? idempotencyKey,
        CancellationToken ct = default);
}

public interface IAgentProfileQueryService
{
    Task<AgentProfileManagementSnapshot?> GetOwnedAsync(
        AgentProfileCallerContext caller,
        string profileSlug,
        CancellationToken ct = default);

    Task<AgentProfileDiscoverySnapshot?> ResolveVisibleAsync(
        AgentProfileCallerContext caller,
        AgentProfileReference reference,
        CancellationToken ct = default);
}
