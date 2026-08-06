using System.Text.Json;
using Aevatar.AI.Abstractions;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

internal static class BindingToolResultReceipts
{
    private const string ReadyStatus = "EXTERNAL_CAPABILITY_READINESS_STATUS_READY";

    public static AgentToolReceipt? CreateCapabilityList(
        string defaultToolName,
        string callId,
        string toolName,
        string resultJson) =>
        CreateReadOnly(
            defaultToolName,
            callId,
            toolName,
            resultJson,
            "external_capability_list_failed",
            static root =>
                HasNonEmptyString(root, "scope_id") &&
                HasProperty(root, "capabilities", JsonValueKind.Array));

    public static AgentToolReceipt? CreateReadiness(
        string defaultToolName,
        string callId,
        string toolName,
        string resultJson) =>
        CreateReadOnly(
            defaultToolName,
            callId,
            toolName,
            resultJson,
            "external_capability_readiness_failed",
            static root =>
                TryGetString(root, "status", out var status) &&
                string.Equals(status, ReadyStatus, StringComparison.Ordinal),
            readReadinessStatus: true);

    public static AgentToolReceipt? CreateExplicitRequestPreview(
        string defaultToolName,
        string callId,
        string toolName,
        string resultJson) =>
        CreateReadOnly(
            defaultToolName,
            callId,
            toolName,
            resultJson,
            "workflow_explicit_request_preview_failed",
            static root =>
                HasNonEmptyString(root, "workflow_id") &&
                HasNonEmptyString(root, "revision_id") &&
                HasNonEmptyString(root, "execution_mode") &&
                HasProperty(root, "confirmations", JsonValueKind.Array) &&
                HasProperty(root, "requests", JsonValueKind.Array));

    public static AgentToolReceipt? CreateScopeWorkflowList(
        string defaultToolName,
        string callId,
        string toolName,
        string resultJson) =>
        CreateReadOnly(
            defaultToolName,
            callId,
            toolName,
            resultJson,
            "scope_workflow_list_failed",
            static root =>
                HasNonEmptyString(root, "scope_id") &&
                HasProperty(root, "workflows", JsonValueKind.Array));

    public static AgentToolReceipt? CreateScopeWorkflowGet(
        string defaultToolName,
        string callId,
        string toolName,
        string resultJson) =>
        CreateReadOnly(
            defaultToolName,
            callId,
            toolName,
            resultJson,
            "scope_workflow_get_failed",
            static root =>
            {
                if (!HasProperty(root, "available", JsonValueKind.True) &&
                    !HasProperty(root, "available", JsonValueKind.False))
                {
                    return false;
                }

                if (!HasNonEmptyString(root, "scope_id"))
                    return false;

                return root.GetProperty("available").GetBoolean()
                    ? HasProperty(root, "workflow", JsonValueKind.Object)
                    : HasNonEmptyString(root, "workflow_id") && HasNonEmptyString(root, "status");
            });

    private static AgentToolReceipt? CreateReadOnly(
        string defaultToolName,
        string callId,
        string toolName,
        string resultJson,
        string defaultErrorCode,
        Func<JsonElement, bool> isVerifiedSuccess,
        bool readReadinessStatus = false)
    {
        if (!TryParseObject(resultJson, out var document))
            return null;

        using (document)
        {
            var root = document.RootElement;
            if (TryReadError(root, defaultErrorCode, out var errorCode, out var errorMessage) ||
                (readReadinessStatus && TryReadReadinessError(root, out errorCode, out errorMessage)))
            {
                return Receipt(
                    defaultToolName,
                    callId,
                    toolName,
                    AgentToolReceiptStatus.Error,
                    resultJson,
                    errorCode,
                    errorMessage);
            }

            return isVerifiedSuccess(root)
                ? Receipt(
                    defaultToolName,
                    callId,
                    toolName,
                    AgentToolReceiptStatus.Success,
                    resultJson)
                : null;
        }
    }

    private static bool TryReadError(
        JsonElement root,
        string defaultErrorCode,
        out string errorCode,
        out string errorMessage)
    {
        errorCode = string.Empty;
        errorMessage = string.Empty;
        if (!root.TryGetProperty("error", out var error))
            return false;

        if (error.ValueKind == JsonValueKind.String)
        {
            errorCode = defaultErrorCode;
            errorMessage = error.GetString() ?? string.Empty;
            return true;
        }

        if (error.ValueKind != JsonValueKind.Object)
            return false;

        TryGetString(error, "code", out errorCode);
        TryGetString(error, "message", out errorMessage);
        if (string.IsNullOrWhiteSpace(errorCode))
            errorCode = defaultErrorCode;
        if (string.IsNullOrWhiteSpace(errorMessage))
            errorMessage = errorCode;
        return true;
    }

    private static bool TryReadReadinessError(
        JsonElement root,
        out string errorCode,
        out string errorMessage)
    {
        errorCode = string.Empty;
        errorMessage = string.Empty;
        if (!TryGetString(root, "status", out var status) ||
            string.Equals(status, ReadyStatus, StringComparison.Ordinal))
        {
            return false;
        }

        errorCode = status;
        if (root.TryGetProperty("blockers", out var blockers) &&
            blockers.ValueKind == JsonValueKind.Array)
        {
            foreach (var blocker in blockers.EnumerateArray())
            {
                if (blocker.ValueKind != JsonValueKind.Object)
                    continue;
                if (TryGetString(blocker, "safe_message", out errorMessage) &&
                    !string.IsNullOrWhiteSpace(errorMessage))
                {
                    return true;
                }
            }
        }

        errorMessage = status;
        return true;
    }

    private static AgentToolReceipt Receipt(
        string defaultToolName,
        string callId,
        string toolName,
        AgentToolReceiptStatus status,
        string resultJson,
        string errorCode = "",
        string errorMessage = "") =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? defaultToolName : toolName,
            Status = status,
            ApprovalMode = AgentToolReceiptApprovalMode.NeverRequire,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
            ResultJson = resultJson ?? string.Empty,
        };

    private static bool TryParseObject(string? resultJson, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(resultJson))
            return false;

        try
        {
            document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return true;

            document.Dispose();
            document = null!;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasNonEmptyString(JsonElement root, string propertyName) =>
        TryGetString(root, propertyName, out var value) && !string.IsNullOrWhiteSpace(value);

    private static bool HasProperty(JsonElement root, string propertyName, JsonValueKind valueKind) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == valueKind;

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }
}
