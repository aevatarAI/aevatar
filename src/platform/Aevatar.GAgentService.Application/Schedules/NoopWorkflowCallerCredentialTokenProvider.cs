using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Application.Schedules;

public sealed class NoopWorkflowCallerCredentialTokenProvider : IWorkflowCallerCredentialTokenProvider
{
    public Task<WorkflowCallerCredentialTokenResolution> ResolveAsync(
        WorkflowNyxIdCredentialSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Workflow caller NyxID credential token provider is not configured.");
    }
}
