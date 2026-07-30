using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;

namespace Aevatar.AI.Core.Auditing;

public sealed record FinalizedToolCallReceipt(
    AgentToolReceipt Receipt,
    bool IsSynthetic);

public static class ToolCallReceiptFinalizer
{
    public static FinalizedToolCallReceipt Finalize(ToolCallContext context, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var callSafety = context.Tool.GetCallSafety(context.ArgumentsJson);

        if (context.Receipt != null)
            return new FinalizedToolCallReceipt(
                NormalizeReceipt(context, context.Receipt, callSafety),
                IsSynthetic: false);

        if (exception != null)
        {
            return new FinalizedToolCallReceipt(
                CreateMinimalReceipt(
                    context,
                    callSafety,
                    AgentToolReceiptStatus.Error,
                    errorCode: ResolveExceptionErrorCode(exception),
                    errorMessage: ResolveSafeExceptionClass(exception)),
                IsSynthetic: true);
        }

        if (context.Terminate)
            return new FinalizedToolCallReceipt(
                CreateTerminationReceipt(context, callSafety),
                IsSynthetic: true);

        var successReceipt = AgentToolReceiptFactory.CreateSuccess(
            context.Tool,
            context.ToolCallId,
            context.ToolName,
            callSafety,
            context.Result ?? string.Empty,
            context.ArgumentsJson);

        return new FinalizedToolCallReceipt(
            successReceipt == null
                ? CreateMinimalReceipt(context, callSafety, AgentToolReceiptStatus.Success)
                : NormalizeReceipt(context, successReceipt, callSafety),
            IsSynthetic: successReceipt == null);
    }

    private static AgentToolReceipt CreateTerminationReceipt(
        ToolCallContext context,
        AgentToolCallSafety callSafety)
    {
        var errorCode = ExtractErrorCode(context.Result) ?? ResolveTerminationErrorCode(context);
        var errorMessage = context.TerminationReason ?? ExtractErrorMessage(context.Result) ?? string.Empty;
        var status = context.TerminationKind switch
        {
            ToolCallTerminationKind.ApprovalDenied => AgentToolReceiptStatus.Denied,
            ToolCallTerminationKind.ApprovalPending => AgentToolReceiptStatus.ApprovalRequired,
            ToolCallTerminationKind.MiddlewareTerminated when string.Equals(
                errorCode,
                "credential_denied",
                StringComparison.Ordinal) => AgentToolReceiptStatus.Denied,
            _ => AgentToolReceiptStatus.Error,
        };

        return CreateMinimalReceipt(
            context,
            callSafety,
            status,
            context.PendingApproval?.ApprovalRequestId ?? string.Empty,
            errorCode,
            errorMessage);
    }

