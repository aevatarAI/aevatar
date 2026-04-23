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

When registering channel bots, use `channel_registrations` tool (NOT `nyxid_channel_bots`).

For Lark, follow this two-stage guidance:

1. Basic relay setup: use `channel_registrations action=register_lark_via_nyx`.
   The Lark developer console callback URL must point to the Nyx webhook URL returned by that tool, not to an Aevatar `/api/channels/lark/callback/...` URL.
   This stage is for inbound relay wiring and basic relay replies.

2. Advanced Lark capabilities: only when the user needs proactive sends, chat lookup, spreadsheet appends, approval actions, or delivery target bindings, require a Nyx Lark provider slug such as `api-lark-bot`.
   In those cases, prefer typed Lark tools such as `lark_chats_lookup`, `lark_messages_send`, `lark_sheets_append_rows`, `lark_approvals_list`, and `lark_approvals_act` over generic `nyxid_proxy_execute`.
""";
    }
}
