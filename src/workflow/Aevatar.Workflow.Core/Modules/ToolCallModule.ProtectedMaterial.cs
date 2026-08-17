using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Execution;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

public sealed partial class ToolCallModule
{
    internal const string ProtectedMaterialSchema = "tool-call-protected-material.v1";
    internal static readonly TimeSpan ProtectedMaterialTimeToLive = TimeSpan.FromHours(24);

    internal const string ProtectedMaterialAuditReason = "workflow.tool-call-protected-material";

    internal static class ToolCallProtectedMaterialErrorCodes
    {
        internal const string InvalidIdentity = "tool_call_protected_material_invalid_identity";
        internal const string InvalidSchema = "tool_call_protected_material_invalid_schema";
        internal const string StoreUnavailable = "tool_call_protected_material_store_unavailable";
        internal const string StoreFailed = "tool_call_protected_material_store_failed";
        internal const string InvalidReference = "tool_call_protected_material_invalid_reference";
        internal const string ResolveFailed = "tool_call_protected_material_resolve_failed";
        internal const string Unavailable = "tool_call_protected_material_unavailable";
        internal const string InvalidEncoding = "tool_call_protected_material_invalid_encoding";
        internal const string SchemaMismatch = "tool_call_protected_material_schema_mismatch";
        internal const string DigestMismatch = "tool_call_protected_material_digest_mismatch";
        internal const string IdentityMismatch = "tool_call_protected_material_identity_mismatch";
    }

    internal sealed record ToolCallProtectedMaterialResolution(
        ToolCallProtectedMaterial? Material,
        string ErrorCode,
        bool IsTransientFailure)
    {
        internal bool Resolved => Material != null && string.IsNullOrEmpty(ErrorCode);

        internal static ToolCallProtectedMaterialResolution Success(ToolCallProtectedMaterial material) =>
            new(material, string.Empty, IsTransientFailure: false);

        internal static ToolCallProtectedMaterialResolution Failed(
            string errorCode,
            bool isTransientFailure = false) =>
            new(null, errorCode, isTransientFailure);
    }

#pragma warning disable CS0612 // Legacy fields remain readable only so every new actor-state write can scrub them.
    internal static void ScrubLegacyPayloadFields(ToolCallModuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (var pending in state.PendingApprovals.Values)
        {
            pending.ArgumentsJson = string.Empty;
            pending.Input = string.Empty;
            pending.InputFileRefs.Clear();
            pending.IdempotencyKey = string.Empty;
            pending.ExternalInvocation = null;
            pending.DisplayName = string.Empty;
        }

        foreach (var pending in state.PendingExecutions.Values)
        {
            pending.ArgumentsJson = string.Empty;
            pending.InputFileRefs.Clear();
            pending.IdempotencyKey = string.Empty;
            pending.ExternalInvocation = null;
            pending.DisplayName = string.Empty;
        }
    }
#pragma warning restore CS0612

    internal static ToolCallProtectedMaterial BuildProtectedMaterial(
        StepRequestEvent request,
        string runId,
        string toolName,
        string callId,
        string approvalRequestId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var material = new ToolCallProtectedMaterial
        {
            SchemaVersion = ProtectedMaterialSchema,
            RunId = NormalizeRequired(runId),
            StepId = NormalizeRequired(request.StepId),
            ExecutionId = NormalizeRequired(request.ExecutionId),
            ToolName = NormalizeRequired(toolName),
            CallId = NormalizeRequired(callId),
            ApprovalRequestId = NormalizeRequired(approvalRequestId),
            ArgumentsJson = ResolveArgumentsJson(request),
            Input = request.Input ?? string.Empty,
            IdempotencyKey = request.IdempotencyKey ?? string.Empty,
            ExternalInvocation = request.ExternalInvocation?.Clone(),
            DisplayName = ResolveStepDisplayName(request.DisplayName, request.StepId),
        };
        material.InputFileRefs.Add(request.InputFileRefs.Select(static fileRef => fileRef.Clone()));

        if (!HasValidMaterialIdentity(material))
            throw new InvalidOperationException(ToolCallProtectedMaterialErrorCodes.InvalidIdentity);

        return material;
    }

