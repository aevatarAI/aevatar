using System.Text.Json;
using Aevatar.AI.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public static class NyxIdServiceInventoryReceiptFactory
{
    private const string FailureCode = "NYXID_SERVICE_INVENTORY_FAILED";
    private const string FailureMessage = "The connected-service inventory request failed.";

    public static AgentToolReceipt? Create(
        string callId,
        string toolName,
        string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
            {
                return new AgentToolReceipt
                {
                    CallId = callId ?? string.Empty,
                    ToolName = toolName ?? string.Empty,
                    Status = AgentToolReceiptStatus.Error,
                    ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                    ErrorCode = FailureCode,
                    ErrorMessage = FailureMessage,
                    ResultJson = JsonSerializer.Serialize(new
                    {
                        error = FailureCode,
                        message = FailureMessage,
                    }),
                };
            }

            if (!root.TryGetProperty("instances", out var instances) ||
                instances.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = toolName ?? string.Empty,
                Status = AgentToolReceiptStatus.Success,
                ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
