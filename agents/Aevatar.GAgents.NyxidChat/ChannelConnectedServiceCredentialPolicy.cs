using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Selects the authority used by Channel connected-service operations. The LLM
/// credential is resolved separately; this policy only prepares the tool
/// execution context. Sender authority requires both a binding identity and a
/// readable bearer token; otherwise a registration Agent Key may own the turn.
/// </summary>
public static class ChannelConnectedServiceCredentialPolicy
{
    public static AgentToolExecutionContext Apply(
        AgentToolExecutionContext context,
        string? senderToken,
        string? registrationAgentKey)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bindingId = Normalize(context.SenderBinding.BindingId);
        var token = Normalize(senderToken);
        if (bindingId is not null && token is not null)
        {
            return context with
            {
                CredentialSource = AgentToolCredentialSource.BearerToken,
                DurableNyxIdCredential = null,
                Credentials = new AgentToolCredentials(
                    token,
                    NyxIdOrgToken: null,
                    SenderNyxIdAccessToken: token,
                    NyxIdCredentialKind: AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
                    SourceReadableNyxIdAccessToken: token,
                    NyxIdCredentialAuthority: AgentToolNyxIdCredentialAuthority.ToolExecutionContext),
            };
        }

        var agentKey = Normalize(registrationAgentKey);
        if (agentKey is null)
        {
            if (bindingId is null)
                return context;

            return context with
            {
                CredentialSource = AgentToolCredentialSource.BearerToken,
                DurableNyxIdCredential = null,
                Credentials = new AgentToolCredentials(
                    null,
                    NyxIdOrgToken: null,
                    SenderNyxIdAccessToken: null,
                    NyxIdCredentialKind: AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
                    SourceReadableNyxIdAccessToken: null,
                    NyxIdCredentialAuthority: AgentToolNyxIdCredentialAuthority.ToolExecutionContext),
            };
        }

        return context with
        {
            CredentialSource = AgentToolCredentialSource.ChannelRegistration,
            Credentials = new AgentToolCredentials(
                agentKey,
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.AgentKey,
                SourceReadableNyxIdAccessToken: null,
                NyxIdCredentialAuthority: AgentToolNyxIdCredentialAuthority.ToolExecutionContext),
        };
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
