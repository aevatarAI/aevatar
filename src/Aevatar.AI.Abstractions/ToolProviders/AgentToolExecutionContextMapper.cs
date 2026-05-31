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
            return request.LlmControl?.ToToolContext(typedContext) ?? typedContext;

        // Refactor (issue1574): Old pattern: core request mapping promoted owned control keys from Metadata.
        // New principle: core LLMRequest control is typed; Metadata contributes only scrubbed annotations.
        var mapped = AgentToolExecutionContext.Empty with
        {
            ExternalMetadata = StripOwnedControlKeys(request.Metadata),
        };
        mapped = request.LlmControl?.ToToolContext(mapped) ?? mapped;
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

    public static AgentToolExecutionContext FromPayload(AgentToolExecutionContextPayload? payload)
    {
        if (payload == null)
            return AgentToolExecutionContext.Empty;

        return new AgentToolExecutionContext(
            new AgentToolRequestIdentity(
                AgentToolExecutionContext.Normalize(payload.Request?.RequestId),
                AgentToolExecutionContext.Normalize(payload.Request?.CallId)),
            new AgentToolCredentials(
                AgentToolExecutionContext.Normalize(payload.Credentials?.NyxIdAccessToken),
                AgentToolExecutionContext.Normalize(payload.Credentials?.NyxIdOrgToken),
                AgentToolExecutionContext.Normalize(payload.Credentials?.SenderNyxIdAccessToken)),
            new AgentToolCallerContext(
                AgentToolExecutionContext.Normalize(payload.Caller?.ScopeId),
                AgentToolExecutionContext.Normalize(payload.Caller?.OwnerSubject),
                AgentToolExecutionContext.Normalize(payload.Caller?.ResponseId)),
            new AgentToolChannelContext(
                AgentToolExecutionContext.Normalize(payload.Channel?.Platform),
                AgentToolExecutionContext.Normalize(payload.Channel?.SenderId),
                AgentToolExecutionContext.Normalize(payload.Channel?.RegistrationScopeId),
                AgentToolExecutionContext.Normalize(payload.Channel?.MessageId),
                AgentToolExecutionContext.Normalize(payload.Channel?.PlatformMessageId)),
            new AgentToolSenderBindingContext(AgentToolExecutionContext.Normalize(payload.SenderBinding?.BindingId)),
            new LLMRequestRoutingContext(
                AgentToolExecutionContext.Normalize(payload.Routing?.ModelOverride),
                AgentToolExecutionContext.Normalize(payload.Routing?.NyxIdRoutePreference),
                payload.Routing?.HasMaxToolRoundsOverride == true ? payload.Routing.MaxToolRoundsOverride : null,
                AgentToolExecutionContext.Normalize(payload.Routing?.UserMemoryPrompt)),
            new AgentToolConnectedServicesContext(AgentToolExecutionContext.Normalize(payload.ConnectedServices?.ContextJson)),
            StripOwnedControlKeys(payload.ExternalMetadata));
    }

    public static AgentToolExecutionContextPayload ToPayload(this AgentToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var payload = new AgentToolExecutionContextPayload
        {
            Request = new AgentToolRequestIdentityPayload
            {
                RequestId = context.Request.RequestId ?? string.Empty,
                CallId = context.Request.CallId ?? string.Empty,
            },
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = context.Credentials.NyxIdAccessToken ?? string.Empty,
                NyxIdOrgToken = context.Credentials.NyxIdOrgToken ?? string.Empty,
                SenderNyxIdAccessToken = context.Credentials.SenderNyxIdAccessToken ?? string.Empty,
            },
            Caller = new AgentToolCallerContextPayload
            {
                ScopeId = context.Caller.ScopeId ?? string.Empty,
                OwnerSubject = context.Caller.OwnerSubject ?? string.Empty,
                ResponseId = context.Caller.ResponseId ?? string.Empty,
            },
            Channel = new AgentToolChannelContextPayload
            {
                Platform = context.Channel.Platform ?? string.Empty,
                SenderId = context.Channel.SenderId ?? string.Empty,
                RegistrationScopeId = context.Channel.RegistrationScopeId ?? string.Empty,
                MessageId = context.Channel.MessageId ?? string.Empty,
                PlatformMessageId = context.Channel.PlatformMessageId ?? string.Empty,
            },
            SenderBinding = new AgentToolSenderBindingContextPayload
            {
                BindingId = context.SenderBinding.BindingId ?? string.Empty,
            },
            Routing = new LLMRequestRoutingContextPayload
            {
                ModelOverride = context.Routing.ModelOverride ?? string.Empty,
                NyxIdRoutePreference = context.Routing.NyxIdRoutePreference ?? string.Empty,
                UserMemoryPrompt = context.Routing.UserMemoryPrompt ?? string.Empty,
            },
            ConnectedServices = new AgentToolConnectedServicesContextPayload
            {
                ContextJson = context.ConnectedServices.ContextJson ?? string.Empty,
            },
        };

        if (context.Routing.MaxToolRoundsOverride.HasValue)
            payload.Routing.MaxToolRoundsOverride = context.Routing.MaxToolRoundsOverride.Value;

        foreach (var pair in StripOwnedControlKeys(context.ExternalMetadata))
            payload.ExternalMetadata[pair.Key] = pair.Value;

        return payload;
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
