using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

// Refactor (iter115/cluster-3):
//   Old pattern: captured secure input values used a process-local runtime
//                dictionary as the authority.
//   New principle: the compatibility facade reads and writes typed secure input
//                  module state owned by the workflow actor.
internal static class SecureInputRuntimeContextAccess
{
    private static readonly TimeSpan SecureInputValueTtl = TimeSpan.FromHours(24);

    public static async Task SetCapturedValueAsync(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        string? value,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var state = SecureInputStateAccess.Load(ctx);
        if (!TryNormalize(runId, variable, out var normalizedRunId, out var normalizedVariable))
            return;

        var reference = await StoreCapturedValueReferenceAsync(
            ctx,
            normalizedRunId,
            normalizedVariable,
            value,
            ct);

        SecureInputStateAccess.SetCapturedReference(state, normalizedRunId, normalizedVariable, reference);
        await SecureInputStateAccess.SaveAsync(state, ctx, ct);
    }

    public static async Task<RuntimeSecretReference> StoreCapturedValueReferenceAsync(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        string? value,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!TryNormalize(runId, variable, out var normalizedRunId, out var normalizedVariable))
        {
            throw new ArgumentException("Workflow secure input captured value requires a run id and variable.");
        }

        var runtimeSecretStore = WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(ctx)
            ?? throw new InvalidOperationException("Workflow secure input runtime secret store is unavailable.");
        var stored = await runtimeSecretStore.PutAsync(new StoreRuntimeSecretRequest(
                CredentialSecretPurposes.WorkflowSecureInputValue,
                normalizedRunId,
                normalizedVariable,
                value ?? string.Empty,
                SecureInputValueTtl,
                ConsumeOnce: false,
                AuditReason: "workflow.secure-input-value"),
            ct);

        return stored.Reference.Clone();
    }

    public static async Task<(bool Found, string Value)> TryGetCapturedValueAsync(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!TryNormalize(runId, variable, out var normalizedRunId, out var normalizedVariable))
        {
            return (false, string.Empty);
        }

        var state = SecureInputStateAccess.Load(ctx);
        var key = $"{normalizedRunId}::{normalizedVariable}";
        if (state.Captured.TryGetValue(key, out var captured) &&
            !string.IsNullOrWhiteSpace(captured.ValueReference?.Ref))
        {
            var resolved = await WorkflowRunExecutionContextStateAccess.TryResolveRuntimeSecretAsync(
                    ctx,
                    captured.ValueReference,
                    ct);

            return resolved.Found ? (true, resolved.Secret) : (false, string.Empty);
        }

        return SecureInputStateAccess.TryGetCaptured(state, normalizedRunId, normalizedVariable, out var value)
            ? (true, value)
            : (false, string.Empty);
    }

    public static bool TryGetCapturedValue(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        out string value)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!TryNormalize(runId, variable, out var normalizedRunId, out var normalizedVariable))
        {
            value = string.Empty;
            return false;
        }

        var state = SecureInputStateAccess.Load(ctx);
        var key = $"{normalizedRunId}::{normalizedVariable}";
        if (state.Captured.TryGetValue(key, out var captured) &&
            !string.IsNullOrWhiteSpace(captured.ValueReference?.Ref))
        {
            value = string.Empty;
            return false;
        }

        return SecureInputStateAccess.TryGetCaptured(state, normalizedRunId, normalizedVariable, out value);
    }

    public static async Task<bool> RemoveCapturedValueAsync(
        IWorkflowExecutionContext ctx,
        string? runId,
        string? variable,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var state = SecureInputStateAccess.Load(ctx);
        var removed = SecureInputStateAccess.RemoveCaptured(state, runId, variable);
        if (removed)
            await SecureInputStateAccess.SaveAsync(state, ctx, ct);
        return removed;
    }

    public static Task RemoveRunAsync(
        IWorkflowExecutionContext ctx,
        string? runId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var state = SecureInputStateAccess.Load(ctx);
        SecureInputStateAccess.RemoveRun(state, runId);
        return SecureInputStateAccess.SaveAsync(state, ctx, ct);
    }

    private static bool TryNormalize(
        string? runId,
        string? variable,
        out string normalizedRunId,
        out string normalizedVariable)
    {
        normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        normalizedVariable = string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();
        return !string.IsNullOrWhiteSpace(normalizedRunId) &&
               !string.IsNullOrWhiteSpace(normalizedVariable);
    }
}
