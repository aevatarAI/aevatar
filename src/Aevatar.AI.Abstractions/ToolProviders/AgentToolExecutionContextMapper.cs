using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Abstractions.ToolProviders;

// Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
//   Old pattern: any tool could parse control keys from Metadata directly.
//   New principle: legacy metadata decoding is isolated here; tool control flow uses typed context.
public static class AgentToolExecutionContextMapper
{
    private static readonly HashSet<string> OwnedControlKeys = new(StringComparer.Ordinal)
    {
        LLMRequestMetadataKeys.RequestId,
        LLMRequestMetadataKeys.CallId,
        LLMRequestMetadataKeys.ScopeId,
        "scope_id",
        LLMRequestMetadataKeys.OwnerSubject,
        LLMRequestMetadataKeys.ResponseId,
        LLMRequestMetadataKeys.NyxIdAccessToken,
        LLMRequestMetadataKeys.NyxIdOrgToken,
        LLMRequestMetadataKeys.NyxIdRoutePreference,
        LLMRequestMetadataKeys.ModelOverride,
        LLMRequestMetadataKeys.MaxToolRoundsOverride,
        LLMRequestMetadataKeys.UserMemoryPrompt,
        LLMRequestMetadataKeys.ConnectedServicesContext,
        LLMRequestMetadataKeys.SenderBindingId,
        LLMRequestMetadataKeys.SenderNyxIdAccessToken,
        "platform",
        "channel.platform",
        "sender_id",
        "channel.sender_id",
        "registration_scope_id",
        "message_id",
        "channel.message_id",
        "platform_message_id",
        "channel.platform_message_id",
        "lark.message_id",
        "lark.open_id",
        "lark.receive_id",
        "telegram.chat_id",
    };

    public static AgentToolExecutionContext FromRequest(LLMRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ToolContext is { } typedContext)
            return typedContext;

        var mapped = FromMetadata(request.Metadata);
        var caller = request.CallerContext;
        return mapped with
        {
            Request = new AgentToolRequestIdentity(
                AgentToolExecutionContext.Normalize(request.RequestId) ?? mapped.Request.RequestId,
                mapped.Request.CallId),
            Caller = caller == null
                ? mapped.Caller
                : new AgentToolCallerContext(
                    AgentToolExecutionContext.Normalize(caller.ScopeId) ?? mapped.Caller.ScopeId,
                    AgentToolExecutionContext.Normalize(caller.OwnerSubject) ?? mapped.Caller.OwnerSubject,
                    AgentToolExecutionContext.Normalize(caller.ResponseId) ?? mapped.Caller.ResponseId),
            Credentials = caller?.Credentials == null
                ? mapped.Credentials
                : mapped.Credentials with
                {
                    NyxIdAccessToken = AgentToolExecutionContext.Normalize(caller.Credentials.NyxIdBearer)
                        ?? mapped.Credentials.NyxIdAccessToken,
                },
            Routing = request.RoutingContext ?? mapped.Routing,
            ExternalMetadata = StripOwnedControlKeys(request.Metadata),
        };
    }

    public static AgentToolExecutionContext FromRequestWithCallId(LLMRequest request, string? callId)
    {
        ArgumentNullException.ThrowIfNull(request);

        return FromRequest(request).WithCallId(callId);
    }

    public static AgentToolExecutionContext FromMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return AgentToolExecutionContext.Empty;

        var maxToolRounds = TryGet(metadata, LLMRequestMetadataKeys.MaxToolRoundsOverride);
        return new AgentToolExecutionContext(
            new AgentToolRequestIdentity(
                TryGet(metadata, LLMRequestMetadataKeys.RequestId),
                TryGet(metadata, LLMRequestMetadataKeys.CallId)),
            new AgentToolCredentials(
                TryGet(metadata, LLMRequestMetadataKeys.NyxIdAccessToken),
                TryGet(metadata, LLMRequestMetadataKeys.NyxIdOrgToken),
                TryGet(metadata, LLMRequestMetadataKeys.SenderNyxIdAccessToken)),
            new AgentToolCallerContext(
                TryGet(metadata, LLMRequestMetadataKeys.ScopeId) ?? TryGet(metadata, "scope_id"),
                TryGet(metadata, LLMRequestMetadataKeys.OwnerSubject),
                TryGet(metadata, LLMRequestMetadataKeys.ResponseId)),
            new AgentToolChannelContext(
                TryGet(metadata, "channel.platform") ?? TryGet(metadata, "platform"),
                TryGet(metadata, "channel.sender_id") ?? TryGet(metadata, "sender_id") ?? TryGet(metadata, "lark.open_id"),
                TryGet(metadata, "registration_scope_id"),
                TryGet(metadata, "channel.message_id") ?? TryGet(metadata, "message_id") ?? TryGet(metadata, "lark.message_id"),
                TryGet(metadata, "channel.platform_message_id") ?? TryGet(metadata, "platform_message_id")),
            new AgentToolSenderBindingContext(TryGet(metadata, LLMRequestMetadataKeys.SenderBindingId)),
            new LLMRequestRoutingContext(
                TryGet(metadata, LLMRequestMetadataKeys.ModelOverride),
                TryGet(metadata, LLMRequestMetadataKeys.NyxIdRoutePreference),
                int.TryParse(maxToolRounds, out var parsedMaxToolRounds) ? parsedMaxToolRounds : null,
                TryGet(metadata, LLMRequestMetadataKeys.UserMemoryPrompt)),
            new AgentToolConnectedServicesContext(TryGet(metadata, LLMRequestMetadataKeys.ConnectedServicesContext)),
            StripOwnedControlKeys(metadata));
    }

    public static IReadOnlyDictionary<string, string> StripOwnedControlKeys(IReadOnlyDictionary<string, string>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata == null)
            return result;

        foreach (var (key, value) in metadata)
        {
            if (OwnedControlKeys.Contains(key))
                continue;

            result[key] = value;
        }

        return result;
    }

    private static string? TryGet(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? AgentToolExecutionContext.Normalize(value) : null;
}
