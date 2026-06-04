using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Execution;

// Refactor (iter159/cluster-613-first):
//   Old pattern: NyxID bearer entered workflow durable + pending approval surface.
//   New principle: request bearer scrubbed at envelope/state/continuation; only durable model/route controls remain.
internal static class WorkflowRunExecutionContextStateAccess
{
    public static WorkflowRunExecutionContextState Get(IWorkflowExecutionStateHost stateHost)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return stateHost.ExecutionContextSnapshot;
    }

    public static WorkflowRunExecutionContextState Get(IWorkflowExecutionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx is IWorkflowExecutionStateHost stateHost)
            return stateHost.ExecutionContextSnapshot;
        if (ctx is IWorkflowExecutionStateHostAccessor stateHostAccessor)
            return stateHostAccessor.StateHost.ExecutionContextSnapshot;

        return new WorkflowRunExecutionContextState();
    }

    public static Task ClearAsync(
        IWorkflowExecutionStateHost stateHost,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return stateHost.ClearExecutionContextAsync(ct);
    }

    public static WorkflowRunExecutionContextDelta BuildCallerCredentialDelta(WorkflowCallerCredential? credential)
    {
        var delta = new WorkflowRunExecutionContextDelta
        {
            ClearCallerCredential = true,
        };
        var normalized = Normalize(credential?.NyxIdBearer);
        if (string.IsNullOrWhiteSpace(normalized))
            return delta;

        delta.CallerCredential = new WorkflowCallerCredential
        {
            NyxIdBearer = normalized,
        };

        return delta;
    }

    public static Task ApplyLlmControlAsync(
        IWorkflowExecutionStateHost stateHost,
        WorkflowLlmControlContext? llmControl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        var delta = BuildLlmControlDelta(llmControl);
        return stateHost.UpdateExecutionContextAsync(delta, ct);
    }

    public static WorkflowRunExecutionContextDelta BuildLlmControlDelta(WorkflowLlmControlContext? llmControl)
    {
        var delta = new WorkflowRunExecutionContextDelta
        {
            ClearLlm = true,
        };
        if (llmControl == null)
            return delta;

        var llm = new WorkflowRunLlmExecutionContextDelta
        {
            ModelOverride = Normalize(llmControl.ModelOverride),
            UserMemoryPrompt = Normalize(llmControl.UserMemoryPrompt),
        };
        if (llmControl.HasMaxToolRoundsOverride)
            llm.MaxToolRoundsOverride = llmControl.MaxToolRoundsOverride;

        if (string.IsNullOrWhiteSpace(llm.ModelOverride) &&
            string.IsNullOrWhiteSpace(llm.UserMemoryPrompt) &&
            !llm.HasMaxToolRoundsOverride)
        {
            return delta;
        }

        delta.Llm = llm;
        return delta;
    }

    public static bool TryGetCallerCredential(
        IWorkflowExecutionContext ctx,
        out WorkflowCallerCredential credential)
    {
        var callerCredential = Get(ctx).CallerCredential;
        if (!string.IsNullOrWhiteSpace(callerCredential?.NyxIdBearer))
        {
            credential = new WorkflowCallerCredential
            {
                NyxIdBearer = callerCredential.NyxIdBearer.Trim(),
            };
            return true;
        }

        credential = new WorkflowCallerCredential();
        return false;
    }

    public static bool TryGetLlm(
        IWorkflowExecutionContext ctx,
        out WorkflowLlmExecutionContextState llm)
    {
        llm = Get(ctx).Llm ?? new WorkflowLlmExecutionContextState();
        return !string.IsNullOrWhiteSpace(llm.ModelOverride) ||
               !string.IsNullOrWhiteSpace(llm.UserMemoryPrompt) ||
               llm.HasMaxToolRoundsOverride;
    }

    public static WorkflowRunExecutionContextState RedactedClone(WorkflowRunExecutionContextState? source)
    {
        var clone = source?.Clone() ?? new WorkflowRunExecutionContextState();
        if (!string.IsNullOrWhiteSpace(clone.CallerCredential?.NyxIdBearer))
            clone.CallerCredential.NyxIdBearer = string.Empty;
        return clone;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
