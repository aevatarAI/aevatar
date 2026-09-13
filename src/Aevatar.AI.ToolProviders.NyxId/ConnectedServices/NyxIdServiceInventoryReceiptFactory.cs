using System.Text.Json;
using Aevatar.AI.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

public static class NyxIdServiceInventoryReceiptFactory
{
    private const string FailureCode = "NYXID_SERVICE_INVENTORY_FAILED";
    private const string FailureMessage = "The connected-service inventory request failed.";
    private const string CredentialDeniedCode = "NYXID_SERVICE_INVENTORY_CREDENTIAL_DENIED";
    private const string CredentialDeniedMessage =
        "The connected-service inventory read was denied by credential policy. The credential configuration must be corrected before retrying.";
    private const string ContractInvalidMessage =
        "The connected-service inventory response did not match the execution inventory contract. Service availability could not be determined.";

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
                var credentialDenied = error.ValueKind == JsonValueKind.String &&
                    error.GetString() is "credential_denied" or CredentialDeniedCode;
                var contractInvalid = error.ValueKind == JsonValueKind.String &&
                    error.GetString() is "inventory_contract_invalid" or NyxIdServiceInventoryContractException.ErrorCode;
                var failureCode = credentialDenied ? CredentialDeniedCode :
                    contractInvalid ? NyxIdServiceInventoryContractException.ErrorCode : FailureCode;
                var failureMessage = credentialDenied ? CredentialDeniedMessage :
                    contractInvalid ? ContractInvalidMessage : FailureMessage;
                return new AgentToolReceipt
                {
                    CallId = callId ?? string.Empty,
                    ToolName = toolName ?? string.Empty,
                    Status = credentialDenied ? AgentToolReceiptStatus.Denied : AgentToolReceiptStatus.Error,
                    ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
                    ErrorCode = failureCode,
                    ErrorMessage = failureMessage,
                    ResultJson = JsonSerializer.Serialize(new
                    {
                        error = failureCode,
                        message = failureMessage,
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
