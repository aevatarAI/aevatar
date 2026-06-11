namespace Aevatar.GAgents.NyxidChat;

public static class NyxIdRelayPromptConfiguration
{
    public const string RelayCallbackPath = "/api/webhooks/nyxid-relay";
    private const string UnconfiguredCallback = "[nyx relay webhook base URL is not configured in this host]";

    public static string ResolveRelayCallbackUrl(global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? options)
    {
        return ResolveRelayCallbackUrl(options?.WebhookBaseUrl);
    }

    public static string ResolveRelayCallbackUrl(string? webhookBaseUrl)
    {
        var baseUrl = webhookBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return UnconfiguredCallback;

        return $"{baseUrl.TrimEnd('/')}{RelayCallbackPath}";
    }

    public static string BuildChannelRuntimeConfigurationSection(global::Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions? options)
    {
        // Refactor (iter25/cluster-025-nyxid-tool-discovery-actor-cache):
        //   Old pattern: NyxID relay prompt steered agents toward deleted generic capability-search/proxy-execute tools backed by Aevatar-side catalog state.
        //   New principle: prompt guidance names durable typed tools and uses live nyxid_proxy only for explicit downstream proxy calls.
        var relayCallbackUrl = ResolveRelayCallbackUrl(options);
        return $"""

## Channel Runtime Configuration (Auto-Injected)

Aevatar's Nyx relay callback URL is: `{relayCallbackUrl}`

For new Aevatar-managed Lark relay provisioning, use `channel_registrations`.
For existing-bot inspection, use `nyxid_channel_bots` and `nyxid_api_keys` to inspect Nyx state. If the local Aevatar mirror is missing, provision through `channel_registrations action=register_lark_via_nyx`.

For Lark, follow this guidance:

1. Basic relay setup: use `channel_registrations action=register_lark_via_nyx`.
   If the user has a Lark verification token or the backend requires it, pass `verification_token=<token>` through the tool call.
   The Lark developer console callback URL must point to the Nyx webhook URL returned by that tool.
   This stage is for inbound relay wiring and basic relay replies.

2. Existing-bot inspection: if Nyx already has the Lark bot and route but `channel_registrations action=list` is empty or Aevatar is silent, inspect the Nyx bot via `nyxid_channel_bots action=show`, inspect routes via `nyxid_channel_bots action=routes`, inspect the relay API key callback via `nyxid_api_keys action=show`, then provision through `channel_registrations action=register_lark_via_nyx`.

3. Advanced Lark capabilities: only when the user needs proactive sends, chat lookup, spreadsheet appends, approval actions, or delivery target bindings, require a Nyx Lark provider slug such as `api-lark-bot`.
   In those cases, prefer typed Lark tools such as `lark_messages_send`, `lark_messages_batch_get`, `lark_messages_reactions_list`, `lark_messages_reactions_delete`, `lark_chats_lookup`, `lark_sheets_append_rows`, `lark_approvals_list`, and `lark_approvals_act`.
   Only call `lark_messages_reply` or `lark_messages_react` when the user explicitly asks you to reply to or react to a specific Lark message outside the current relay turn.

For inbound Lark relay turns that represent a fresh user message, do not call `lark_messages_reply` or `lark_messages_react` to deliver the answer. Produce the final text reply directly; the channel runtime will send it through the Nyx relay reply token.
""";
    }
}
