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
        if (WorkflowCallerCredentialTokens.ParseOptional(credential.BearerToken).IsValid)
            return credential;
        if (credential.NyxIdAuthority == null)
            return credential;
        if (provider == null)
            throw new InvalidOperationException("Workflow caller NyxID access token provider is unavailable.");

        var token = await provider.IssueAsync(credential.NyxIdAuthority, ct);
        return new WorkflowCallerCredential
        {
            BearerToken = token,
            NyxIdAuthority = credential.NyxIdAuthority.Clone(),
        };
    }
}
