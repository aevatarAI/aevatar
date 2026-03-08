using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowRuntimeCallbackLeaseSupport
{
    public static bool MatchesLease(
        EventEnvelope envelope,
        WorkflowRuntimeCallbackLeaseState? state)
    {
        var lease = WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(state);
        return lease != null && RuntimeCallbackEnvelopeMetadataReader.MatchesLease(envelope, lease);
    }

    public static Task CancelAsync(
        IWorkflowExecutionContext ctx,
        WorkflowRuntimeCallbackLeaseState? state,
        CancellationToken ct)
    {
        var lease = WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(state);
        return lease == null
            ? Task.CompletedTask
            : ctx.CancelDurableCallbackAsync(lease, ct);
    }
}
