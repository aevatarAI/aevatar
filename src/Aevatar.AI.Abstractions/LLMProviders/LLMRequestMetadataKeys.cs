namespace Aevatar.AI.Abstractions.LLMProviders;

public static class LLMRequestMetadataKeys
{
    public const string RequestId = "aevatar.request_id";
    public const string CallId = "aevatar.call_id";
    public const string NyxIdAccessToken = "nyxid.access_token";
    public const string NyxIdRefreshToken = "nyxid.refresh_token";
    public const string NyxIdOrgToken = "nyxid.org_token";
    public const string NyxIdRoutePreference = "nyxid.route_preference";
    public const string ModelOverride = "aevatar.model_override";
    public const string MaxToolRoundsOverride = "aevatar.max_tool_rounds_override";
    public const string UserMemoryPrompt = "aevatar.user_memory";
    public const string ConnectedServicesContext = "aevatar.connected_services";
}
