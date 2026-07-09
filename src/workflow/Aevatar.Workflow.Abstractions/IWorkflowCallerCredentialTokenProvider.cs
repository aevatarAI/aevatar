namespace Aevatar.Workflow.Abstractions;

public interface IWorkflowCallerCredentialTokenProvider
{
    Task<WorkflowCallerCredentialTokenResolution> ResolveAsync(
        WorkflowNyxIdCredentialSource source,
        CancellationToken ct = default);
}

public sealed record WorkflowCallerCredentialTokenResolution(
    string AccessToken,
    DateTimeOffset? ExpiresAtUtc = null);
