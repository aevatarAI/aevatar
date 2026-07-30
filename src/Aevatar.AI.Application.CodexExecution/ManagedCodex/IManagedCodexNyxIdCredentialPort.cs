namespace Aevatar.AI.Application.CodexExecution;

public sealed record ManagedCodexNyxIdService(
    string Id,
    string Slug,
    bool IsActive,
    string CredentialSourceType,
    bool? CredentialSourceAllowed,
    bool? ForwardAccessToken,
    bool? InjectDelegationToken,
    string? DelegationTokenScope);

public sealed record ManagedCodexNyxIdApiKey(
    string Id,
    string Name,
    string Scopes,
    string Platform,
    bool IsActive,
    bool AllowAllServices,
    IReadOnlyList<string> AllowedServiceIds,
    bool AllowAllNodes,
    IReadOnlyList<string> AllowedNodeIds,
    DateTimeOffset? ExpiresAt);

public sealed record ManagedCodexNyxIdApiKeyIssueRequest(
    string Name,
    string Description,
    string Scopes,
    string Platform,
    bool AllowAllServices,
    IReadOnlyList<string> AllowedServiceIds,
    bool AllowAllNodes,
    IReadOnlyList<string> AllowedNodeIds,
    DateTimeOffset ExpiresAt);

public sealed record ManagedCodexNyxIdApiKeyPolicyUpdateRequest(
    string Scopes,
    string Platform,
    bool AllowAllServices,
    IReadOnlyList<string> AllowedServiceIds,
    bool AllowAllNodes,
    IReadOnlyList<string> AllowedNodeIds);

public sealed record ManagedCodexNyxIdIssuedApiKey(
    ManagedCodexNyxIdApiKey Key,
    ManagedCodexOpaqueSecret Secret);

public interface IManagedCodexNyxIdCredentialPort
{
    Task<string> GetCurrentUserIdAsync(
        string bearerToken,
        CancellationToken ct = default);

    Task<IReadOnlyList<ManagedCodexNyxIdService>> ListUserServicesAsync(
        string bearerToken,
        CancellationToken ct = default);

    Task<IReadOnlyList<ManagedCodexNyxIdApiKey>> ListApiKeysAsync(
        string bearerToken,
        CancellationToken ct = default);

    Task<ManagedCodexNyxIdIssuedApiKey> CreateApiKeyAsync(
        string bearerToken,
        ManagedCodexNyxIdApiKeyIssueRequest request,
        CancellationToken ct = default);

    Task UpdateApiKeyPolicyAsync(
        string bearerToken,
        string apiKeyId,
        ManagedCodexNyxIdApiKeyPolicyUpdateRequest request,
        CancellationToken ct = default);

    Task<ManagedCodexNyxIdIssuedApiKey> RotateApiKeyAsync(
        string bearerToken,
        string apiKeyId,
        CancellationToken ct = default);

    Task<bool> RevokeApiKeyAsync(
        string bearerToken,
        string apiKeyId,
        CancellationToken ct = default);
}
