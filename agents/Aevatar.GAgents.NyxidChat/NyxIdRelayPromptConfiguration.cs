namespace Aevatar.GAgents.NyxidChat;

public static class NyxIdRelayPromptConfiguration
{
    public const string RelayCallbackPath = "/api/webhooks/nyxid-relay";
    private const string UnconfiguredCallback = "[nyx relay webhook base URL is not configured in this host]";

    public static string ResolveRelayCallbackUrl(NyxIdRelayOptions? options)
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

    public static string BuildChannelRuntimeConfigurationSection(NyxIdRelayOptions? options)
    {
        var relayCallbackUrl = ResolveRelayCallbackUrl(options);
        return $"""

## Channel Runtime Configuration (Auto-Injected)

Aevatar's Nyx relay callback URL is: `{relayCallbackUrl}`

For new Aevatar-managed Lark relay provisioning, use `channel_registrations`.
For existing-bot inspection or repair, use `nyxid_channel_bots` and `nyxid_api_keys` to inspect Nyx state. If the authoritative Aevatar actor still exists but the read model is stale, use `channel_registrations action=rebuild_projection`. If Nyx resources exist but the local Aevatar mirror is missing, use `channel_registrations action=repair_lark_mirror`.

For Lark, follow this guidance:

1. Basic relay setup: use `channel_registrations action=register_lark_via_nyx`.
   If the user has a Lark verification token or the backend requires it, pass `verification_token=<token>` through the tool call.
   The Lark developer console callback URL must point to the Nyx webhook URL returned by that tool, not to an Aevatar `/api/channels/lark/callback/...` URL.
   This stage is for inbound relay wiring and basic relay replies.

2. Existing-bot repair: if Nyx already has the Lark bot and route but `channel_registrations action=list` is empty or Aevatar is silent, first call `channel_registrations action=rebuild_projection`. If the local list is still empty, inspect the Nyx bot via `nyxid_channel_bots action=show`, inspect routes via `nyxid_channel_bots action=routes`, inspect the relay API key callback via `nyxid_api_keys action=show`, then call `channel_registrations action=repair_lark_mirror webhook_base_url=<this host base URL>`. Preserve the existing relay credential reference by reusing the old `registration_id` when its secret still exists, or by passing `credential_ref=<existing vault ref>` explicitly.

3. Advanced Lark capabilities: only when the user needs proactive sends, chat lookup, spreadsheet appends, approval actions, or delivery target bindings, require a Nyx Lark provider slug such as `api-lark-bot`.
   In those cases, prefer typed Lark tools such as `lark_messages_send`, `lark_messages_reply`, `lark_messages_search`, `lark_messages_batch_get`, `lark_messages_react`, `lark_messages_reactions_list`, `lark_messages_reactions_delete`, `lark_chats_lookup`, `lark_sheets_append_rows`, `lark_approvals_list`, and `lark_approvals_act` over generic `nyxid_proxy_execute`.

For inbound Lark relay turns that represent a fresh user message (not a card action), if the typed tool `lark_messages_react` is available and metadata exposes `channel.platform_message_id`, call `lark_messages_react` first with an acknowledgment emoji such as `OK`, then continue with the substantive reply.
""";
    }
}
