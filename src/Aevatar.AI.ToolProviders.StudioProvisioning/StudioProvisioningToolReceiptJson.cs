using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal static class StudioProvisioningToolReceiptJson
{
    public static AgentToolReceipt? CreateReceiptFromResult(
        IAgentTool tool,
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        var sideEffectKind = Normalize(tool.SideEffectKind);
        if (sideEffectKind.Length == 0 && !tool.IsDestructive)
            return null;

        var receipt = new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? tool.Name : toolName.Trim(),
            Status = AgentToolReceiptStatus.Success,
            ApprovalMode = MapApprovalMode(tool.ApprovalMode),
            IsDestructive = tool.IsDestructive,
            SideEffectKind = sideEffectKind,
            ResultJson = resultJson ?? string.Empty,
        };
        ApplySubject(receipt, argumentsJson);

        if (!TryReadNestedError(resultJson, out var code, out var message))
            return receipt;

        receipt.Status = AgentToolReceiptStatus.Error;
        receipt.ErrorCode = code;
        receipt.ErrorMessage = message;
        return receipt;
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

    private static void ApplySubject(AgentToolReceipt receipt, string? argumentsJson)
    {
        var subjectHash = ComputeSubjectHash(argumentsJson);
        receipt.SubjectKind = receipt.SideEffectKind;
        receipt.SubjectId = subjectHash;
        receipt.SubjectHash = subjectHash;
    }

    private static string ComputeSubjectHash(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonicalJsonValue(writer, document.RootElement);
            }
            return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        }
        catch (JsonException)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson.Trim())));
        }
    }

    private static void WriteCanonicalJsonValue(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJsonValue(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJsonValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
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
