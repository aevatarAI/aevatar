using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowCallerCredentialRuntimeContextAccess
{
    internal const string OwnerStepId = "workflow.caller";
    private static readonly TimeSpan CallerBearerTokenTtl = TimeSpan.FromHours(24);

    public static async Task<WorkflowRunExecutionContextDelta> BuildCredentialDeltaAsync(
        IWorkflowExecutionStateHost stateHost,
        WorkflowCallerCredential? credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);

        var delta = new WorkflowRunExecutionContextDelta
        {
            ClearCallerCredential = true,
        };
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(credential?.BearerToken);
        if (parsed.IsInvalid)
            throw new ArgumentException("Workflow caller credential bearer token is invalid.", nameof(credential));
        if (HasNyxIdCredentialSource(credential?.NyxId))
        {
            delta.CallerCredential = new WorkflowCallerCredential
            {
                NyxId = NormalizeNyxIdCredentialSource(credential!.NyxId),
            };
            return delta;
        }
        if (parsed.IsMissing)
            return delta;

        var runtimeSecretStore = WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(stateHost)
            ?? throw new InvalidOperationException("Workflow caller credential runtime secret store is unavailable.");
        var stored = await runtimeSecretStore.PutAsync(new StoreRuntimeSecretRequest(
            CredentialSecretPurposes.WorkflowCallerBearerToken,
            WorkflowRunIdNormalizer.Normalize(stateHost.RunId),
            OwnerStepId,
            parsed.NormalizedBearerToken ?? string.Empty,
            CallerBearerTokenTtl,
            ConsumeOnce: false,
            AuditReason: "workflow.caller-bearer-token"),
            ct);

        delta.CallerCredential = new WorkflowCallerCredential
        {
            RuntimeSecretReference = stored.Reference,
        };
        return delta;
    }

    public static Task SetCredentialAsync(
        IWorkflowExecutionStateHost stateHost,
        WorkflowCallerCredential? credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return SetCredentialCoreAsync(stateHost, credential, ct);
    }

    private static async Task SetCredentialCoreAsync(
        IWorkflowExecutionStateHost stateHost,
        WorkflowCallerCredential? credential,
        CancellationToken ct)
    {
        var delta = await BuildCredentialDeltaAsync(stateHost, credential, ct);
        await stateHost.UpdateExecutionContextAsync(delta, ct);
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

    public static async Task<(bool Found, WorkflowCallerCredential Credential)> TryGetCredentialAsync(
        IWorkflowExecutionContext ctx,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return await WorkflowRunExecutionContextStateAccess.TryGetCallerCredentialAsync(ctx, ct);
    }

    public static async Task<(bool Found, WorkflowCallerCredential Credential)> TryGetCredentialAsync(
        IWorkflowExecutionStateHost stateHost,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stateHost);
        return await WorkflowRunExecutionContextStateAccess.TryGetCallerCredentialAsync(stateHost, ct);
    }

    internal static bool HasNyxIdCredentialSource(WorkflowNyxIdCredentialSource? source) =>
        source?.Subject != null &&
        !string.IsNullOrWhiteSpace(source.Subject.Platform) &&
        !string.IsNullOrWhiteSpace(source.Subject.Tenant) &&
        !string.IsNullOrWhiteSpace(source.Subject.ExternalUserId) &&
        !string.IsNullOrWhiteSpace(source.Scope);

    internal static WorkflowNyxIdCredentialSource NormalizeNyxIdCredentialSource(
        WorkflowNyxIdCredentialSource source)
    {
        if (!HasNyxIdCredentialSource(source))
            throw new ArgumentException("Workflow caller NyxID credential source is incomplete.", nameof(source));

        return new WorkflowNyxIdCredentialSource
        {
            Subject = new WorkflowNyxIdSubjectRef
            {
                Platform = source.Subject.Platform.Trim(),
                Tenant = source.Subject.Tenant.Trim(),
                ExternalUserId = source.Subject.ExternalUserId.Trim(),
            },
            Scope = source.Scope.Trim(),
        };
    }
}
