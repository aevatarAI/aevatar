using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

internal static class AgentToolReceiptFactory
{
    internal const string UnknownErrorCode = "tool_outcome_unknown";
    internal const string UnknownErrorMessage = "The tool outcome could not be verified.";
    internal const string UnknownResultJson =
        "{\"status\":\"unknown\",\"message\":\"The tool outcome could not be verified.\"}";

    public static AgentToolReceipt CreateRunning(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety) =>
        CreateBase(tool, callId, toolName, callSafety, AgentToolReceiptStatus.Unspecified);

    public static AgentToolReceipt CreateResult(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        string resultJson,
        string argumentsJson = "")
    {
        AgentToolReceipt? providerReceipt;
        try
        {
            providerReceipt = tool.CreateResultReceipt(
                callId,
                toolName,
                argumentsJson ?? string.Empty,
                resultJson ?? string.Empty);
        }
        catch
        {
            return CreateUnknown(tool, callId, toolName, callSafety);
        }

        if (providerReceipt is not null)
            return NormalizeProviderResultReceipt(tool, callId, toolName, callSafety, resultJson, providerReceipt);

        return CreateUnknown(tool, callId, toolName, callSafety);
    }

    public static AgentToolReceipt CreateResult(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        string resultJson,
        AgentToolReceipt? terminalReceipt,
        string argumentsJson = "")
    {
        if (terminalReceipt is null)
            return CreateResult(tool, callId, toolName, callSafety, resultJson, argumentsJson);

        try
        {
            return NormalizeProviderResultReceipt(
                tool,
                callId,
                toolName,
                callSafety,
                resultJson,
                terminalReceipt);
        }
        catch
        {
            return CreateUnknown(tool, callId, toolName, callSafety);
        }
    }

    public static AgentToolReceipt CreateSuccess(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        string resultJson) =>
        CreateResult(tool, callId, toolName, callSafety, resultJson);

    public static AgentToolReceipt CreateError(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        string resultJson,
        string errorCode,
        string errorMessage)
    {
        var receipt = CreateBase(tool, callId, toolName, callSafety, AgentToolReceiptStatus.Error);
        receipt.ResultJson = resultJson ?? string.Empty;
        receipt.ErrorCode = errorCode ?? string.Empty;
        receipt.ErrorMessage = errorMessage ?? string.Empty;
        receipt.FailureOutcome = AgentToolFailureOutcome.CalleeConfirmed;
        return receipt;
    }

    public static AgentToolReceipt CreateApprovalRequired(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        string resultJson,
        string approvalRequestId)
    {
        var receipt = CreateBase(tool, callId, toolName, callSafety, AgentToolReceiptStatus.ApprovalRequired);
        receipt.ResultJson = resultJson ?? string.Empty;
        receipt.ApprovalRequestId = approvalRequestId ?? string.Empty;
        return receipt;
    }

    public static AgentToolReceipt CreateDenied(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        string resultJson,
        string approvalRequestId,
        string reason)
    {
        var receipt = CreateBase(tool, callId, toolName, callSafety, AgentToolReceiptStatus.Denied);
        receipt.ResultJson = resultJson ?? string.Empty;
        receipt.ApprovalRequestId = approvalRequestId ?? string.Empty;
        receipt.ErrorCode = "approval_denied";
        receipt.ErrorMessage = reason ?? string.Empty;
        receipt.FailureOutcome = AgentToolFailureOutcome.CalleeConfirmed;
        return receipt;
    }

    public static AgentToolReceipt CreateDenied(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        string resultJson,
        string errorCode,
        string errorMessage,
        string approvalRequestId = "")
    {
        var receipt = CreateBase(tool, callId, toolName, callSafety, AgentToolReceiptStatus.Denied);
        receipt.ResultJson = resultJson ?? string.Empty;
        receipt.ApprovalRequestId = approvalRequestId ?? string.Empty;
        receipt.ErrorCode = errorCode ?? string.Empty;
        receipt.ErrorMessage = errorMessage ?? string.Empty;
        receipt.FailureOutcome = AgentToolFailureOutcome.CalleeConfirmed;
        return receipt;
    }

