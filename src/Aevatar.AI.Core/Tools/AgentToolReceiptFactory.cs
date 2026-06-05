using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

internal static class AgentToolReceiptFactory
{
    public static bool IsReceiptWorthy(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.ApprovalMode != ToolApprovalMode.NeverRequire ||
               tool.IsDestructive ||
               !string.IsNullOrWhiteSpace(tool.SideEffectKind);
    }

    public static AgentToolReceipt? CreateSuccess(
        IAgentTool tool,
        string callId,
        string toolName,
        string resultJson)
    {
        if (!IsReceiptWorthy(tool))
            return null;

        var providerReceipt = tool.CreateSuccessReceipt(callId, toolName, resultJson ?? string.Empty);
        if (providerReceipt is not null)
            return NormalizeProviderSuccessReceipt(tool, callId, toolName, resultJson, providerReceipt);

        var receipt = CreateBase(tool, callId, toolName, AgentToolReceiptStatus.Success);
        receipt.ResultJson = resultJson ?? string.Empty;
        return receipt;
    }

    public static AgentToolReceipt? CreateError(
        IAgentTool tool,
        string callId,
        string toolName,
        string resultJson,
        string errorCode,
        string errorMessage)
    {
        if (!IsReceiptWorthy(tool))
            return null;

        var receipt = CreateBase(tool, callId, toolName, AgentToolReceiptStatus.Error);
        receipt.ResultJson = resultJson ?? string.Empty;
        receipt.ErrorCode = errorCode ?? string.Empty;
        receipt.ErrorMessage = errorMessage ?? string.Empty;
        return receipt;
    }

    public static AgentToolReceipt CreateApprovalRequired(
        IAgentTool tool,
        string callId,
        string toolName,
        string resultJson,
        string approvalRequestId)
    {
        var receipt = CreateBase(tool, callId, toolName, AgentToolReceiptStatus.ApprovalRequired);
        receipt.ResultJson = resultJson ?? string.Empty;
        receipt.ApprovalRequestId = approvalRequestId ?? string.Empty;
        return receipt;
    }

    public static AgentToolReceipt CreateDenied(
        IAgentTool tool,
        string callId,
        string toolName,
        string resultJson,
        string approvalRequestId,
        string reason)
    {
        var receipt = CreateBase(tool, callId, toolName, AgentToolReceiptStatus.Denied);
        receipt.ResultJson = resultJson ?? string.Empty;
        receipt.ApprovalRequestId = approvalRequestId ?? string.Empty;
        receipt.ErrorCode = "approval_denied";
        receipt.ErrorMessage = reason ?? string.Empty;
        return receipt;
    }

    public static AgentToolReceipt CreateApprovalError(
        IAgentTool tool,
        string callId,
        string toolName,
        string resultJson,
        string approvalRequestId,
        string errorCode,
        string errorMessage)
    {
        var receipt = CreateBase(tool, callId, toolName, AgentToolReceiptStatus.Error);
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
        AgentToolReceiptStatus status) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? tool.Name ?? string.Empty : toolName,
            Status = status,
            ApprovalMode = MapApprovalMode(tool.ApprovalMode),
            IsDestructive = tool.IsDestructive,
            SideEffectKind = NormalizeSideEffectKind(tool.SideEffectKind),
        };

    private static string NormalizeSideEffectKind(string? sideEffectKind) =>
        string.IsNullOrWhiteSpace(sideEffectKind) ? string.Empty : sideEffectKind.Trim().ToLowerInvariant();

    private static AgentToolReceipt NormalizeProviderSuccessReceipt(
        IAgentTool tool,
        string callId,
        string toolName,
        string? resultJson,
        AgentToolReceipt receipt)
    {
        var normalized = receipt.Clone();
        normalized.CallId = string.IsNullOrWhiteSpace(normalized.CallId) ? callId ?? string.Empty : normalized.CallId;
        normalized.ToolName = string.IsNullOrWhiteSpace(normalized.ToolName)
            ? string.IsNullOrWhiteSpace(toolName) ? tool.Name ?? string.Empty : toolName
            : normalized.ToolName;
        normalized.Status = AgentToolReceiptStatus.Success;
        if (normalized.ApprovalMode == AgentToolReceiptApprovalMode.Unspecified)
            normalized.ApprovalMode = MapApprovalMode(tool.ApprovalMode);
        normalized.IsDestructive = normalized.IsDestructive || tool.IsDestructive;
        normalized.SideEffectKind = string.IsNullOrWhiteSpace(normalized.SideEffectKind)
            ? NormalizeSideEffectKind(tool.SideEffectKind)
            : NormalizeSideEffectKind(normalized.SideEffectKind);
        if (string.IsNullOrWhiteSpace(normalized.ResultJson))
            normalized.ResultJson = resultJson ?? string.Empty;
        return normalized;
    }
}
