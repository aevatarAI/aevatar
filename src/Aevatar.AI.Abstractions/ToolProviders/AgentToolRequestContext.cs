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

    public static string? SourceReadableNyxIdAccessToken =>
        s_context.Value?.Credentials.SourceReadableNyxIdAccessToken;
    public static AgentToolNyxIdCredentialKind NyxIdCredentialKind =>
        s_context.Value?.Credentials.NyxIdCredentialKind ?? AgentToolNyxIdCredentialKind.Unspecified;
    public static string? ScopeId => s_context.Value?.Caller.ScopeId;
    public static string? OwnerScopeId => s_context.Value?.Caller.OwnerScopeId;
    public static string? OwnerSubject => s_context.Value?.Caller.OwnerSubject;
    public static AgentToolNyxIdAuthorityContext NyxIdAuthority =>
        s_context.Value?.NyxIdAuthority ?? AgentToolNyxIdAuthorityContext.Empty;
    public static string? ResponseId => s_context.Value?.Caller.ResponseId;
    public static string? RequestId => s_context.Value?.Request.RequestId;
    public static string? CallId => s_context.Value?.Request.CallId;
    public static string? IdempotencyKey => s_context.Value?.Request.IdempotencyKey;
    public static string? SenderBindingId => s_context.Value?.SenderBinding.BindingId;
    public static string? SenderNyxUserId => s_context.Value?.SenderBinding.NyxUserId;
    public static string? ModelOverride => s_context.Value?.Routing.ModelOverride;
    public static string? NyxIdRoutePreference => s_context.Value?.Routing.NyxIdRoutePreference;
    public static int? MaxToolRoundsOverride => s_context.Value?.Routing.MaxToolRoundsOverride;
    public static string? ConnectedServicesContext => s_context.Value?.ConnectedServices.ContextJson;
    public static string? ChannelPlatform => s_context.Value?.Channel.Platform;
    public static string? ChannelSenderId => s_context.Value?.Channel.SenderId;
    public static string? ChannelRegistrationScopeId => s_context.Value?.Channel.RegistrationScopeId;
    public static string? ChannelMessageId => s_context.Value?.Channel.MessageId;
    public static string? ChannelPlatformMessageId => s_context.Value?.Channel.PlatformMessageId;
    public static string? ChannelDeliveryTargetId => s_context.Value?.Channel.DeliveryTargetId;
    public static AgentToolVisibilityScope ToolVisibility =>
        s_context.Value?.ToolVisibility ?? AgentToolVisibilityScope.Unrestricted;

    public static IReadOnlyList<Aevatar.AI.Abstractions.ChatFileRef> InputFileRefs =>
        s_context.Value?.InputFileRefs ?? [];

    public static string? TryGetExternalMetadata(string key)
    {
        var metadata = s_context.Value?.ExternalMetadata;
        if (metadata != null && metadata.TryGetValue(key, out var value))
            return value;
        return null;
    }
}
