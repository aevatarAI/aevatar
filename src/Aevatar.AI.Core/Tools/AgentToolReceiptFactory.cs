using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

internal static class AgentToolReceiptFactory
{
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
        var providerReceipt = tool.CreateResultReceipt(
            callId,
            toolName,
            argumentsJson ?? string.Empty,
            resultJson ?? string.Empty);
        if (providerReceipt is not null)
            return NormalizeProviderResultReceipt(tool, callId, toolName, callSafety, resultJson, providerReceipt);

        var unverifiedReceipt = CreateBase(
            tool,
            callId,
            toolName,
            callSafety,
            AgentToolReceiptStatus.Unspecified);
        unverifiedReceipt.ResultJson = resultJson ?? string.Empty;
        return unverifiedReceipt;
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
        };

    private static string NormalizeSideEffectKind(string? sideEffectKind) =>
        string.IsNullOrWhiteSpace(sideEffectKind) ? string.Empty : sideEffectKind.Trim().ToLowerInvariant();

    private static AgentToolReceipt NormalizeProviderResultReceipt(
        IAgentTool tool,
        string callId,
        string toolName,
        AgentToolCallSafety callSafety,
        string? resultJson,
        AgentToolReceipt receipt)
    {
        var normalized = receipt.Clone();
        normalized.CallId = string.IsNullOrWhiteSpace(normalized.CallId) ? callId ?? string.Empty : normalized.CallId;
        normalized.ToolName = string.IsNullOrWhiteSpace(normalized.ToolName)
            ? string.IsNullOrWhiteSpace(toolName) ? tool.Name ?? string.Empty : toolName
            : normalized.ToolName;
        if (normalized.ApprovalMode == AgentToolReceiptApprovalMode.Unspecified)
            normalized.ApprovalMode = MapApprovalMode(tool.ApprovalMode);
        normalized.IsDestructive = normalized.IsDestructive || callSafety.IsDestructive;
        normalized.SideEffectKind = string.IsNullOrWhiteSpace(normalized.SideEffectKind)
            ? NormalizeSideEffectKind(tool.SideEffectKind)
            : NormalizeSideEffectKind(normalized.SideEffectKind);
        if (normalized.Status == AgentToolReceiptStatus.Success &&
            string.IsNullOrWhiteSpace(normalized.ResultJson))
        {
            normalized.ResultJson = resultJson ?? string.Empty;
        }
        return normalized;
    }
}
