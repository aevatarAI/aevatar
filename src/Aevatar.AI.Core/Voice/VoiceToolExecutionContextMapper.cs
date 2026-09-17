using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.AI.Core.Voice;

internal static class VoiceToolExecutionContextMapper
{
    public static AgentToolExecutionContext ToAgentToolContext(
        VoiceToolExecutionContext voiceContext,
        string nyxIdAccessToken)
    {
        ArgumentNullException.ThrowIfNull(voiceContext);

        var context = new AgentToolExecutionContext(
            new AgentToolRequestIdentity(null, null),
            new AgentToolCredentials(Normalize(nyxIdAccessToken), null, null),
            new AgentToolCallerContext(
                Normalize(voiceContext.CallerScopeId),
                Normalize(voiceContext.OwnerSubject),
                Normalize(voiceContext.ResponseId)),
            new AgentToolChannelContext(
                Normalize(voiceContext.ChannelPlatform),
                Normalize(voiceContext.ChannelSenderId),
                Normalize(voiceContext.ChannelRegistrationScopeId),
                Normalize(voiceContext.ChannelMessageId),
                Normalize(voiceContext.ChannelPlatformMessageId),
                Normalize(voiceContext.ChannelDeliveryTargetId)),
            new AgentToolSenderBindingContext(Normalize(voiceContext.SenderBindingId)),
            new LLMRequestRoutingContext(
                ModelOverride: null,
                NyxIdRoutePreference: Normalize(voiceContext.NyxIdRoutePreference),
                MaxToolRoundsOverride: null,
                UserMemoryPrompt: null),
            new AgentToolConnectedServicesContext(Normalize(voiceContext.ConnectedServicesContextJson)),
            AgentWorkflowRuntimeContext.Empty,
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal));

        return context with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(
                voiceContext.AllowedToolNames),
        };
    }

    public static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static bool IsUsableCredentialRef(VoiceToolExecutionContext? voiceContext, DateTimeOffset nowUtc)
    {
        if (Normalize(voiceContext?.CredentialRef) is null)
            return false;

        var expiresAt = voiceContext?.ExpiresAt?.ToDateTimeOffset();
        return expiresAt is not null && expiresAt.Value.ToUniversalTime() > nowUtc.ToUniversalTime();
    }
}
