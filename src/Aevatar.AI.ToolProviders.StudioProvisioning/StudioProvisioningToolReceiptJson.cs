using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal static class StudioProvisioningToolReceiptJson
{
    public static AgentToolReceipt? CreateErrorReceiptFromNestedError(
        IAgentTool tool,
        string callId,
        string toolName,
        string resultJson)
    {
        if (!TryReadNestedError(resultJson, out var code, out var message))
            return null;

        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? tool.Name : toolName.Trim(),
            Status = AgentToolReceiptStatus.Error,
            ApprovalMode = MapApprovalMode(tool.ApprovalMode),
            IsDestructive = tool.IsDestructive,
            SideEffectKind = Normalize(tool.SideEffectKind),
            ErrorCode = code,
            ErrorMessage = message,
            ResultJson = resultJson ?? string.Empty,
        };
    }

    private static bool TryReadNestedError(
        string? resultJson,
        out string code,
        out string message)
    {
        code = string.Empty;
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(resultJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("code", out var codeElement))
            {
                return false;
            }

            code = Normalize(codeElement.GetString());
            if (code.Length == 0)
                return false;

            message = error.TryGetProperty("message", out var messageElement)
                ? Normalize(messageElement.GetString())
                : string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AgentToolReceiptApprovalMode MapApprovalMode(ToolApprovalMode mode) => mode switch
    {
        ToolApprovalMode.NeverRequire => AgentToolReceiptApprovalMode.NeverRequire,
        ToolApprovalMode.AlwaysRequire => AgentToolReceiptApprovalMode.AlwaysRequire,
        ToolApprovalMode.Auto => AgentToolReceiptApprovalMode.Auto,
        _ => AgentToolReceiptApprovalMode.Unspecified,
    };

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
