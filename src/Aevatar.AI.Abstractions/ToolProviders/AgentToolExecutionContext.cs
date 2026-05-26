using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Abstractions.ToolProviders;

// Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
//   Old pattern: tool control facts lived in AsyncLocal string Metadata.
//   New principle: tool control semantics are typed context fields; Metadata is not the internal control plane.
public sealed record AgentToolExecutionContext(
    AgentToolRequestIdentity Request,
    AgentToolCredentials Credentials,
    AgentToolCallerContext Caller,
    AgentToolChannelContext Channel,
    AgentToolSenderBindingContext SenderBinding,
    LLMRequestRoutingContext Routing,
    AgentToolConnectedServicesContext ConnectedServices,
    IReadOnlyDictionary<string, string> ExternalMetadata)
{
    public static AgentToolExecutionContext Empty { get; } = new(
        AgentToolRequestIdentity.Empty,
        AgentToolCredentials.Empty,
        AgentToolCallerContext.Empty,
        AgentToolChannelContext.Empty,
        AgentToolSenderBindingContext.Empty,
        LLMRequestRoutingContext.Empty,
        AgentToolConnectedServicesContext.Empty,
        new Dictionary<string, string>(StringComparer.Ordinal));

    public AgentToolExecutionContext WithCallId(string? callId) =>
        this with { Request = Request with { CallId = Normalize(callId) } };

    public IReadOnlyDictionary<string, string> ToLegacyMetadata()
    {
        var metadata = new Dictionary<string, string>(ExternalMetadata, StringComparer.Ordinal);
        Set(metadata, LLMRequestMetadataKeys.RequestId, Request.RequestId);
        Set(metadata, LLMRequestMetadataKeys.CallId, Request.CallId);
        Set(metadata, LLMRequestMetadataKeys.ScopeId, Caller.ScopeId);
        Set(metadata, "scope_id", Caller.ScopeId);
        Set(metadata, LLMRequestMetadataKeys.OwnerSubject, Caller.OwnerSubject);
        Set(metadata, LLMRequestMetadataKeys.ResponseId, Caller.ResponseId);
        Set(metadata, LLMRequestMetadataKeys.NyxIdAccessToken, Credentials.NyxIdAccessToken);
        Set(metadata, LLMRequestMetadataKeys.NyxIdOrgToken, Credentials.NyxIdOrgToken);
        Set(metadata, LLMRequestMetadataKeys.SenderNyxIdAccessToken, Credentials.SenderNyxIdAccessToken);
        Set(metadata, LLMRequestMetadataKeys.SenderBindingId, SenderBinding.BindingId);
        Set(metadata, LLMRequestMetadataKeys.ModelOverride, Routing.ModelOverride);
        Set(metadata, LLMRequestMetadataKeys.NyxIdRoutePreference, Routing.NyxIdRoutePreference);
        Set(metadata, LLMRequestMetadataKeys.MaxToolRoundsOverride, Routing.MaxToolRoundsOverride?.ToString());
        Set(metadata, LLMRequestMetadataKeys.UserMemoryPrompt, Routing.UserMemoryPrompt);
        Set(metadata, LLMRequestMetadataKeys.ConnectedServicesContext, ConnectedServices.ContextJson);
        Set(metadata, "platform", Channel.Platform);
        Set(metadata, "channel.platform", Channel.Platform);
        Set(metadata, "sender_id", Channel.SenderId);
        Set(metadata, "channel.sender_id", Channel.SenderId);
        Set(metadata, "registration_scope_id", Channel.RegistrationScopeId);
        Set(metadata, "message_id", Channel.MessageId);
        Set(metadata, "channel.message_id", Channel.MessageId);
        Set(metadata, "platform_message_id", Channel.PlatformMessageId);
        Set(metadata, "channel.platform_message_id", Channel.PlatformMessageId);
        return metadata;
    }

    private static void Set(IDictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value.Trim();
    }

    internal static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AgentToolRequestIdentity(string? RequestId, string? CallId)
{
    public static AgentToolRequestIdentity Empty { get; } = new(null, null);
}

public sealed record AgentToolCredentials(
    string? NyxIdAccessToken,
    string? NyxIdOrgToken,
    string? SenderNyxIdAccessToken)
{
    public static AgentToolCredentials Empty { get; } = new(null, null, null);
}

public sealed record AgentToolCallerContext(string? ScopeId, string? OwnerSubject, string? ResponseId)
{
    public static AgentToolCallerContext Empty { get; } = new(null, null, null);
}

public sealed record AgentToolChannelContext(
    string? Platform,
    string? SenderId,
    string? RegistrationScopeId,
    string? MessageId,
    string? PlatformMessageId)
{
    public static AgentToolChannelContext Empty { get; } = new(null, null, null, null, null);
}

public sealed record AgentToolSenderBindingContext(string? BindingId)
{
    public static AgentToolSenderBindingContext Empty { get; } = new((string?)null);
}

public sealed record AgentToolConnectedServicesContext(string? ContextJson)
{
    public static AgentToolConnectedServicesContext Empty { get; } = new((string?)null);
}
