namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Ornn 技能平台配置。</summary>
public sealed class OrnnOptions
{
    public const string DefaultNyxIdSlug = "ornn-api";

    /// <summary>
    /// NyxID-bound service slug used to reach the Ornn skill API. Default <c>"ornn-api"</c>
    /// matches the slug under which the chrono-ornn HTTP service is currently registered in
    /// the production NyxID catalog (verified via <c>nyxid service list</c>). The bare
    /// <c>"ornn"</c> slug is the SPA frontend that only serves HTML, not the API.
    /// All requests route through NyxID's proxy:
    /// <c>{NyxID}/api/v1/proxy/s/{slug}/api/v1/...</c>
    /// Deployments using a non-default registration name should set
    /// <c>Aevatar:Ornn:NyxIdSlug</c> in configuration.
    /// </summary>
    public string NyxIdSlug { get; set; } = DefaultNyxIdSlug;

    /// <summary>
    /// Per-call timeout used for Ornn read, validation, and publish requests through NyxID.
    /// </summary>
    public TimeSpan PerCallTimeout { get; set; } = OrnnSkillClient.DefaultPerCallTimeout;
}
