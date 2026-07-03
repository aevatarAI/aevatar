using System.Text.Json;
using Aevatar.AI.Abstractions;
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

        if (context.Receipt != null)
            return new FinalizedToolCallReceipt(NormalizeReceipt(context, context.Receipt), IsSynthetic: false);

        if (exception != null)
        {
            return new FinalizedToolCallReceipt(
                CreateMinimalReceipt(
                    context,
                    AgentToolReceiptStatus.Error,
                    errorCode: "tool_execution_exception",
                    errorMessage: exception.Message),
                IsSynthetic: true);
        }

        if (context.Terminate)
            return new FinalizedToolCallReceipt(CreateTerminationReceipt(context), IsSynthetic: true);

        var successReceipt = AgentToolReceiptFactory.CreateSuccess(
            context.Tool,
            context.ToolCallId,
            context.ToolName,
            context.Result ?? string.Empty);

        return new FinalizedToolCallReceipt(
            successReceipt == null
                ? CreateMinimalReceipt(context, AgentToolReceiptStatus.Success)
                : NormalizeReceipt(context, successReceipt),
            IsSynthetic: successReceipt == null);
    }

    private static AgentToolReceipt CreateTerminationReceipt(ToolCallContext context)
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
            status,
            context.PendingApproval?.ApprovalRequestId ?? string.Empty,
            errorCode,
            errorMessage);
    }

    private static AgentToolReceipt NormalizeReceipt(ToolCallContext context, AgentToolReceipt receipt)
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
        normalized.IsDestructive = normalized.IsDestructive || context.Tool.IsDestructive;
        normalized.SideEffectKind = NormalizeSideEffectKind(
            string.IsNullOrWhiteSpace(normalized.SideEffectKind)
                ? context.Tool.SideEffectKind
                : normalized.SideEffectKind);
        return normalized;
    }

    private static AgentToolReceipt CreateMinimalReceipt(
        ToolCallContext context,
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
            IsDestructive = context.Tool.IsDestructive,
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
