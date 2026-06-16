using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowRuntimeCallbackLeaseSupport
{
    public static bool MatchesLease(
        EventEnvelope envelope,
        WorkflowRuntimeCallbackLeaseState? state)
    {
        var lease = WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(state);
        return lease != null && RuntimeCallbackEnvelopeStateReader.MatchesLease(envelope, lease);
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

    public static Task TryCancelAsync(
        IWorkflowExecutionContext ctx,
        WorkflowRuntimeCallbackLeaseState? state,
        string operation,
        CancellationToken ct)
    {
        var lease = WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(state);
        return TryCancelAsync(ctx, lease, operation, ct);
    }

    public static Task TryCancelAsync(
        IWorkflowExecutionContext ctx,
        RuntimeCallbackLease? lease,
        string operation,
        CancellationToken ct) =>
        TryCancelAsync(ctx.CancelDurableCallbackAsync, ctx.Logger, lease, operation, ct);

    public static async Task TryCancelAsync(
        Func<RuntimeCallbackLease, CancellationToken, Task> cancelAsync,
        ILogger logger,
        RuntimeCallbackLease? lease,
        string operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cancelAsync);
        ArgumentNullException.ThrowIfNull(logger);

        if (lease == null)
            return;

        try
        {
            await cancelAsync(lease, ct);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            logger.LogDebug(
                ex,
                "{Operation} canceled while canceling callback={CallbackId} generation={Generation}",
                operation,
                lease.CallbackId,
                lease.Generation);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "{Operation} failed while canceling callback={CallbackId} generation={Generation}",
                operation,
                lease.CallbackId,
                lease.Generation);
        }
    }
}
