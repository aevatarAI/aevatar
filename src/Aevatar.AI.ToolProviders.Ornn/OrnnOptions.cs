namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Ornn 技能平台配置。</summary>
public sealed class OrnnOptions
{
    /// <summary>
    /// NyxID-bound service slug used to reach the Ornn skill API. The Ornn skill backend is
    /// not directly reachable at the public frontend URL (which serves the SPA shell), so all
    /// requests go through NyxID's proxy: <c>{NyxID}/api/v1/proxy/s/{slug}/api/web/...</c>
    /// NyxID resolves the upstream backend address from the user's bound service.
    /// </summary>
    public string NyxIdSlug { get; set; } = "ornn-api";
}
