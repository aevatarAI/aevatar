using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Integration.AI;

internal static class WorkflowCallerCredentialToolContextMapper
{
    public static AgentToolExecutionContext FromCredential(WorkflowCallerCredential? credential)
    {
        var token = WorkflowCallerCredentialTokens.ParseOptional(credential?.BearerToken);
        if (token.IsMissing)
            return AgentToolExecutionContext.Empty;
        if (token.IsInvalid)
            throw new ArgumentException("Workflow caller credential bearer token is invalid.", nameof(credential));

        return AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = token.NormalizedBearerToken,
                NyxIdOrgToken = token.NormalizedBearerToken,
            },
        };
    }
}
