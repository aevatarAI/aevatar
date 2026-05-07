namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Ornn 技能平台配置。</summary>
public sealed class OrnnOptions
{
    /// <summary>
    /// Ornn API 基础地址。默认指向生产平台，部署可通过 `Ornn:BaseUrl` 配置覆盖。
    /// 不再依赖 helm 显式注入，避免 issue #530 中 `ornn_search_skills` 因配置缺失静默缺席。
    /// </summary>
    public string BaseUrl { get; set; } = "https://ornn.chrono-ai.fun";
}
