using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowCallerCredentialRuntimeContextAccess
{
    internal const string OwnerStepId = "workflow.caller";
    internal const string SourceReadableOwnerStepId = "workflow.caller.source-readable";
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
        var authority = WorkflowRunExecutionContextStateAccess.NormalizeCallerNyxIdAuthority(
            credential?.NyxIdAuthority,
            nameof(credential));
        var hasDurableCredential = HasDurableCallerCredential(credential?.DurableCallerCredential);
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(credential?.BearerToken);
        var sourceReadable = WorkflowCallerCredentialTokens.ParseOptional(
            credential?.SourceReadableUserBearerToken);
        if (parsed.IsInvalid || sourceReadable.IsInvalid)
            throw new ArgumentException("Workflow caller credential bearer token is invalid.", nameof(credential));
        if (hasDurableCredential && (parsed.IsValid || sourceReadable.IsValid))
            throw new ArgumentException("Workflow caller credential must not carry both durable and bearer credentials.", nameof(credential));
        if (!parsed.IsValid && sourceReadable.IsValid)
        {
            throw new ArgumentException(
                "Workflow caller source-readable bearer requires an execution credential.",
                nameof(credential));
        }
        if (sourceReadable.IsValid && credential?.Kind != NyxIdCallerCredentialKind.ProxyDelegation)
        {
            throw new ArgumentException(
                "Workflow caller source-readable bearer can supplement only a proxy delegation credential.",
                nameof(credential));
        }
        if (hasDurableCredential)
        {
            delta.CallerCredential = new WorkflowCallerCredential
            {
                DurableCallerCredential = credential!.DurableCallerCredential.Clone(),
                NyxIdAuthority = authority,
                Kind = credential.Kind,
            };
            return delta;
        }

        if (parsed.IsMissing)
        {
            if (authority != null)
            {
                delta.CallerCredential = new WorkflowCallerCredential
                {
                    NyxIdAuthority = authority,
                    Kind = credential!.Kind,
                };
            }

            return delta;
        }

        var runtimeSecretStore = WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(stateHost)
            ?? throw new InvalidOperationException("Workflow caller credential runtime secret store is unavailable.");
        RuntimeSecretReference? sourceReadableReference = null;
        if (sourceReadable.IsValid)
        {
            var sourceReadableStored = await runtimeSecretStore.PutAsync(new StoreRuntimeSecretRequest(
                CredentialSecretPurposes.WorkflowCallerSourceReadableUserBearerToken,
                WorkflowRunIdNormalizer.Normalize(stateHost.RunId),
                SourceReadableOwnerStepId,
                sourceReadable.NormalizedBearerToken ?? string.Empty,
                CallerBearerTokenTtl,
                ConsumeOnce: false,
                AuditReason: "workflow.caller-source-readable-user-bearer-token"),
                ct);
            sourceReadableReference = sourceReadableStored.Reference;
        }

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
            SourceReadableUserBearerRuntimeSecretReference = sourceReadableReference,
            NyxIdAuthority = authority,
            Kind = credential!.Kind,
        };
        return delta;
    }

    private static bool HasDurableCallerCredential(DurableCallerCredentialRef? reference) =>
        reference != null && !string.IsNullOrWhiteSpace(reference.Ref);

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
}
