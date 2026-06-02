namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// 工具执行期间的请求上下文。通过 AsyncLocal 将 typed control context 传递给工具。
/// 在 ToolCallLoop 执行期间设置，工具执行完毕后清除。
/// </summary>
// Refactor (iter24/cluster-002-agent-tool-context-generic-metadata-bag):
//   Old pattern: credentials, scope and routing moved through generic Metadata.
//   New principle: tool control semantics are typed context fields; Metadata is not the internal control plane.
public static class AgentToolRequestContext
{
    private static readonly AsyncLocal<AgentToolExecutionContext?> s_context = new();

    public static AgentToolExecutionContext? Current
    {
        get => s_context.Value;
        set => s_context.Value = value;
    }

    public static string? NyxIdAccessToken => s_context.Value?.Credentials.NyxIdAccessToken;
    public static string? NyxIdOrgToken => s_context.Value?.Credentials.NyxIdOrgToken;
    public static string? SenderNyxIdAccessToken => s_context.Value?.Credentials.SenderNyxIdAccessToken;
    public static string? ScopeId => s_context.Value?.Caller.ScopeId;
    public static string? OwnerSubject => s_context.Value?.Caller.OwnerSubject;
    public static string? ResponseId => s_context.Value?.Caller.ResponseId;
    public static string? RequestId => s_context.Value?.Request.RequestId;
    public static string? CallId => s_context.Value?.Request.CallId;
    public static string? SenderBindingId => s_context.Value?.SenderBinding.BindingId;
    public static string? ModelOverride => s_context.Value?.Routing.ModelOverride;
    public static string? NyxIdRoutePreference => s_context.Value?.Routing.NyxIdRoutePreference;
    public static int? MaxToolRoundsOverride => s_context.Value?.Routing.MaxToolRoundsOverride;
    public static string? ConnectedServicesContext => s_context.Value?.ConnectedServices.ContextJson;
    public static string? ChannelPlatform => s_context.Value?.Channel.Platform;
    public static string? ChannelSenderId => s_context.Value?.Channel.SenderId;
    public static string? ChannelRegistrationScopeId => s_context.Value?.Channel.RegistrationScopeId;
    public static string? ChannelMessageId => s_context.Value?.Channel.MessageId;
    public static string? ChannelPlatformMessageId => s_context.Value?.Channel.PlatformMessageId;

    public static string? TryGetExternalMetadata(string key)
    {
        var metadata = s_context.Value?.ExternalMetadata;
        if (metadata != null && metadata.TryGetValue(key, out var value))
            return value;
        return null;
    }

    /// <summary>
    /// Legacy adapter/test bridge for old or third-party metadata-only tool surfaces.
    /// Application, Domain, and Core production code must use typed accessors on
    /// <see cref="AgentToolExecutionContext"/> instead of reading control semantics from this bag.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? LegacyMetadata
    {
        get => s_context.Value?.ToLegacyMetadata();
        set => s_context.Value = AgentToolExecutionContextMapper.FromMetadata(value);
    }

    /// <summary>
    /// Legacy test-only alias kept so older test fixtures do not have to migrate all at once.
    /// New production code must use <see cref="LegacyMetadata"/> only at explicit adapter boundaries.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? CurrentMetadata
    {
        get => LegacyMetadata;
        set => LegacyMetadata = value;
    }

    /// <summary>
    /// Legacy mapper-only shim for older tests and adapters while production consumers migrate.
    /// Production control flow must read typed properties from <see cref="Current"/>.
    /// </summary>
    public static string? TryGet(string key)
    {
        var context = s_context.Value;
        if (context == null)
            return null;

        return key switch
        {
            LLMProviders.LLMRequestMetadataKeys.NyxIdAccessToken => context.Credentials.NyxIdAccessToken,
            LLMProviders.LLMRequestMetadataKeys.NyxIdOrgToken => context.Credentials.NyxIdOrgToken,
            LLMProviders.LLMRequestMetadataKeys.SenderNyxIdAccessToken => context.Credentials.SenderNyxIdAccessToken,
            LLMProviders.LLMRequestMetadataKeys.ScopeId or "scope_id" => context.Caller.ScopeId,
            LLMProviders.LLMRequestMetadataKeys.OwnerSubject => context.Caller.OwnerSubject,
            LLMProviders.LLMRequestMetadataKeys.ResponseId => context.Caller.ResponseId,
            LLMProviders.LLMRequestMetadataKeys.RequestId => context.Request.RequestId,
            LLMProviders.LLMRequestMetadataKeys.CallId => context.Request.CallId,
            LLMProviders.LLMRequestMetadataKeys.SenderBindingId => context.SenderBinding.BindingId,
            LLMProviders.LLMRequestMetadataKeys.ModelOverride => context.Routing.ModelOverride,
            LLMProviders.LLMRequestMetadataKeys.NyxIdRoutePreference => context.Routing.NyxIdRoutePreference,
            LLMProviders.LLMRequestMetadataKeys.MaxToolRoundsOverride => context.Routing.MaxToolRoundsOverride?.ToString(),
            LLMProviders.LLMRequestMetadataKeys.ConnectedServicesContext => context.ConnectedServices.ContextJson,
            "platform" => context.Channel.Platform,
            "sender_id" => context.Channel.SenderId,
            "registration_scope_id" => context.Channel.RegistrationScopeId,
            "message_id" or "lark.message_id" => context.Channel.MessageId,
            "platform_message_id" => context.Channel.PlatformMessageId,
            _ => TryGetExternalMetadata(key),
        };
    }
}
