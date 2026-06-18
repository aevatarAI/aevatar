namespace Aevatar.AI.Abstractions.LLMProviders;

/// <summary>
/// Single source of truth for the deployment-wide default LLM route + model, applied whenever no
/// per-request, per-provider, or host-config override is present. Both the NyxID provider
/// server-default path (Aevatar.Bootstrap.Extensions.AI) and the OpenAI-compatible Responses
/// ingress default (Aevatar.GAgentService.Application) resolve their fallback from here so the
/// value cannot drift across subsystems. Per-deployment overrides: Aevatar:NyxId:DefaultRoute /
/// Aevatar:NyxId:DefaultModel and Aevatar:Responses:Ingress:DefaultModel.
/// </summary>
public static class LlmDefaults
{
    /// <summary>Default model when no override is set.</summary>
    public const string Model = "gpt-5.5";

    /// <summary>
    /// Default NyxID route (proxy slug) when no route override is set. The bare NyxID gateway
    /// forwards to OpenAI, which fails closed when that provider is not connected; this public
    /// chrono-llm route is the connected default.
    /// </summary>
    public const string NyxIdRoute = "chrono-llm-public";

    /// <summary>
    /// Combined <c>route/model</c> shape consumed by the OpenAI-compatible Responses ingress
    /// default (e.g. <c>chrono-llm-public/gpt-5.5</c>).
    /// </summary>
    public const string NyxIdRouteModel = NyxIdRoute + "/" + Model;
}