    internal static string ComputeProtectedMaterialDigest(ToolCallProtectedMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return Convert.ToHexString(SHA256.HashData(SerializeProtectedMaterial(material))).ToLowerInvariant();
    }

    internal static async Task<RuntimeSecretReference> StoreProtectedMaterialAsync(
        ToolCallProtectedMaterial material,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(ctx);

        if (!string.Equals(material.SchemaVersion, ProtectedMaterialSchema, StringComparison.Ordinal))
            throw new InvalidOperationException(ToolCallProtectedMaterialErrorCodes.InvalidSchema);
        if (!HasValidMaterialIdentity(material))
            throw new InvalidOperationException(ToolCallProtectedMaterialErrorCodes.InvalidIdentity);

        var runtimeSecretStore = WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(ctx);
        if (runtimeSecretStore == null)
            throw new InvalidOperationException(ToolCallProtectedMaterialErrorCodes.StoreUnavailable);

        try
        {
            var stored = await runtimeSecretStore.PutAsync(
                new StoreRuntimeSecretRequest(
                    CredentialSecretPurposes.WorkflowToolCallProtectedMaterial,
                    material.RunId,
                    material.StepId,
                    Convert.ToBase64String(SerializeProtectedMaterial(material)),
                    ProtectedMaterialTimeToLive,
                    ConsumeOnce: false,
                    AuditReason: ProtectedMaterialAuditReason),
                ct);
            var reference = stored.Reference;
            if (!MatchesReferenceIdentity(reference, material.RunId, material.StepId))
                throw new InvalidOperationException(ToolCallProtectedMaterialErrorCodes.InvalidReference);

            return reference.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException exception) when (
            IsProtectedMaterialErrorCode(exception.Message))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(ToolCallProtectedMaterialErrorCodes.StoreFailed, exception);
        }
    }

    internal static async Task<ToolCallProtectedMaterialResolution> ResolveAndVerifyProtectedMaterialAsync(
        RuntimeSecretReference? reference,
        string expectedDigestSha256,
        string runId,
        string stepId,
        string executionId,
        string callId,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var normalizedRunId = NormalizeRequired(runId);
        var normalizedStepId = NormalizeRequired(stepId);
        var normalizedExecutionId = NormalizeRequired(executionId);
        var normalizedCallId = NormalizeRequired(callId);
        if (normalizedRunId.Length == 0 ||
            normalizedStepId.Length == 0 ||
            normalizedCallId.Length == 0)
        {
            return ToolCallProtectedMaterialResolution.Failed(
                ToolCallProtectedMaterialErrorCodes.InvalidIdentity);
        }

        if (!MatchesReferenceIdentity(reference, normalizedRunId, normalizedStepId))
        {
            return ToolCallProtectedMaterialResolution.Failed(
                ToolCallProtectedMaterialErrorCodes.InvalidReference);
        }

        var runtimeSecretStore = WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(ctx);
        if (runtimeSecretStore == null)
        {
            return ToolCallProtectedMaterialResolution.Failed(
                ToolCallProtectedMaterialErrorCodes.StoreUnavailable,
                isTransientFailure: true);
        }

        ResolveRuntimeSecretResult resolved;
        try
        {
            resolved = await runtimeSecretStore.ResolveAsync(
                new ResolveRuntimeSecretRequest(
                    reference!.Ref,
                    reference.Purpose,
                    reference.OwnerRunId,
                    reference.OwnerStepId,
                    ProtectedMaterialAuditReason),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ToolCallProtectedMaterialResolution.Failed(
                ToolCallProtectedMaterialErrorCodes.ResolveFailed,
                isTransientFailure: true);
        }

        if (!resolved.Resolved ||
            string.IsNullOrWhiteSpace(resolved.Secret) ||
            !MatchesProtectedMaterialReference(reference!, resolved.Reference))
        {
            return ToolCallProtectedMaterialResolution.Failed(
                ToolCallProtectedMaterialErrorCodes.Unavailable);
        }

        ToolCallProtectedMaterial material;
        try
        {
            material = ToolCallProtectedMaterial.Parser.ParseFrom(
                Convert.FromBase64String(resolved.Secret));
        }
        catch (FormatException)
        {
            return ToolCallProtectedMaterialResolution.Failed(
                ToolCallProtectedMaterialErrorCodes.InvalidEncoding);
        }
        catch (InvalidProtocolBufferException)
        {
            return ToolCallProtectedMaterialResolution.Failed(
                ToolCallProtectedMaterialErrorCodes.InvalidEncoding);
        }

        if (!string.Equals(material.SchemaVersion, ProtectedMaterialSchema, StringComparison.Ordinal))
        {
            return ToolCallProtectedMaterialResolution.Failed(
                ToolCallProtectedMaterialErrorCodes.SchemaMismatch);
        }

        if (!FixedTimeDigestEquals(
                ComputeProtectedMaterialDigest(material),
                NormalizeRequired(expectedDigestSha256)))
        {
            return ToolCallProtectedMaterialResolution.Failed(
                ToolCallProtectedMaterialErrorCodes.DigestMismatch);
        }

        if (!HasValidMaterialIdentity(material) ||
            !string.Equals(material.RunId, normalizedRunId, StringComparison.Ordinal) ||
            !string.Equals(material.StepId, normalizedStepId, StringComparison.Ordinal) ||
            !string.Equals(material.ExecutionId, normalizedExecutionId, StringComparison.Ordinal) ||
            !string.Equals(material.CallId, normalizedCallId, StringComparison.Ordinal))
        {
            return ToolCallProtectedMaterialResolution.Failed(
                ToolCallProtectedMaterialErrorCodes.IdentityMismatch);
        }

        return ToolCallProtectedMaterialResolution.Success(material);
    }

    internal static async Task<bool> RevokeProtectedMaterialAsync(
        RuntimeSecretReference? reference,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return await RevokeProtectedMaterialAsync(
            reference,
            WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(ctx),
            ct);
    }

    internal static async Task<bool> RevokeProtectedMaterialAsync(
        RuntimeSecretReference? reference,
        IRuntimeSecretStore? runtimeSecretStore,
        CancellationToken ct)
    {
        if (!IsProtectedMaterialReference(reference))
            return false;
        if (runtimeSecretStore == null)
            return false;

        try
        {
            var revoked = await runtimeSecretStore.RevokeAsync(
                new RevokeRuntimeSecretRequest(
                    reference!.Ref,
                    reference.Purpose,
                    reference.OwnerRunId,
                    reference.OwnerStepId,
                    ProtectedMaterialAuditReason),
                ct);
            return revoked.Revoked;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static Task<bool> RevokeOrConfirmProtectedMaterialUnavailableAsync(
        RuntimeSecretReference? reference,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return RevokeOrConfirmProtectedMaterialUnavailableAsync(
            reference,
            WorkflowRunExecutionContextStateAccess.ResolveRuntimeSecretStore(ctx),
            ct);
    }

    internal static async Task<bool> RevokeOrConfirmProtectedMaterialUnavailableAsync(
        RuntimeSecretReference? reference,
        IRuntimeSecretStore? runtimeSecretStore,
        CancellationToken ct)
    {
        if (reference == null)
            return true;

        if (!IsProtectedMaterialReference(reference))
            return false;

        if (runtimeSecretStore == null)
            return false;

        if (await RevokeProtectedMaterialAsync(reference, runtimeSecretStore, ct))
            return true;

        try
        {
            var resolved = await runtimeSecretStore.ResolveAsync(
                new ResolveRuntimeSecretRequest(
                    reference.Ref,
                    reference.Purpose,
                    reference.OwnerRunId,
                    reference.OwnerStepId,
                    ProtectedMaterialAuditReason),
                ct);
            return !resolved.Resolved;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task TryCleanupUnownedProtectedMaterialAfterStateSaveFailureAsync(
        RuntimeSecretReference? reference,
        IWorkflowExecutionContext ctx,
        Exception saveException)
    {
        if (reference == null)
            return;

        try
        {
            var authoritativeState = WorkflowExecutionStateAccess.Load<ToolCallModuleState>(ctx, ModuleStateKey);
            if (DurableStateOwnsProtectedMaterialReference(authoritativeState, reference))
                return;

            if (!await RevokeOrConfirmProtectedMaterialUnavailableAsync(
                    reference,
                    ctx,
                    CancellationToken.None))
            {
                TryLogProtectedMaterialCleanupFailure(
                    ctx,
                    reference,
                    saveException,
                    exception: null);
            }
        }
        catch (Exception exception)
        {
            TryLogProtectedMaterialCleanupFailure(ctx, reference, saveException, exception);
        }
    }

    private static bool DurableStateOwnsProtectedMaterialReference(
        ToolCallModuleState state,
        RuntimeSecretReference expected) =>
        state.PendingApprovals.Values.Any(pending =>
            MatchesProtectedMaterialReference(expected, pending.ProtectedMaterialReference)) ||
        state.PendingExecutions.Values.Any(pending =>
            MatchesProtectedMaterialReference(expected, pending.ProtectedMaterialReference)) ||
        state.PendingOperations.Values.Any(pending =>
            MatchesProtectedMaterialReference(expected, pending.ProtectedMaterialReference)) ||
        state.Completions.Any(completion =>
            MatchesProtectedMaterialReference(expected, completion.ProtectedMaterialReference));

    private static void TryLogProtectedMaterialCleanupFailure(
        IWorkflowExecutionContext ctx,
        RuntimeSecretReference reference,
        Exception saveException,
        Exception? exception)
    {
        saveException.Data["WorkflowToolCallProtectedMaterialCleanupFailure"] =
            exception?.ToString() ?? "Protected material cleanup remains unconfirmed.";
        try
        {
            ctx.Logger.LogWarning(
                exception,
                "ToolCall: protected material cleanup after state save failure remains unconfirmed run={RunId} step={StepId}",
                reference.OwnerRunId,
                reference.OwnerStepId);
        }
        catch (Exception loggingException)
        {
            saveException.Data["WorkflowToolCallProtectedMaterialCleanupLoggingFailure"] =
                loggingException.ToString();
        }
    }

    private static byte[] SerializeProtectedMaterial(ToolCallProtectedMaterial material)
    {
        using var stream = new MemoryStream(material.CalculateSize());
        using (var output = new CodedOutputStream(stream, leaveOpen: true) { Deterministic = true })
        {
            material.WriteTo(output);
            output.Flush();
        }

        return stream.ToArray();
    }

    private static bool HasValidMaterialIdentity(ToolCallProtectedMaterial material) =>
        !string.IsNullOrWhiteSpace(material.RunId) &&
        !string.IsNullOrWhiteSpace(material.StepId) &&
        !string.IsNullOrWhiteSpace(material.ToolName) &&
        !string.IsNullOrWhiteSpace(material.CallId);

    private static bool IsProtectedMaterialReference(RuntimeSecretReference? reference) =>
        reference != null &&
        !string.IsNullOrWhiteSpace(reference.Ref) &&
        string.Equals(
            reference.Purpose,
            CredentialSecretPurposes.WorkflowToolCallProtectedMaterial,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(reference.OwnerRunId) &&
        !string.IsNullOrWhiteSpace(reference.OwnerStepId) &&
        !reference.ConsumeOnce;

    private static bool MatchesReferenceIdentity(
        RuntimeSecretReference? reference,
        string runId,
        string stepId) =>
        IsProtectedMaterialReference(reference) &&
        string.Equals(reference!.OwnerRunId, runId, StringComparison.Ordinal) &&
        string.Equals(reference.OwnerStepId, stepId, StringComparison.Ordinal);

    private static bool MatchesProtectedMaterialReference(
        RuntimeSecretReference expected,
        RuntimeSecretReference? actual) =>
        actual != null &&
        string.Equals(actual.Ref, expected.Ref, StringComparison.Ordinal) &&
        string.Equals(actual.Purpose, expected.Purpose, StringComparison.Ordinal) &&
        string.Equals(actual.OwnerRunId, expected.OwnerRunId, StringComparison.Ordinal) &&
        string.Equals(actual.OwnerStepId, expected.OwnerStepId, StringComparison.Ordinal) &&
        string.Equals(actual.Fingerprint, expected.Fingerprint, StringComparison.Ordinal) &&
        actual.ExpiresAtUnixMs == expected.ExpiresAtUnixMs &&
        actual.ConsumeOnce == expected.ConsumeOnce;

    private static bool FixedTimeDigestEquals(string left, string right)
    {
        if (!IsSha256Hex(left) || !IsSha256Hex(right))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsProtectedMaterialErrorCode(string message) =>
        message.StartsWith("tool_call_protected_material_", StringComparison.Ordinal);
}
