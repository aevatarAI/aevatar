using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Integration.AI;

internal static class WorkflowCallerCredentialToolContextMapper
{
    public static AgentToolExecutionContext FromCredential(WorkflowCallerCredential? credential)
    {
        return FromCredential(credential, AgentWorkflowRuntimeContext.Empty);
    }

    public static AgentToolExecutionContext FromCredential(
        WorkflowCallerCredential? credential,
        AgentWorkflowRuntimeContext workflowRuntimeContext)
    {
        var token = WorkflowCallerCredentialTokens.ParseOptional(credential?.BearerToken);
        if (token.IsInvalid)
            throw new ArgumentException("Workflow caller credential bearer token is invalid.", nameof(credential));

        var context = AgentToolExecutionContext.Empty with
        {
            WorkflowRuntime = workflowRuntimeContext,
            NyxIdAuthority = credential?.NyxIdAuthority == null
                ? AgentToolNyxIdAuthorityContext.Empty
                : new AgentToolNyxIdAuthorityContext(
                    Normalize(credential.NyxIdAuthority.Platform),
                    Normalize(credential.NyxIdAuthority.Tenant),
                    Normalize(credential.NyxIdAuthority.ExternalUserId),
                    Normalize(credential.NyxIdAuthority.Scope)),
            SenderBinding = credential?.NyxIdAuthority == null
                ? AgentToolSenderBindingContext.Empty
                : new AgentToolSenderBindingContext(
                    Normalize(credential.NyxIdAuthority.BindingId),
                    Normalize(credential.NyxIdAuthority.ExternalUserId),
                    Normalize(credential.NyxIdAuthority.Tenant)),
        };

        if (token.IsMissing)
            return context;

        return context with
        {
            Credentials = context.Credentials with
            {
                NyxIdAccessToken = token.NormalizedBearerToken,
                NyxIdOrgToken = token.NormalizedBearerToken,
                SenderNyxIdAccessToken = token.NormalizedBearerToken,
            },
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
