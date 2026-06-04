using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Integration.AI;

internal static class WorkflowCallerCredentialToolContextMapper
{
    public static AgentToolExecutionContext FromCredential(WorkflowCallerCredential? credential)
    {
        var token = NormalizeToken(credential?.BearerToken);
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

    private static string? NormalizeToken(string? rawToken)
    {
        var normalized = string.IsNullOrWhiteSpace(rawToken) ? string.Empty : rawToken.Trim();
        if (normalized.Length == 0 ||
            string.Equals(normalized, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            ContainsWhitespace(normalized))
        {
            return null;
        }

        return normalized;
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
                return true;
        }

        return false;
    }
}
