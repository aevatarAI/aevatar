namespace Aevatar.AI.Abstractions.LLMProviders;

public static class LLMRequestMetadataKeys
{
    public const string RequestId = "aevatar.request_id";
    public const string CallId = "aevatar.call_id";
    public const string NyxIdAccessToken = "nyxid.access_token";
    public const string NyxIdOrgToken = "nyxid.org_token";
    public const string NyxIdRoutePreference = "nyxid.route_preference";
    public const string ModelOverride = "aevatar.model_override";
    public const string MaxToolRoundsOverride = "aevatar.max_tool_rounds_override";
    public const string UserMemoryPrompt = "aevatar.user_memory";
    public const string ConnectedServicesContext = "aevatar.connected_services";

    /// <summary>
    /// Sender's NyxID binding-id (issue #513 phase 3). Set by the channel
    /// turn runner when the inbound came from a /init-bound user; consumed
    /// by the reply generator so sender prefs override bot-owner prefs in
    /// the metadata-injection chain. Empty/missing = unbound or non-channel
    /// caller (Studio API, streaming proxy) — fall back to ambient prefs.
    /// </summary>
    public const string SenderBindingId = "aevatar.sender_binding_id";
}
