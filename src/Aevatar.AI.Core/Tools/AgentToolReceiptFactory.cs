using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

// refactor helper, no behavior change: centralizes typed receipt construction for tool side-effect authority.
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

        var receipt = CreateBase(tool, callId, toolName, AgentToolReceiptStatus.Success);
        receipt.ResultJson = resultJson ?? string.Empty;
        ApplySubject(receipt, resultJson);
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

    private static void ApplySubject(AgentToolReceipt receipt, string? resultJson)
    {
        if (!string.Equals(receipt.SideEffectKind, "ornn.publish.skill", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(resultJson))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return;

            receipt.SubjectKind = "ornn.skill";
            receipt.SubjectId = TryGetString(doc.RootElement, "guid", "id", "skill_id", "skillId") ?? string.Empty;
            receipt.SubjectVersion = TryGetString(doc.RootElement, "version", "subject_version", "subjectVersion") ?? string.Empty;
            receipt.SubjectHash = TryGetString(doc.RootElement, "skillHash", "skill_hash", "hash", "subject_hash") ?? string.Empty;
        }
        catch (JsonException)
        {
            return;
        }
    }

    private static string? TryGetString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return value.ToString();
        }

        return null;
    }
}
