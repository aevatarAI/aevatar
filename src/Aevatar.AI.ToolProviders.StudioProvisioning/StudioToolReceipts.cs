using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

/// <summary>
/// Shared typed-receipt builder for the Studio provisioning tool family.
///
/// Every tool in this provider owns its own result contract: a structured failure
/// is <c>{"error":{"code","message"}}</c> and anything else is the tool's typed
/// success object. That makes each outcome classifiable by the provider, which is
/// what the audit-trail canon requires — a tool that returns without a receipt has
/// an unverified outcome and consumers must not upgrade it to success. Leaving the
/// receipt null here would let <c>ToolCallReceiptFinalizer</c> replace a real
/// mutation result with the synthetic <c>tool_outcome_unknown</c> payload and hide
/// the created resource identity from the next chat tool round.
/// </summary>
internal static class StudioToolReceipts
{
    /// <summary>
    /// Classifies <paramref name="resultJson"/> into a typed provider receipt.
    /// Returns null only when the payload cannot be classified at all, which
    /// honestly leaves the outcome unverified.
    /// </summary>
    /// <param name="subjectKind">Stable resource family this tool acts on.</param>
    /// <param name="subjectIdKeys">
    /// Candidate result properties holding the resource identity, in priority order.
    /// </param>
    public static AgentToolReceipt? Create(
        string callId,
        string toolName,
        string fallbackToolName,
        string sideEffectKind,
        string resultJson,
        string subjectKind,
        params string[] subjectIdKeys)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(resultJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var receipt = new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = string.IsNullOrWhiteSpace(toolName) ? fallbackToolName : toolName,
                ApprovalMode = AgentToolReceiptApprovalMode.Unspecified,
                SideEffectKind = sideEffectKind ?? string.Empty,
                ResultJson = resultJson,
            };

            if (TryReadError(root, out var code, out var message))
            {
                receipt.Status = AgentToolReceiptStatus.Error;
                receipt.ErrorCode = code;
                receipt.ErrorMessage = message;
                // A failed mutation must still name the resource it was aiming at so
                // audit can line the failure up with the successful attempt.
                receipt.SubjectKind = subjectKind ?? string.Empty;
                receipt.SubjectId = ReadSubjectId(root, subjectIdKeys);
                return receipt;
            }

            receipt.Status = AgentToolReceiptStatus.Success;
            receipt.SubjectKind = subjectKind ?? string.Empty;
            receipt.SubjectId = ReadSubjectId(root, subjectIdKeys);
            return receipt;
        }
    }

    private static bool TryReadError(JsonElement root, out string code, out string message)
    {
        code = string.Empty;
        message = string.Empty;

        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return false;

        if (!error.TryGetProperty("code", out var codeElement) ||
            codeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        code = codeElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code))
            return false;

        message = error.TryGetProperty("message", out var messageElement) &&
                  messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString() ?? string.Empty
            : string.Empty;
        return true;
    }

    private static string ReadSubjectId(JsonElement root, string[] subjectIdKeys)
    {
        foreach (var key in subjectIdKeys ?? [])
        {
            if (TryReadNonEmptyString(root, key, out var value))
                return value;

            // Structured failures repeat the target id inside the error body.
            if (root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                TryReadNonEmptyString(error, key, out var errorValue))
            {
                return errorValue;
            }
        }

        return string.Empty;
    }

    private static bool TryReadNonEmptyString(JsonElement element, string key, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        var raw = property.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        value = raw;
        return true;
    }
}