    private static AgentToolReceipt NormalizeReceipt(
        ToolCallContext context,
        AgentToolReceipt receipt,
        AgentToolCallSafety callSafety)
    {
        var normalized = receipt.Clone();
        if (string.IsNullOrWhiteSpace(normalized.CallId))
            normalized.CallId = context.ToolCallId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized.ToolName))
            normalized.ToolName = string.IsNullOrWhiteSpace(context.ToolName)
                ? context.Tool.Name ?? string.Empty
                : context.ToolName;
        if (normalized.Status == AgentToolReceiptStatus.Unspecified)
            normalized.Status = AgentToolReceiptStatus.Success;
        if (normalized.ApprovalMode == AgentToolReceiptApprovalMode.Unspecified)
            normalized.ApprovalMode = AgentToolReceiptFactory.MapApprovalMode(context.Tool.ApprovalMode);
        normalized.IsDestructive = normalized.IsDestructive || callSafety.IsDestructive;
        normalized.SideEffectKind = NormalizeSideEffectKind(
            string.IsNullOrWhiteSpace(normalized.SideEffectKind)
                ? context.Tool.SideEffectKind
                : normalized.SideEffectKind);
        return normalized;
    }

    private static AgentToolReceipt CreateMinimalReceipt(
        ToolCallContext context,
        AgentToolCallSafety callSafety,
        AgentToolReceiptStatus status,
        string approvalRequestId = "",
        string errorCode = "",
        string errorMessage = "") =>
        new()
        {
            CallId = context.ToolCallId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(context.ToolName)
                ? context.Tool.Name ?? string.Empty
                : context.ToolName,
            Status = status,
            ApprovalMode = AgentToolReceiptFactory.MapApprovalMode(context.Tool.ApprovalMode),
            IsDestructive = callSafety.IsDestructive,
            SideEffectKind = NormalizeSideEffectKind(context.Tool.SideEffectKind),
            ApprovalRequestId = approvalRequestId ?? string.Empty,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
        };

    private static string ResolveTerminationErrorCode(ToolCallContext context) =>
        context.TerminationKind switch
        {
            ToolCallTerminationKind.ApprovalDenied => "approval_denied",
            ToolCallTerminationKind.ApprovalTimedOut => "approval_timeout",
            ToolCallTerminationKind.ApprovalPending => string.Empty,
            ToolCallTerminationKind.MiddlewareTerminated => "middleware_terminated",
            _ => "tool_call_terminated",
        };

    private static string ResolveSafeExceptionClass(Exception exception) =>
        exception switch
        {
            CodexExecutionException => nameof(CodexExecutionException),
            OperationCanceledException => nameof(OperationCanceledException),
            TimeoutException => nameof(TimeoutException),
            HttpRequestException => nameof(HttpRequestException),
            ArgumentException => nameof(ArgumentException),
            InvalidOperationException => nameof(InvalidOperationException),
            NotSupportedException => nameof(NotSupportedException),
            _ => nameof(Exception),
        };

    private static string ResolveExceptionErrorCode(Exception exception) =>
        exception is CodexExecutionException codexException
            ? CodexExecutionAuditFailureSemantics.From(codexException.Failure.Kind)
            : "tool_execution_exception";

    private static string NormalizeSideEffectKind(string? sideEffectKind) =>
        string.IsNullOrWhiteSpace(sideEffectKind) ? string.Empty : sideEffectKind.Trim().ToLowerInvariant();

    private static string? ExtractErrorCode(string? resultJson) =>
        TryExtractString(resultJson, "code") ?? TryExtractString(resultJson, "error_code");

    private static string? ExtractErrorMessage(string? resultJson) =>
        TryExtractString(resultJson, "message") ?? TryExtractString(resultJson, "error");

    private static string? TryExtractString(string? resultJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class CodexExecutionAuditFailureSemantics
{
    public static string From(CodexExecutionFailureKind kind) =>
        kind switch
        {
            CodexExecutionFailureKind.TargetNotConfigured => "codex_execution_target_not_configured",
            CodexExecutionFailureKind.AdmissionDenied => "codex_execution_admission_denied",
            CodexExecutionFailureKind.LlmProviderNotConnected => "codex_execution_llm_provider_not_connected",
            CodexExecutionFailureKind.CapacityUnavailable => "codex_execution_capacity_unavailable",
            CodexExecutionFailureKind.ProvisioningFailed => "codex_execution_provisioning_failed",
            CodexExecutionFailureKind.ReadinessFailed => "codex_execution_readiness_failed",
            CodexExecutionFailureKind.IsolationUnavailable => "codex_execution_isolation_unavailable",
            CodexExecutionFailureKind.MalformedOutput => "codex_execution_malformed_output",
            CodexExecutionFailureKind.TerminalFailure => "codex_execution_terminal_failure",
            CodexExecutionFailureKind.TimedOut => "codex_execution_timed_out",
            CodexExecutionFailureKind.Cancelled => "codex_execution_cancelled",
            CodexExecutionFailureKind.CleanupFailed => "codex_execution_cleanup_failed",
            _ => "tool_execution_exception",
        };

    public static bool IsOwned(string? code) =>
        code is
            "codex_execution_target_not_configured" or
            "codex_execution_admission_denied" or
            "codex_execution_llm_provider_not_connected" or
            "codex_execution_capacity_unavailable" or
            "codex_execution_provisioning_failed" or
            "codex_execution_readiness_failed" or
            "codex_execution_isolation_unavailable" or
            "codex_execution_malformed_output" or
            "codex_execution_terminal_failure" or
            "codex_execution_timed_out" or
            "codex_execution_cancelled" or
            "codex_execution_cleanup_failed";
}