    public static AgentToolReceipt CreateApprovalError(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        string resultJson,
        string approvalRequestId,
        string errorCode,
        string errorMessage)
    {
        var receipt = CreateBase(tool, callId, toolName, callSafety, AgentToolReceiptStatus.Error);
        receipt.ResultJson = resultJson ?? string.Empty;
        receipt.ApprovalRequestId = approvalRequestId ?? string.Empty;
        receipt.ErrorCode = errorCode ?? string.Empty;
        receipt.ErrorMessage = errorMessage ?? string.Empty;
        receipt.FailureOutcome = AgentToolFailureOutcome.CalleeConfirmed;
        return receipt;
    }

    public static AgentToolReceiptApprovalMode MapApprovalMode(ToolApprovalMode mode) =>
        mode switch
        {
            ToolApprovalMode.NeverRequire => AgentToolReceiptApprovalMode.NeverRequire,
            ToolApprovalMode.AlwaysRequire => AgentToolReceiptApprovalMode.AlwaysRequire,
            ToolApprovalMode.Auto => AgentToolReceiptApprovalMode.Auto,
            _ => AgentToolReceiptApprovalMode.Unspecified,
        };

    private static AgentToolReceipt CreateBase(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        AgentToolReceiptStatus status) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? tool.Name ?? string.Empty : toolName,
            Status = status,
            ApprovalMode = MapApprovalMode(tool.ApprovalMode),
            IsDestructive = callSafety.IsDestructive,
            SideEffectKind = NormalizeSideEffectKind(tool.SideEffectKind),
            Effect = AgentToolReceiptEffectPolicy.FromCallSafety(callSafety, tool.SideEffectKind),
        };

    private static string NormalizeSideEffectKind(string? sideEffectKind) =>
        string.IsNullOrWhiteSpace(sideEffectKind) ? string.Empty : sideEffectKind.Trim().ToLowerInvariant();

    private static AgentToolReceipt CreateUnknown(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety)
    {
        var receipt = CreateBase(
            tool,
            callId,
            toolName,
            callSafety,
            AgentToolReceiptStatus.Unspecified);
        receipt.ResultJson = UnknownResultJson;
        receipt.ErrorCode = UnknownErrorCode;
        receipt.ErrorMessage = UnknownErrorMessage;
        receipt.FailureOutcome = AgentToolFailureOutcome.OutcomeUncertain;
        return receipt;
    }

    private static AgentToolReceipt NormalizeProviderResultReceipt(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        string? resultJson,
        AgentToolReceipt receipt)
    {
        var normalized = receipt.Clone();
        normalized.CallId = callId ?? string.Empty;
        normalized.ToolName = string.IsNullOrWhiteSpace(toolName) ? tool.Name ?? string.Empty : toolName;
        normalized.ApprovalMode = MapApprovalMode(tool.ApprovalMode);
        normalized.IsDestructive = normalized.IsDestructive || callSafety.IsDestructive;
        normalized.SideEffectKind = string.IsNullOrWhiteSpace(normalized.SideEffectKind)
            ? NormalizeSideEffectKind(tool.SideEffectKind)
            : NormalizeSideEffectKind(normalized.SideEffectKind);
        normalized.Effect = AgentToolReceiptEffectPolicy.FromCallSafety(
            callSafety,
            normalized.SideEffectKind);
        if (normalized.Status == AgentToolReceiptStatus.Unspecified)
        {
            normalized.ResultJson = UnknownResultJson;
            normalized.ErrorCode = UnknownErrorCode;
            normalized.ErrorMessage = UnknownErrorMessage;
            normalized.FailureOutcome = AgentToolFailureOutcome.OutcomeUncertain;
            return normalized;
        }
        if (normalized.Status is AgentToolReceiptStatus.Error or
                AgentToolReceiptStatus.Denied or
                AgentToolReceiptStatus.AuthorizationRequired &&
            normalized.FailureOutcome == AgentToolFailureOutcome.Unspecified)
        {
            normalized.FailureOutcome = AgentToolFailureOutcome.CalleeConfirmed;
        }
        if (normalized.Status == AgentToolReceiptStatus.Success &&
            string.IsNullOrWhiteSpace(normalized.ResultJson))
        {
            normalized.ResultJson = resultJson ?? string.Empty;
        }
        return normalized;
    }
}
