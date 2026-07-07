using Aevatar.Foundation.Abstractions.Credentials;
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
            RuntimeSecretReference = new RuntimeSecretReference
            {
                Purpose = CredentialSecretPurposes.WorkflowCallerBearerToken,
                OwnerRunId = "run-1",
                OwnerStepId = WorkflowCallerCredentialRuntimeContextAccess.OwnerStepId,
            },
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
        if (HasRuntimeSecretReference(callerCredential?.RuntimeSecretReference))
        {
            credential = new WorkflowCallerCredential();
            return false;
        }

        return TryGetLegacyCallerCredential(callerCredential, out credential);
    }

    public static async Task<(bool Found, WorkflowCallerCredential Credential)> TryGetCallerCredentialAsync(
        IWorkflowExecutionContext ctx,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return await TryGetCallerCredentialAsync(ctx, Get(ctx).CallerCredential, ct);
    }

    public static async Task<(bool Found, WorkflowCallerCredential Credential)> TryGetCallerCredentialAsync(
        IWorkflowExecutionStateHost stateHost,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return await TryGetCallerCredentialAsync(stateHost, Get(stateHost).CallerCredential, ct);
    }

    private static async Task<(bool Found, WorkflowCallerCredential Credential)> TryGetCallerCredentialAsync(
        object source,
        WorkflowCallerCredentialState? callerCredential,
        CancellationToken ct)
    {
        if (HasRuntimeSecretReference(callerCredential?.RuntimeSecretReference))
        {
            var resolved = await TryResolveRuntimeSecretAsync(source, callerCredential!.RuntimeSecretReference, ct);
            return resolved.Found
                ? (true, new WorkflowCallerCredential { BearerToken = resolved.Secret })
                : (false, new WorkflowCallerCredential());
        }

        return TryGetLegacyCallerCredential(callerCredential, out var credential)
            ? (true, credential)
            : (false, new WorkflowCallerCredential());
    }

    private static bool TryGetLegacyCallerCredential(
        WorkflowCallerCredentialState? callerCredential,
        out WorkflowCallerCredential credential)
    {
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

    internal static async Task<(bool Found, string Secret)> TryResolveRuntimeSecretAsync(
        object source,
        RuntimeSecretReference? reference,
        CancellationToken ct = default)
    {
        if (reference == null || string.IsNullOrWhiteSpace(reference.Ref))
        {
            return (false, string.Empty);
        }

        var runtimeStore = ResolveRuntimeSecretStore(source);
        if (runtimeStore is null)
        {
            return (false, string.Empty);
        }

        var result = await runtimeStore.ResolveAsync(new ResolveRuntimeSecretRequest(
            reference.Ref,
            reference.Purpose,
            reference.OwnerRunId,
            reference.OwnerStepId,
            "workflow-runtime-resolve"), ct);
        if (!result.Resolved || string.IsNullOrWhiteSpace(result.Secret))
        {
            return (false, string.Empty);
        }

        return (true, result.Secret.Trim());
    }

    private static bool HasRuntimeSecretReference(RuntimeSecretReference? reference) =>
        reference != null && !string.IsNullOrWhiteSpace(reference.Ref);

    internal static IRuntimeSecretStore? ResolveRuntimeSecretStore(object source)
    {
        if (source is IRuntimeSecretStoreAccessor accessor)
            return accessor.RuntimeSecretStore;
        if (source is IWorkflowExecutionStateHostAccessor stateHostAccessor &&
            stateHostAccessor.StateHost is IRuntimeSecretStoreAccessor stateHostRuntimeAccessor)
        {
            return stateHostRuntimeAccessor.RuntimeSecretStore;
        }

        return null;
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
