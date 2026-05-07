namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Ornn 技能平台配置。</summary>
public sealed class OrnnOptions
{
    /// <summary>
    /// NyxID-bound service slug used to reach the Ornn skill API. Default <c>"ornn"</c>
    /// matches chrono-ornn's published catalog entry (<c>category=internal</c>,
    /// <c>requires_credential=false</c>) — see chrono-ornn's <c>ornn-core-skills/*/SKILL.md</c>.
    /// All requests route through NyxID's proxy: <c>{NyxID}/api/v1/proxy/s/{slug}/api/web/...</c>
    /// so deployments override this only if their NyxID catalog uses a different slug name.
    /// </summary>
    public string NyxIdSlug { get; set; } = "ornn";
}
