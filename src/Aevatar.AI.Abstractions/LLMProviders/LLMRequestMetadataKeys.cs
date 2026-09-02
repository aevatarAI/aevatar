namespace Aevatar.AI.Abstractions.LLMProviders;

public static class LLMRequestMetadataKeys
{
    // Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
    //   Old pattern: these owned keys were the tool/LLM control plane.
    //   New principle: constants remain for legacy mapper/boundary translation only.
    public const string RequestId = "aevatar.request_id";
    public const string CallId = "aevatar.call_id";
    public const string ScopeId = "aevatar.scope_id";
    public const string OwnerScopeId = "aevatar.owner_scope_id";
    public const string OwnerSubject = "aevatar.owner_subject";
    public const string ResponseId = "aevatar.response_id";
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
    public const string SenderNyxUserId = "aevatar.sender_nyx_user_id";

    /// <summary>
    /// Short-lived NyxID access token issued for <see cref="SenderBindingId"/>.
    /// Channel LLM turns use this only while attempting the sender's own
    /// configured LLM route. If that attempt fails or the key is missing, the
    /// request falls back to the bot owner's ambient <see cref="NyxIdAccessToken"/>
    /// without asking the sender to run <c>/init</c>.
    /// </summary>
    public const string SenderNyxIdAccessToken = "nyxid.sender_access_token";
}
