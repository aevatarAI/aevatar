using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Credentials;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowCallerAccessTokenResolver
{
    public static async Task<WorkflowCallerCredential> ResolveAsync(
        WorkflowCallerCredential credential,
        IWorkflowCallerAccessTokenProvider? provider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var bearer = WorkflowCallerCredentialTokens.ParseOptional(credential.BearerToken);
        var shouldRefreshProxyDelegation =
            credential.Kind == NyxIdCallerCredentialKind.ProxyDelegation &&
            credential.NyxIdAuthority != null;
        if (bearer.IsValid && !shouldRefreshProxyDelegation)
            return credential;
        if (credential.NyxIdAuthority == null)
            return credential;
        if (provider == null)
            throw new InvalidOperationException("Workflow caller NyxID access token provider is unavailable.");

        var token = await provider.IssueAsync(credential.NyxIdAuthority, ct);
        return new WorkflowCallerCredential
        {
            BearerToken = token,
            SourceReadableUserBearerToken = credential.SourceReadableUserBearerToken,
            NyxIdAuthority = credential.NyxIdAuthority.Clone(),
            Kind = NyxIdCallerCredentialKind.ProxyDelegation,
        };
    }
}
