namespace Aevatar.Workflow.Abstractions;

public enum WorkflowCallerCredentialTokenParseStatus
{
    Missing = 0,
    Valid = 1,
    Invalid = 2,
}

public readonly record struct WorkflowCallerCredentialTokenParseResult(
    WorkflowCallerCredentialTokenParseStatus Status,
    string? NormalizedBearerToken)
{
    public bool IsMissing => Status == WorkflowCallerCredentialTokenParseStatus.Missing;

    public bool IsValid => Status == WorkflowCallerCredentialTokenParseStatus.Valid;

    public bool IsInvalid => Status == WorkflowCallerCredentialTokenParseStatus.Invalid;
}

public static class WorkflowCallerCredentialTokens
{
    public static bool IsInvalidCredentialSet(
        string? executionBearerToken,
        NyxIdCallerCredentialKind executionKind,
        string? sourceReadableUserBearerToken)
    {
        var execution = ParseOptional(executionBearerToken);
        var sourceReadable = ParseOptional(sourceReadableUserBearerToken);
        return execution.IsInvalid ||
               sourceReadable.IsInvalid ||
               sourceReadable.IsValid &&
               (!execution.IsValid || executionKind != NyxIdCallerCredentialKind.ProxyDelegation);
    }

    public static WorkflowCallerCredentialTokenParseResult ParseOptional(string? rawBearerToken)
    {
        if (string.IsNullOrWhiteSpace(rawBearerToken))
            return new WorkflowCallerCredentialTokenParseResult(
                WorkflowCallerCredentialTokenParseStatus.Missing,
                null);

        var normalized = rawBearerToken.Trim();
        if (string.Equals(normalized, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            ContainsWhitespace(normalized))
        {
            return new WorkflowCallerCredentialTokenParseResult(
                WorkflowCallerCredentialTokenParseStatus.Invalid,
                null);
        }

        return new WorkflowCallerCredentialTokenParseResult(
            WorkflowCallerCredentialTokenParseStatus.Valid,
            normalized);
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
