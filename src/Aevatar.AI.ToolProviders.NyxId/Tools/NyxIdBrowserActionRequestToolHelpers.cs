using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Shared owner-authority, safe-identity, and error-envelope logic for the
/// <c>nyxid_request_*</c> browser-action handoff tools (key.create, key.rotate,
/// service.reauthorize). Per-tool code owns only its argument shape, the exact
/// NyxID read it verifies against, result matching, and the typed requirement.
/// </summary>
internal static class NyxIdBrowserActionRequestToolHelpers
{
    private const int MaxIdentityLength = 256;

    /// <summary>
    /// One exact opaque NyxID identity: trimmed, nonempty, no control or whitespace
    /// characters, no path/query/fragment separators, and never a credential prefix.
    /// </summary>
    public static bool TryNormalizeSafeIdentity(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= MaxIdentityLength &&
               !normalized.Any(char.IsControl) &&
               !HasCredentialPrefix(normalized) &&
               !normalized.Any(char.IsWhiteSpace) &&
               !normalized.Any(static character => character is '/' or '\\' or '?' or '#');
    }

    public static bool HasCredentialPrefix(string value) =>
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase);

    public static string? ResolveOwnerReadToken()
        => AgentToolHumanSessionNyxIdCredential.ResolveBearerToken(
            AgentToolRequestContext.Current);

    public static bool HasVerifiedOwnerAuthority() =>
        !string.IsNullOrWhiteSpace(AgentToolRequestContext.OwnerScopeId) &&
        AgentToolRequestContext.NyxIdAuthority.IsComplete &&
        !string.IsNullOrWhiteSpace(AgentToolRequestContext.NyxIdAuthority.ExternalUserId);

    public static bool TryReadError(
        string resultJson,
        out string errorCode,
        out string errorMessage)
    {
        errorCode = string.Empty;
        errorMessage = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("error_code", out var code) || code.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("safe_message", out var message) || message.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            errorCode = code.GetString() ?? string.Empty;
            errorMessage = message.GetString() ?? string.Empty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string ErrorResult(string code, string safeMessage) =>
        JsonSerializer.Serialize(new
        {
            error = true,
            error_code = code,
            safe_message = safeMessage,
        });

    public static AgentToolReceipt ErrorReceipt(
        string callId,
        string toolName,
        string defaultToolName,
        string code,
        string safeMessage) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? defaultToolName : toolName,
            Status = AgentToolReceiptStatus.Error,
            ErrorCode = code,
            ErrorMessage = safeMessage,
            ResultJson = ErrorResult(code, safeMessage),
        };
}
