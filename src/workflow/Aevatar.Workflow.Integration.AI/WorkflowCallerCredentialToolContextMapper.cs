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
        var supplementalSourceReadableToken = WorkflowCallerCredentialTokens.ParseOptional(
            credential?.SourceReadableUserBearerToken);
        if (WorkflowCallerCredentialTokens.IsInvalidCredentialSet(
                credential?.BearerToken,
                credential?.Kind ?? NyxIdCallerCredentialKind.Unspecified,
                credential?.SourceReadableUserBearerToken))
            throw new ArgumentException("Workflow caller credential bearer token is invalid.", nameof(credential));

        var context = AgentToolExecutionContext.Empty with
        {
            WorkflowRuntime = workflowRuntimeContext,
            Caller = AgentToolCallerContext.Empty with
            {
                OwnerSubject = Normalize(credential?.NyxIdAuthority?.ExternalUserId),
            },
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

        var credentialKind = credential!.Kind switch
        {
            NyxIdCallerCredentialKind.SourceReadableUserBearer =>
                AgentToolNyxIdCredentialKind.SourceReadableUserBearer,
            NyxIdCallerCredentialKind.ProxyDelegation =>
                AgentToolNyxIdCredentialKind.ProxyDelegation,
            _ => AgentToolNyxIdCredentialKind.Unspecified,
        };
        var sourceReadableToken = supplementalSourceReadableToken.IsValid
            ? supplementalSourceReadableToken.NormalizedBearerToken
            : credentialKind == AgentToolNyxIdCredentialKind.SourceReadableUserBearer
                ? token.NormalizedBearerToken
                : null;

        return context with
        {
            Credentials = context.Credentials with
            {
                NyxIdAccessToken = token.NormalizedBearerToken,
                NyxIdOrgToken = credentialKind == AgentToolNyxIdCredentialKind.SourceReadableUserBearer
                    ? sourceReadableToken
                    : null,
                SenderNyxIdAccessToken =
                    credentialKind == AgentToolNyxIdCredentialKind.SourceReadableUserBearer
                        ? sourceReadableToken
                        : null,
                NyxIdCredentialKind = credentialKind,
                SourceReadableNyxIdAccessToken = supplementalSourceReadableToken.IsValid
                    ? supplementalSourceReadableToken.NormalizedBearerToken
                    : null,
            },
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class WorkflowRunScopeToolContextMapper
{
    public static AgentToolExecutionContext Apply(
        string? runScopeId,
        AgentToolExecutionContext toolContext)
    {
        var scopeId = Normalize(runScopeId);
        if (scopeId is null)
            return toolContext;

        return toolContext with
        {
            Caller = toolContext.Caller with
            {
                ScopeId = Fill(toolContext.Caller.ScopeId, scopeId),
                OwnerScopeId = Fill(toolContext.Caller.OwnerScopeId, scopeId),
            },
        };
    }

    private static string Fill(string? current, string fallback) =>
        string.IsNullOrWhiteSpace(current) ? fallback : current;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
