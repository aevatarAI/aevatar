using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdRequestKeyRotateTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const string ArgumentsInvalidCode = "NYXID_KEY_ROTATE_ARGUMENTS_INVALID";
    private const string ContextUnavailableCode = "NYXID_KEY_ROTATE_CONTEXT_UNAVAILABLE";
    private const string KeyUnavailableCode = "NYXID_KEY_ROTATE_KEY_UNAVAILABLE";
    private const string ResultInvalidCode = "NYXID_KEY_ROTATE_RESULT_INVALID";
    private const string RequirementCode = "NYXID_KEY_ROTATION_REQUIRED";
    private const string RequirementMessage =
        "Rotate the exact NyxID API key in the secure browser action.";

    private readonly NyxIdApiClient _client;

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    public NyxIdRequestKeyRotateTool(NyxIdApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => "nyxid_request_key_rotate";

    public string Description =>
        "Verify one exact current-caller NyxID API key identity, then emit the typed key.rotate " +
        "browser handoff. This tool never rotates a key and never accepts key material.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "key_id": { "type": "string" }
          },
          "required": ["key_id"],
          "additionalProperties": false
        }
        """;

    public bool IsReadOnly => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryParseArguments(argumentsJson, out var keyId))
            return ErrorResult(ArgumentsInvalidCode, "key_id must be one exact safe identity");

        var token = ResolveOwnerReadToken();
        if (token is null || !HasVerifiedOwnerAuthority())
        {
            return ErrorResult(
                ContextUnavailableCode,
                "verified owner identity and source-readable NyxID authority are required");
        }

        var response = await _client.GetApiKeyAsync(token, keyId, ct).ConfigureAwait(false);
        var evidence = NyxIdApiAccessResponseParser.ParseAgentApiKey(response);
        if (!evidence.Succeeded ||
            evidence.Value is null ||
            !evidence.Value.IsActive ||
            !string.Equals(evidence.Value.Id, keyId, StringComparison.Ordinal))
        {
            return ErrorResult(
                KeyUnavailableCode,
                "The exact caller-visible NyxID API key is unavailable.");
        }

        return JsonSerializer.Serialize(new
        {
            blocked = true,
            action = "key.rotate",
            key_id = keyId,
            reason_code = RequirementCode,
            safe_message = RequirementMessage,
        });
    }

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        if (!TryParseArguments(argumentsJson, out var keyId))
        {
            return ErrorReceipt(
                callId,
                toolName,
                ArgumentsInvalidCode,
                "key_id must be one exact safe identity");
        }

        if (TryReadError(resultJson, out var errorCode, out var errorMessage))
            return ErrorReceipt(callId, toolName, errorCode, errorMessage);

        if (!ResultMatches(resultJson, keyId))
        {
            return ErrorReceipt(
                callId,
                toolName,
                ResultInvalidCode,
                "NyxID key rotation readiness returned an invalid result.");
        }

        var blocker = new NyxIdAuthorizationRequiredEvent
        {
            ReasonCode = RequirementCode,
            SafeMessage = RequirementMessage,
            KeyRotate = new NyxIdKeyRotateActionRequirement { KeyId = keyId },
        };
        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
            Status = AgentToolReceiptStatus.AuthorizationRequired,
            ResultJson = resultJson,
            ErrorCode = blocker.ReasonCode,
            ErrorMessage = blocker.SafeMessage,
            AuthorizationRequired = blocker,
        };
    }

    private static bool TryParseArguments(string? argumentsJson, out string keyId)
    {
        keyId = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson ?? string.Empty);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.EnumerateObject().All(static property => property.Name == "key_id") &&
                   root.TryGetProperty("key_id", out var element) &&
                   element.ValueKind == JsonValueKind.String &&
                   TryNormalizeIdentity(element.GetString(), out keyId) &&
                   string.Equals(element.GetString(), keyId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryNormalizeIdentity(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 256 &&
               !normalized.Any(char.IsControl) &&
               !normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
               !normalized.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Any(char.IsWhiteSpace) &&
               !normalized.Any(static character => character is '/' or '\\' or '?' or '#');
    }

    private static string? ResolveOwnerReadToken()
    {
        var credentials = AgentToolRequestContext.Current?.Credentials;
        var sourceReadable = AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(credentials);
        if (sourceReadable is not null)
            return sourceReadable;
        return credentials?.NyxIdCredentialKind == AgentToolNyxIdCredentialKind.ProxyDelegation
            ? NormalizeBearerToken(credentials.NyxIdAccessToken)
            : null;
    }

    private static bool HasVerifiedOwnerAuthority() =>
        !string.IsNullOrWhiteSpace(AgentToolRequestContext.OwnerScopeId) &&
        AgentToolRequestContext.NyxIdAuthority.IsComplete &&
        !string.IsNullOrWhiteSpace(AgentToolRequestContext.NyxIdAuthority.ExternalUserId);

    private static string? NormalizeBearerToken(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) ||
            normalized.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            normalized.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return normalized;
    }

    private static bool ResultMatches(string resultJson, string keyId)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("blocked", out var blocked) &&
                   blocked.ValueKind == JsonValueKind.True &&
                   root.TryGetProperty("action", out var action) &&
                   action.GetString() == "key.rotate" &&
                   root.TryGetProperty("key_id", out var resultKeyId) &&
                   resultKeyId.GetString() == keyId &&
                   root.TryGetProperty("reason_code", out var reason) &&
                   reason.GetString() == RequirementCode &&
                   root.TryGetProperty("safe_message", out var message) &&
                   message.GetString() == RequirementMessage;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadError(
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

            errorCode = code.GetString() ?? ResultInvalidCode;
            errorMessage = message.GetString() ?? "NyxID key rotation readiness failed.";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ErrorResult(string code, string safeMessage) =>
        JsonSerializer.Serialize(new
        {
            error = true,
            error_code = code,
            safe_message = safeMessage,
        });

    private static AgentToolReceipt ErrorReceipt(
        string callId,
        string toolName,
        string code,
        string safeMessage) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? "nyxid_request_key_rotate" : toolName,
            Status = AgentToolReceiptStatus.Error,
            ErrorCode = code,
            ErrorMessage = safeMessage,
            ResultJson = ErrorResult(code, safeMessage),
        };
}
