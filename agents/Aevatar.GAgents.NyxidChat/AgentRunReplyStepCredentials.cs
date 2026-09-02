namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Strips runtime-only NyxID credentials from an <see cref="AgentRunReplyStepState"/> before it is
/// committed to the event store as an <c>AgentRunReplyStepStateUpdatedEvent</c>. The proto contract
/// already declares that the persisted per-step waterline must NOT carry credentials — the executor
/// re-supplies the owner token from the transient self-message request and re-mints the sender token
/// from the retained binding id at execution time, so the persisted state keeps only identity facts
/// (binding id, subject, scope, routing), never bearer tokens.
///
/// Only the token <em>strings</em> are cleared; the credential sub-messages themselves are left in
/// place so structural checks (for example <c>AgentRunReplyGenerationExecutor.TryBuildOwnerFallbackCommand</c>,
/// which gates on whether the owner-fallback sub-messages exist) keep working.
/// </summary>
internal static class AgentRunReplyStepCredentials
{
    public static AgentRunReplyStepState StripRuntimeCredentials(AgentRunReplyStepState stepState)
    {
        ArgumentNullException.ThrowIfNull(stepState);
        var stripped = stepState.Clone();
        ClearControlTokens(stripped.LlmControl);
        ClearControlTokens(stripped.OwnerFallbackLlmControl);
        ClearCredentialTokens(stripped.ToolContext?.Credentials);
        ClearCredentialTokens(stripped.OwnerFallbackToolContext?.Credentials);
        return stripped;
    }

    private static void ClearControlTokens(Aevatar.AI.Abstractions.LLMControlContextPayload? control)
    {
        if (control is null) return;
        control.NyxIdAccessToken = string.Empty;
        control.NyxIdOrgToken = string.Empty;
        control.SenderNyxIdAccessToken = string.Empty;
    }

    private static void ClearCredentialTokens(Aevatar.AI.Abstractions.AgentToolCredentialsPayload? credentials)
    {
        if (credentials is null) return;
        credentials.NyxIdAccessToken = string.Empty;
        credentials.NyxIdOrgToken = string.Empty;
        credentials.SenderNyxIdAccessToken = string.Empty;
    }
}
