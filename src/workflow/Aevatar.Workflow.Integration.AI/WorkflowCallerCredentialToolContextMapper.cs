using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Integration.AI;

internal static class WorkflowCallerCredentialToolContextMapper
{
    private const string BearerPrefix = "Bearer ";

    public static AgentToolExecutionContext FromCredential(WorkflowCallerCredential? credential)
    {
        var token = ExtractBearerToken(credential?.NyxIdBearer);
        if (token == null)
            return AgentToolExecutionContext.Empty;

        return AgentToolExecutionContext.Empty with
        {
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = token,
                NyxIdOrgToken = token,
            },
        };
    }

    private static string? ExtractBearerToken(string? authorization)
    {
        var normalized = string.IsNullOrWhiteSpace(authorization) ? string.Empty : authorization.Trim();
        if (!normalized.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = normalized[BearerPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
