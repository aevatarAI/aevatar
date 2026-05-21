namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Abstraction for fetching OpenAPI specs of connected services.
/// </summary>
public interface IConnectedServiceSpecSource
{
    // Refactor (iter25/cluster-025-nyxid-tool-discovery-actor-cache):
    //   Old pattern: NyxIdSpecCatalog + SpecFetchToken + IServiceDiscoveryCache 在仓库内建第二 catalog(NyxID 真实源的影子)
    //   New principle: NyxID 是唯一真实源;删除 in-process catalog 假权威面; routing 和 spec hints 请求时读取 live NyxID surface;保留 typed tools + live nyxid_proxy
    Task<OperationCard[]?> GetOrFetchAsync(
        string slug,
        string? serviceId,
        string? specUrl,
        string accessToken,
        CancellationToken ct = default);
}
