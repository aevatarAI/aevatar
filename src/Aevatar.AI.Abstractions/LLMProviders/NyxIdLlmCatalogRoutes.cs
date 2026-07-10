namespace Aevatar.AI.Abstractions.LLMProviders;

public static class NyxIdLlmCatalogRoutes
{
    public const string ProxyServicesPath = "/api/v1/proxy/services?per_page=100";

    /// <summary>NyxID unified key list - the per-user credential bindings (AI Services).</summary>
    public const string UserKeysPath = "/api/v1/keys";
}
