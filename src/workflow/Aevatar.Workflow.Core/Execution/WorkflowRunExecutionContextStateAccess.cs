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
        var authority = NormalizeCallerNyxIdAuthority(credential?.NyxIdAuthority, nameof(credential));
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(credential?.BearerToken);
        var sourceReadable = WorkflowCallerCredentialTokens.ParseOptional(
            credential?.SourceReadableUserBearerToken);
        if (parsed.IsInvalid || sourceReadable.IsInvalid)
            throw new ArgumentException("Workflow caller credential bearer token is invalid.", nameof(credential));
        if (HasDurableCallerCredential(credential?.DurableCallerCredential) &&
            (parsed.IsValid || sourceReadable.IsValid))
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
        if (HasDurableCallerCredential(credential?.DurableCallerCredential))
        {
            delta.CallerCredential = new WorkflowCallerCredential
            {
                DurableCallerCredential = credential!.DurableCallerCredential.Clone(),
                NyxIdAuthority = authority,
                Kind = credential.Kind,
                DurableCredentialCleanupResponsibility =
                    WorkflowCallerCredentialCleanupResponsibility.Owner,
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

        delta.CallerCredential = new WorkflowCallerCredential
        {
            RuntimeSecretReference = new RuntimeSecretReference
            {
                Purpose = CredentialSecretPurposes.WorkflowCallerBearerToken,
                OwnerRunId = "run-1",
                OwnerStepId = WorkflowCallerCredentialRuntimeContextAccess.OwnerStepId,
            },
            SourceReadableUserBearerRuntimeSecretReference = sourceReadable.IsValid
                ? new RuntimeSecretReference
                {
                    Purpose = CredentialSecretPurposes.WorkflowCallerSourceReadableUserBearerToken,
                    OwnerRunId = "run-1",
                    OwnerStepId = WorkflowCallerCredentialRuntimeContextAccess.SourceReadableOwnerStepId,
                }
                : null,
            NyxIdAuthority = authority,
            Kind = credential!.Kind,
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
        if (HasDurableCallerCredential(callerCredential?.DurableCallerCredential))
        {
            credential = new WorkflowCallerCredential();
            return false;
        }

        if (HasRuntimeSecretReference(callerCredential?.RuntimeSecretReference))
        {
            credential = new WorkflowCallerCredential();
            return false;
        }
        if (HasRuntimeSecretReference(callerCredential?.SourceReadableUserBearerRuntimeSecretReference))
        {
            credential = new WorkflowCallerCredential();
            return false;
        }

        return TryGetLegacyCallerCredential(callerCredential, out credential);
    }

    public static bool TryGetDurableCallerCredential(
        IWorkflowExecutionContext ctx,
        out WorkflowCallerCredential credential)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var callerCredential = Get(ctx).CallerCredential;
        if (!HasDurableCallerCredential(callerCredential?.DurableCallerCredential))
        {
            credential = new WorkflowCallerCredential();
            return false;
        }

        credential = new WorkflowCallerCredential
        {
            DurableCallerCredential = callerCredential!.DurableCallerCredential.Clone(),
            NyxIdAuthority = callerCredential.NyxIdAuthority?.Clone(),
            Kind = callerCredential.Kind,
            DurableCredentialCleanupResponsibility =
                callerCredential.DurableCredentialCleanupResponsibility,
        };
        return true;
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
        var hasRuntimeSecret = HasRuntimeSecretReference(callerCredential?.RuntimeSecretReference);
        var hasSourceReadableRuntimeSecret = HasRuntimeSecretReference(
            callerCredential?.SourceReadableUserBearerRuntimeSecretReference);
        if (hasSourceReadableRuntimeSecret &&
            (!hasRuntimeSecret || callerCredential?.Kind != NyxIdCallerCredentialKind.ProxyDelegation))
        {
            return (false, new WorkflowCallerCredential());
        }

        if (HasDurableCallerCredential(callerCredential?.DurableCallerCredential))
        {
            var resolved = await TryResolveDurableCallerCredentialAsync(
                source,
                callerCredential!.DurableCallerCredential,
                ct);
            return resolved.Found
                ? (true, new WorkflowCallerCredential
                {
                    BearerToken = resolved.Secret,
                    DurableCallerCredential = callerCredential.DurableCallerCredential.Clone(),
                    NyxIdAuthority = callerCredential.NyxIdAuthority?.Clone(),
                    Kind = callerCredential.Kind,
                    DurableCredentialCleanupResponsibility =
                        callerCredential.DurableCredentialCleanupResponsibility,
                })
                : (false, new WorkflowCallerCredential());
        }

        if (hasRuntimeSecret)
        {
            var resolved = await TryResolveRuntimeSecretAsync(source, callerCredential!.RuntimeSecretReference, ct);
            if (!resolved.Found)
                return (false, new WorkflowCallerCredential());

            var sourceReadable = await TryResolveOptionalRuntimeSecretAsync(
                source,
                callerCredential.SourceReadableUserBearerRuntimeSecretReference,
                ct);
            return sourceReadable.RequiredAndMissing
                ? (false, new WorkflowCallerCredential())
                : (true, new WorkflowCallerCredential
                {
                    BearerToken = resolved.Secret,
                    SourceReadableUserBearerToken = sourceReadable.Secret ?? string.Empty,
                    NyxIdAuthority = callerCredential.NyxIdAuthority?.Clone(),
                    Kind = callerCredential.Kind,
                });
        }

        if (TryNormalizeCallerNyxIdAuthority(callerCredential?.NyxIdAuthority, out var authority))
        {
            return (true, new WorkflowCallerCredential
            {
                NyxIdAuthority = authority,
                Kind = callerCredential!.Kind,
            });
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
        var sourceReadable = WorkflowCallerCredentialTokens.ParseOptional(
            callerCredential?.SourceReadableUserBearerToken);
        if (parsed.IsValid &&
            !WorkflowCallerCredentialTokens.IsInvalidCredentialSet(
                parsed.NormalizedBearerToken,
                callerCredential?.Kind ?? NyxIdCallerCredentialKind.Unspecified,
                sourceReadable.NormalizedBearerToken))
        {
            credential = new WorkflowCallerCredential
            {
                BearerToken = parsed.NormalizedBearerToken ?? string.Empty,
                SourceReadableUserBearerToken = sourceReadable.NormalizedBearerToken ?? string.Empty,
                NyxIdAuthority = callerCredential?.NyxIdAuthority?.Clone(),
                Kind = callerCredential?.Kind ?? NyxIdCallerCredentialKind.Unspecified,
            };
            return true;
        }

        credential = new WorkflowCallerCredential();
        return false;
    }

    private static async Task<(bool RequiredAndMissing, string? Secret)> TryResolveOptionalRuntimeSecretAsync(
        object source,
        RuntimeSecretReference? reference,
        CancellationToken ct)
    {
        if (!HasRuntimeSecretReference(reference))
            return (false, null);

        var resolved = await TryResolveRuntimeSecretAsync(source, reference, ct);
        return resolved.Found
            ? (false, resolved.Secret)
            : (true, null);
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

    internal static async Task<(bool Found, string Secret)> TryResolveDurableCallerCredentialAsync(
        object source,
        DurableCallerCredentialRef? reference,
        CancellationToken ct = default)
    {
        if (!HasDurableCallerCredential(reference) ||
            string.IsNullOrWhiteSpace(reference!.Purpose) ||
            string.IsNullOrWhiteSpace(reference.OwnerScopeKey) ||
            string.IsNullOrWhiteSpace(reference.SubjectId) ||
            reference.SourceKind == DurableCallerCredentialSourceKind.WebhookBinding &&
            (!string.Equals(
                 reference.Purpose,
                 CredentialSecretPurposes.WorkflowWebhookBindingAgentKey,
                 StringComparison.Ordinal) ||
             reference.SecretReference == null ||
             string.IsNullOrWhiteSpace(reference.SecretReference.Ref)) ||
            reference.SourceKind == DurableCallerCredentialSourceKind.ChannelRegistration &&
            (!string.Equals(
                 reference.Purpose,
                 CredentialSecretPurposes.ChannelNyxIdAgentKey,
                 StringComparison.Ordinal) ||
             reference.SecretReference == null ||
             string.IsNullOrWhiteSpace(reference.SecretReference.Ref)))
        {
            return (false, string.Empty);
        }

        var vault = ResolveSecretVault(source);
        if (vault is null)
            return (false, string.Empty);

        var result = await vault.ResolveAsync(new ResolveSecretRequest(
            reference.Ref,
            reference.Purpose,
            reference.OwnerScopeKey,
            reference.SubjectId,
            "workflow-durable-caller-resolve"), ct);
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(result.Secret);
        if (!result.Resolved ||
            parsed.IsInvalid ||
            parsed.IsMissing ||
            (reference.SourceKind is
                DurableCallerCredentialSourceKind.WebhookBinding or
                DurableCallerCredentialSourceKind.ChannelRegistration) &&
            !MatchesResolvedReference(reference, result.Reference))
            return (false, string.Empty);

        return (true, parsed.NormalizedBearerToken!);
    }

    private static bool HasDurableCallerCredential(DurableCallerCredentialRef? reference) =>
        reference != null && !string.IsNullOrWhiteSpace(reference.Ref);

    private static bool MatchesResolvedReference(
        DurableCallerCredentialRef expected,
        SecretReference? actual)
    {
        if (actual == null ||
            !string.Equals(actual.Ref, expected.Ref, StringComparison.Ordinal) ||
            !string.Equals(actual.Purpose, expected.Purpose, StringComparison.Ordinal) ||
            !string.Equals(actual.OwnerScopeKey, expected.OwnerScopeKey, StringComparison.Ordinal) ||
            actual.Version <= 0 ||
            string.IsNullOrWhiteSpace(actual.Fingerprint) ||
            actual.CreatedAtUnixMs <= 0 ||
            actual.ExpiresAtUnixMs > 0 && actual.ExpiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            return false;
        }

        var descriptor = expected.SecretReference;
        return descriptor == null || string.IsNullOrWhiteSpace(descriptor.Ref) ||
               string.Equals(descriptor.Ref, actual.Ref, StringComparison.Ordinal) &&
               string.Equals(descriptor.Purpose, actual.Purpose, StringComparison.Ordinal) &&
               string.Equals(descriptor.OwnerScopeKey, actual.OwnerScopeKey, StringComparison.Ordinal) &&
               string.Equals(descriptor.Fingerprint, actual.Fingerprint, StringComparison.Ordinal) &&
               descriptor.Version == actual.Version &&
               descriptor.CreatedAtUnixMs == actual.CreatedAtUnixMs &&
               descriptor.ExpiresAtUnixMs == actual.ExpiresAtUnixMs;
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

    internal static ISecretVault? ResolveSecretVault(object source)
    {
        if (source is ISecretVaultAccessor accessor)
            return accessor.SecretVault;
        if (source is IWorkflowExecutionStateHostAccessor stateHostAccessor &&
            stateHostAccessor.StateHost is ISecretVaultAccessor stateHostSecretVaultAccessor)
        {
            return stateHostSecretVaultAccessor.SecretVault;
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
        if (!string.IsNullOrWhiteSpace(clone.CallerCredential?.SourceReadableUserBearerToken))
            clone.CallerCredential.SourceReadableUserBearerToken = string.Empty;
        if (clone.CallerCredential?.DurableCallerCredential != null)
            clone.CallerCredential.DurableCallerCredential = null;
        if (clone.CallerCredential?.NyxIdAuthority != null)
            clone.CallerCredential.NyxIdAuthority = null;
        clone.UnattendedEffectAuthorization = null;
        return clone;
    }

    internal static WorkflowCallerNyxIdAuthority? NormalizeCallerNyxIdAuthority(
        WorkflowCallerNyxIdAuthority? source,
        string parameterName)
    {
        if (source == null)
            return null;
        if (TryNormalizeCallerNyxIdAuthority(source, out var authority))
            return authority;

        throw new ArgumentException("Workflow caller NyxID authority is incomplete.", parameterName);
    }

    internal static bool TryNormalizeCallerNyxIdAuthority(
        WorkflowCallerNyxIdAuthority? source,
        out WorkflowCallerNyxIdAuthority? authority)
    {
        authority = null;
        if (source == null)
            return false;

        var platform = Normalize(source.Platform);
        var externalUserId = Normalize(source.ExternalUserId);
        var scope = Normalize(source.Scope);
        if (string.IsNullOrWhiteSpace(platform) ||
            string.IsNullOrWhiteSpace(externalUserId) ||
            string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        authority = new WorkflowCallerNyxIdAuthority
        {
            Platform = platform,
            Tenant = Normalize(source.Tenant),
            ExternalUserId = externalUserId,
            Scope = scope,
            BindingId = Normalize(source.BindingId),
        };
        return true;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
