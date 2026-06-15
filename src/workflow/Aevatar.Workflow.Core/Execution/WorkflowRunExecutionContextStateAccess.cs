using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Modules;

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
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(credential?.BearerToken);
        if (parsed.IsInvalid)
            throw new ArgumentException("Workflow caller credential bearer token is invalid.", nameof(credential));
        if (parsed.IsMissing)
            return delta;

        delta.CallerCredential = new WorkflowCallerCredential
        {
            BearerToken = parsed.NormalizedBearerToken ?? string.Empty,
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
            RoutePreference = Normalize(llmControl.RoutePreference),
        };
        if (llmControl.HasMaxToolRoundsOverride)
            llm.MaxToolRoundsOverride = llmControl.MaxToolRoundsOverride;

        if (string.IsNullOrWhiteSpace(llm.ModelOverride) &&
            string.IsNullOrWhiteSpace(llm.UserMemoryPrompt) &&
            string.IsNullOrWhiteSpace(llm.RoutePreference) &&
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
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(callerCredential?.BearerToken);
        if (parsed.IsValid)
        {
            credential = new WorkflowCallerCredential
            {
                BearerToken = parsed.NormalizedBearerToken ?? string.Empty,
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
               !string.IsNullOrWhiteSpace(llm.RoutePreference) ||
               llm.HasMaxToolRoundsOverride;
    }

    public static WorkflowRunExecutionContextDelta ClearWorkflowRuntimeDelta() =>
        new()
        {
            ClearWorkflowRuntime = true,
        };

    public static WorkflowRunExecutionContextDelta BuildWorkflowRuntimeDelta(WorkflowToolRuntimeContextPayload? runtimeContext)
    {
        var delta = ClearWorkflowRuntimeDelta();
        if (runtimeContext == null)
            return delta;

        var parentActorId = Normalize(runtimeContext.ParentActorId);
        var parentRunId = Normalize(runtimeContext.ParentRunId);
        var parentStepId = Normalize(runtimeContext.ParentStepId);
        if (string.IsNullOrWhiteSpace(parentActorId) ||
            string.IsNullOrWhiteSpace(parentRunId) ||
            string.IsNullOrWhiteSpace(parentStepId))
        {
            return delta;
        }

        var normalizedParentRunId = WorkflowRunIdNormalizer.Normalize(parentRunId);
        delta.WorkflowRuntime = new WorkflowToolRuntimeContextPayload
        {
            ParentActorId = parentActorId,
            ParentRunId = normalizedParentRunId,
            ParentStepId = parentStepId,
            RootRunId = string.IsNullOrWhiteSpace(runtimeContext.RootRunId)
                ? normalizedParentRunId
                : WorkflowRunIdNormalizer.Normalize(runtimeContext.RootRunId),
            Depth = Math.Max(0, runtimeContext.Depth),
        };
        return delta;
    }

    public static WorkflowToolRuntimeContext GetWorkflowRuntimeContext(
        IWorkflowExecutionContext ctx,
        string parentActorId,
        string runId,
        string stepId)
    {
        var runtime = Get(ctx).WorkflowRuntime;
        if (runtime == null ||
            string.IsNullOrWhiteSpace(runtime.ParentActorId) ||
            string.IsNullOrWhiteSpace(runtime.ParentRunId) ||
            string.IsNullOrWhiteSpace(runtime.ParentStepId))
        {
            var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
            return new WorkflowToolRuntimeContext(
                parentActorId?.Trim() ?? string.Empty,
                normalizedRunId,
                stepId?.Trim() ?? string.Empty,
                normalizedRunId,
                0);
        }

        return new WorkflowToolRuntimeContext(
            parentActorId?.Trim() ?? string.Empty,
            WorkflowRunIdNormalizer.Normalize(runId),
            stepId?.Trim() ?? string.Empty,
            WorkflowRunIdNormalizer.Normalize(runtime.RootRunId),
            Math.Max(0, runtime.Depth));
    }

    public static WorkflowRunExecutionContextState RedactedClone(WorkflowRunExecutionContextState? source)
    {
        var clone = source?.Clone() ?? new WorkflowRunExecutionContextState();
        if (!string.IsNullOrWhiteSpace(clone.CallerCredential?.BearerToken))
            clone.CallerCredential.BearerToken = string.Empty;
        return clone;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
