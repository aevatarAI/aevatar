namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Abstraction for fetching and caching OpenAPI specs of connected services.
/// Implementations can be in-memory (dev/single-node) or distributed (production).
/// </summary>
public interface IConnectedServiceSpecSource
{
    Task<OperationCard[]?> GetOrFetchAsync(
        string slug,
        string? serviceId,
        string? specUrl,
        string accessToken,
        CancellationToken ct = default);
}
