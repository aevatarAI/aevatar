using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using static Aevatar.AI.ToolProviders.NyxId.Tools.NyxIdBrowserActionRequestToolHelpers;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdRequestKeyCreateTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const int MaxAllowedServiceIds = 64;
    private const string ArgumentsInvalidCode = "NYXID_KEY_CREATE_ARGUMENTS_INVALID";
    private const string ContextUnavailableCode = "NYXID_KEY_CREATE_CONTEXT_UNAVAILABLE";
    private const string InventoryUnavailableCode = "NYXID_KEY_CREATE_INVENTORY_UNAVAILABLE";
    private const string ServiceIdentityInvalidCode = "NYXID_KEY_CREATE_SERVICE_IDENTITY_INVALID";
    private const string ResultInvalidCode = "NYXID_KEY_CREATE_RESULT_INVALID";
    private const string RequirementCode = "NYXID_KEY_CREATION_REQUIRED";
    private const string RequirementMessage =
        "Create the least-scope NyxID API key in the secure browser action.";

    private readonly NyxIdApiClient _client;

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    public NyxIdRequestKeyCreateTool(NyxIdApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => "nyxid_request_key_create";

    public string Description =>
        "Validate an exact nonempty set of current-caller NyxID UserService identities, then emit " +
        "the typed key.create browser handoff. This tool never creates a key and never accepts key material.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "platform": { "type": "string" },
            "allowed_service_ids": {
              "type": "array",
              "minItems": 1,
              "maxItems": 64,
              "uniqueItems": true,
              "items": { "type": "string" }
            }
          },
          "required": ["name", "platform", "allowed_service_ids"],
          "additionalProperties": false
        }
        """;

    public bool IsReadOnly => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryParseArguments(argumentsJson, out var request))
            return ErrorResult(ArgumentsInvalidCode, "name, platform, and allowed_service_ids must be valid");

        var token = ResolveOwnerReadToken();
        if (token is null || !HasVerifiedOwnerAuthority())
        {
            return ErrorResult(
                ContextUnavailableCode,
                "verified owner identity and source-readable NyxID authority are required");
        }

        var response = await _client.ListServicesAsync(token, ct).ConfigureAwait(false);
        var inventory = NyxIdApiAccessResponseParser.ParseUserServiceKeys(response);
        if (!inventory.Succeeded)
        {
            return ErrorResult(
                InventoryUnavailableCode,
                "The caller-visible NyxID service inventory is currently unavailable.");
        }

        var visibleIds = inventory.Value!.Services
            .Select(static service => service.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (request.AllowedServiceIds.Any(id => !visibleIds.Contains(id)))
        {
            return ErrorResult(
                ServiceIdentityInvalidCode,
                "allowed_service_ids contains an identity outside the current caller inventory.");
        }

        return JsonSerializer.Serialize(new
        {
            blocked = true,
            action = "key.create",
            name = request.Name,
            platform = request.Platform,
            allowed_service_ids = request.AllowedServiceIds,
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
        if (!TryParseArguments(argumentsJson, out var request))
        {
            return ErrorReceipt(
                callId,
                toolName,
                Name,
                ArgumentsInvalidCode,
                "name, platform, and allowed_service_ids must be valid");
        }

        if (TryReadError(resultJson, out var errorCode, out var errorMessage))
            return ErrorReceipt(callId, toolName, Name, errorCode, errorMessage);

        if (!ResultMatches(resultJson, request))
        {
            return ErrorReceipt(
                callId,
                toolName,
                Name,
                ResultInvalidCode,
                "NyxID key creation readiness returned an invalid result.");
        }

        var keyCreate = new NyxIdKeyCreateActionRequirement
        {
            Name = request.Name,
            Platform = request.Platform,
        };
        keyCreate.AllowedServiceIds.Add(request.AllowedServiceIds);
        var blocker = new NyxIdAuthorizationRequiredEvent
        {
            ReasonCode = RequirementCode,
            SafeMessage = RequirementMessage,
            KeyCreate = keyCreate,
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

    private static bool TryParseArguments(string? argumentsJson, out KeyCreateRequest request)
    {
        request = null!;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Any(property => property.Name is not
                    ("name" or "platform" or "allowed_service_ids")) ||
                !TryReadSafeString(root, "name", 256, out var name) ||
                !TryReadSafeString(root, "platform", 128, out var platform) ||
                !root.TryGetProperty("allowed_service_ids", out var allowedServiceIds) ||
                allowedServiceIds.ValueKind != JsonValueKind.Array ||
                allowedServiceIds.GetArrayLength() is < 1 or > MaxAllowedServiceIds)
            {
                return false;
            }

            var ids = new List<string>(allowedServiceIds.GetArrayLength());
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in allowedServiceIds.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String ||
                    !TryNormalizeSafeValue(item.GetString(), 256, out var id) ||
                    !string.Equals(item.GetString(), id, StringComparison.Ordinal) ||
                    !distinct.Add(id))
                {
                    return false;
                }

                ids.Add(id);
            }

            request = new KeyCreateRequest(name, platform, ids);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadSafeString(
        JsonElement root,
        string propertyName,
        int maxLength,
        out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               TryNormalizeSafeValue(element.GetString(), maxLength, out value);
    }

    private static bool TryNormalizeSafeValue(string? value, int maxLength, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maxLength || normalized.Any(char.IsControl) ||
            HasCredentialPrefix(normalized))
        {
            return false;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            (!string.IsNullOrEmpty(uri.UserInfo) ||
             !string.IsNullOrEmpty(uri.Query) ||
             !string.IsNullOrEmpty(uri.Fragment)))
        {
            return false;
        }

        return true;
    }

    private static bool ResultMatches(string resultJson, KeyCreateRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("blocked", out var blocked) || blocked.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("action", out var action) || action.GetString() != "key.create" ||
                !root.TryGetProperty("name", out var name) || name.GetString() != request.Name ||
                !root.TryGetProperty("platform", out var platform) || platform.GetString() != request.Platform ||
                !root.TryGetProperty("reason_code", out var reason) || reason.GetString() != RequirementCode ||
                !root.TryGetProperty("safe_message", out var message) || message.GetString() != RequirementMessage ||
                !root.TryGetProperty("allowed_service_ids", out var ids) ||
                ids.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return ids.EnumerateArray().Select(static item => item.GetString()).SequenceEqual(
                request.AllowedServiceIds,
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record KeyCreateRequest(
        string Name,
        string Platform,
        IReadOnlyList<string> AllowedServiceIds);
}
