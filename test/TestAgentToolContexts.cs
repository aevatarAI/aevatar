using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

internal static class TestAgentToolContexts
{
    public static AgentToolExecutionContext FromMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return AgentToolExecutionContext.Empty;

        var maxToolRounds = Get(metadata, LLMRequestMetadataKeys.MaxToolRoundsOverride);
        return new AgentToolExecutionContext(
            new AgentToolRequestIdentity(
                Get(metadata, LLMRequestMetadataKeys.RequestId),
                Get(metadata, LLMRequestMetadataKeys.CallId)),
            new AgentToolCredentials(
                Get(metadata, LLMRequestMetadataKeys.NyxIdAccessToken),
                Get(metadata, LLMRequestMetadataKeys.NyxIdOrgToken),
                Get(metadata, LLMRequestMetadataKeys.SenderNyxIdAccessToken)),
            new AgentToolCallerContext(
                Get(metadata, LLMRequestMetadataKeys.ScopeId) ?? Get(metadata, "scope_id"),
                Get(metadata, LLMRequestMetadataKeys.OwnerSubject),
                Get(metadata, LLMRequestMetadataKeys.ResponseId),
                Get(metadata, LLMRequestMetadataKeys.OwnerScopeId)),
            new AgentToolChannelContext(
                Get(metadata, "channel.platform") ?? Get(metadata, "platform"),
                Get(metadata, "channel.sender_id") ?? Get(metadata, "sender_id") ?? Get(metadata, "lark.open_id"),
                Get(metadata, "registration_scope_id"),
                Get(metadata, "channel.message_id") ?? Get(metadata, "message_id") ?? Get(metadata, "lark.message_id"),
                Get(metadata, "channel.platform_message_id") ?? Get(metadata, "platform_message_id")),
            new AgentToolSenderBindingContext(Get(metadata, LLMRequestMetadataKeys.SenderBindingId)),
            new LLMRequestRoutingContext(
                Get(metadata, LLMRequestMetadataKeys.ModelOverride),
                Get(metadata, LLMRequestMetadataKeys.NyxIdRoutePreference),
                int.TryParse(maxToolRounds, out var parsedMaxToolRounds) ? parsedMaxToolRounds : null,
                Get(metadata, LLMRequestMetadataKeys.UserMemoryPrompt)),
            new AgentToolConnectedServicesContext(Get(metadata, LLMRequestMetadataKeys.ConnectedServicesContext)),
            AgentSkillRecoveryContext.Empty,
            AgentToolExecutionContextMapper.StripOwnedControlKeys(metadata));
    }

    private static string? Get(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? Normalize(value) : null;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
