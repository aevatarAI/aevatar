namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Resolves the NyxID bearer that a verified human Assistant session may use for
/// owner-scoped NyxID reads. A source-readable user bearer keeps its existing
/// semantics. A proxy delegation is accepted only when the typed execution
/// context proves the human NyxID Assistant surface and its owner authority.
/// </summary>
public static class AgentToolHumanSessionNyxIdCredential
{
    public static string? ResolveBearerToken(AgentToolExecutionContext? context)
    {
        var sourceReadable = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
            context?.Credentials);
        if (sourceReadable is not null)
            return sourceReadable;

        if (context is null ||
            context.InvocationSurface != AgentToolInvocationSurface.HumanSession ||
            context.Chat.Surface != AgentChatInvocationSurface.NyxIdAssistant ||
            string.IsNullOrWhiteSpace(context.Caller.OwnerScopeId) ||
            !string.Equals(
                context.Caller.ScopeId,
                context.Caller.OwnerScopeId,
                StringComparison.Ordinal) ||
            !context.NyxIdAuthority.IsComplete ||
            !string.Equals(
                context.Caller.OwnerSubject,
                context.NyxIdAuthority.ExternalUserId,
                StringComparison.Ordinal) ||
            context.Credentials.NyxIdCredentialKind != AgentToolNyxIdCredentialKind.ProxyDelegation)
        {
            return null;
        }

        return Normalize(context.Credentials.NyxIdAccessToken);
    }

    private static string? Normalize(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var normalized = token.Trim();
        if (string.Equals(normalized, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            normalized.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return normalized;
    }
}
