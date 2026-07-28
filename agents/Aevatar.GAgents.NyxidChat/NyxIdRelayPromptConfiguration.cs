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
        var relayCallbackUrl = ResolveRelayCallbackUrl(options);
        return $"""

## Channel Runtime Configuration (Auto-Injected)

Aevatar's Nyx relay callback URL is: `{relayCallbackUrl}`

Use the channel-management tools exposed in the current turn to provision or repair Aevatar-managed relay registrations. The relay registration mirror is local to Aevatar; the upstream channel provider owns platform bot state and webhook routes.

For new channel relay provisioning, create a registration for the requested platform and use the returned provider webhook details in the provider console. This stage is for inbound relay wiring and basic relay replies.

For existing-bot inspection, verify the upstream channel provider state first. If the upstream bot and route are healthy but Aevatar has no local registration, repair the local registration mirror through the available channel-management surface.

For provider-specific capabilities such as proactive sends, chat lookup, document updates, approval actions, or delivery target bindings, use only provider-specific typed tools, loaded skills, or connected-service entries that are present in the current turn. Keep each connected-service identity and route snapshot paired from the same trusted entry.

For inbound relay turns that represent a fresh user message, produce the final text reply directly. The channel runtime will deliver it through the relay reply token; do not call separate provider reply or reaction operations unless the user explicitly asks to act on another specific message outside the current relay turn.
""";
    }
}
