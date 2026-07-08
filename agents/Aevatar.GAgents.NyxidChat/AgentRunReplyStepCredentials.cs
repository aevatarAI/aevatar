using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Strips runtime-only NyxID credentials from an <see cref="AgentRunReplyStepState"/> before it is
/// committed to the event store as an <c>AgentRunReplyStepStateUpdatedEvent</c>. The proto contract
/// already declares that the persisted per-step waterline must NOT carry credentials — the executor
/// re-supplies the owner token from the transient self-message request and re-mints the sender token
/// from the retained binding id at execution time, so the persisted state keeps only identity facts
/// (binding id, subject, scope, routing), never bearer tokens.
///
/// The credential sub-messages themselves are left in place so structural checks (for example
/// <c>AgentRunReplyGenerationExecutor.TryBuildOwnerFallbackCommand</c>, which gates on whether the
/// owner-fallback sub-messages exist) keep working.
/// </summary>
internal static class AgentRunReplyStepCredentials
{
    public static AgentRunReplyStepState StripRuntimeCredentials(AgentRunReplyStepState stepState)
    {
        ArgumentNullException.ThrowIfNull(stepState);
        var stripped = stepState.Clone();
        ClearControlTokens(stripped.LlmControl);
        ClearControlTokens(stripped.OwnerFallbackLlmControl);
        ScrubToolContext(stripped.ToolContext);
        ScrubToolContext(stripped.OwnerFallbackToolContext);
        ScrubExternalMetadata(stripped.ExternalMetadata);
        return stripped;
    }

    private static void ClearControlTokens(Aevatar.AI.Abstractions.LLMControlContextPayload? control)
    {
        if (control is null) return;
        control.NyxIdAccessToken = string.Empty;
        control.NyxIdOrgToken = string.Empty;
        control.SenderNyxIdAccessToken = string.Empty;
    }

    private static void ScrubToolContext(Aevatar.AI.Abstractions.AgentToolExecutionContextPayload? context)
    {
        if (context is null) return;
        ClearCredentialTokens(context.Credentials);
        ScrubExternalMetadata(context.ExternalMetadata);
    }

    private static void ClearCredentialTokens(Aevatar.AI.Abstractions.AgentToolCredentialsPayload? credentials)
    {
        if (credentials is null) return;
        credentials.NyxIdAccessToken = string.Empty;
        credentials.NyxIdOrgToken = string.Empty;
        credentials.SenderNyxIdAccessToken = string.Empty;
    }

    private static void ScrubExternalMetadata(IDictionary<string, string>? externalMetadata)
    {
        if (externalMetadata is null || externalMetadata.Count == 0)
            return;

        var scrubbed = AgentToolExecutionContextMapper.StripOwnedControlKeys(
            new Dictionary<string, string>(externalMetadata, StringComparer.Ordinal));
        externalMetadata.Clear();
        foreach (var pair in scrubbed)
            externalMetadata[pair.Key] = pair.Value;
    }
}
