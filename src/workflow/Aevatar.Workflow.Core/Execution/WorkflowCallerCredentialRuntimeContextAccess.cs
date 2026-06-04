using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowCallerCredentialRuntimeContextAccess
{
    public static Task SetCredentialAsync(
        IWorkflowExecutionStateHost stateHost,
        WorkflowCallerCredential? credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        if (string.IsNullOrWhiteSpace(credential?.NyxIdBearer))
        {
            return stateHost.UpdateExecutionContextAsync(
                new WorkflowRunExecutionContextDelta
                {
                    ClearCallerCredential = true,
                },
                ct);
        }

        return stateHost.UpdateExecutionContextAsync(
            new WorkflowRunExecutionContextDelta
            {
                ClearCallerCredential = true,
                CallerCredential = new WorkflowCallerCredential
                {
                    NyxIdBearer = credential.NyxIdBearer.Trim(),
                },
            },
            ct);
    }

    public static Task RemoveCredentialAsync(
        IWorkflowExecutionStateHost stateHost,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return stateHost.UpdateExecutionContextAsync(
            new WorkflowRunExecutionContextDelta
            {
                ClearCallerCredential = true,
            },
            ct);
    }

    public static bool TryGetCredential(
        IWorkflowExecutionContext ctx,
        out WorkflowCallerCredential credential)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return WorkflowRunExecutionContextStateAccess.TryGetCallerCredential(ctx, out credential);
    }
}
