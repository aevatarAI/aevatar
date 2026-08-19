using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using static Aevatar.AI.ToolProviders.NyxId.Tools.NyxIdBrowserActionRequestToolHelpers;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdRequestServiceReauthorizeTool : INyxIdBuiltInTool, IAgentToolCapabilityDescriptor
{
    private const string ArgumentsInvalidCode = "NYXID_SERVICE_REAUTHORIZE_ARGUMENTS_INVALID";
    private const string ContextUnavailableCode = "NYXID_SERVICE_REAUTHORIZE_CONTEXT_UNAVAILABLE";
    private const string ServiceUnavailableCode = "NYXID_SERVICE_REAUTHORIZE_SERVICE_UNAVAILABLE";
    private const string ResultInvalidCode = "NYXID_SERVICE_REAUTHORIZE_RESULT_INVALID";
    private const string RequirementCode = "NYXID_SERVICE_REAUTHORIZATION_REQUIRED";
    private const string RequirementMessage =
        "Re-authorize the exact connected NyxID service in the secure browser action.";
    private const int MaxRequestedScopes = 64;

    private readonly NyxIdApiClient _client;

    public IReadOnlyCollection<string> Capabilities => NyxIdToolSurfaces.HumanSessionOnly;

    public NyxIdRequestServiceReauthorizeTool(NyxIdApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => "nyxid_request_service_reauthorize";

    public string Description =>
        "Verify one exact current-caller connected NyxID user service identity, then emit the typed " +
        "service.reauthorize browser handoff for the requested scopes. This tool never re-authorizes " +
        "a service and never accepts tokens, keys, or authorization codes.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "user_service_id": { "type": "string" },
            "requested_scopes": {
              "type": "array",
              "minItems": 1,
              "maxItems": 64,
              "uniqueItems": true,
              "items": { "type": "string" }
            }
          },
          "required": ["user_service_id", "requested_scopes"],
          "additionalProperties": false
        }
        """;

    public bool IsReadOnly => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryParseArguments(argumentsJson, out var arguments))
        {
            return ErrorResult(
                ArgumentsInvalidCode,
                "user_service_id must be one exact safe identity and requested_scopes must be a nonempty unique scope set");
        }

        var token = ResolveOwnerReadToken();
        if (token is null || !HasVerifiedOwnerAuthority())
        {
            return ErrorResult(
                ContextUnavailableCode,
                "verified owner identity and source-readable NyxID authority are required");
        }

        var response = await _client.GetServiceAsync(token, arguments.UserServiceId, ct)
            .ConfigureAwait(false);
        var evidence = NyxIdApiAccessResponseParser.ParseUserServiceAuthorization(response);
        if (!evidence.Succeeded ||
            evidence.Value is null ||
            !evidence.Value.IsActive ||
            !string.Equals(evidence.Value.UserServiceId, arguments.UserServiceId, StringComparison.Ordinal))
        {
            return ErrorResult(
                ServiceUnavailableCode,
                "The exact caller-visible connected NyxID service is unavailable.");
        }

        return JsonSerializer.Serialize(new
        {
            blocked = true,
            action = "service.reauthorize",
            user_service_id = arguments.UserServiceId,
            requested_scopes = arguments.RequestedScopes,
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
        if (!TryParseArguments(argumentsJson, out var arguments))
        {
            return ErrorReceipt(
                callId,
                toolName,
                Name,
                ArgumentsInvalidCode,
                "user_service_id must be one exact safe identity and requested_scopes must be a nonempty unique scope set");
        }

        if (TryReadError(resultJson, out var errorCode, out var errorMessage))
            return ErrorReceipt(callId, toolName, Name, errorCode, errorMessage);

        if (!ResultMatches(resultJson, arguments))
        {
            return ErrorReceipt(
                callId,
                toolName,
                Name,
                ResultInvalidCode,
                "NyxID service reauthorization readiness returned an invalid result.");
        }

        var requirement = new NyxIdServiceReauthorizeActionRequirement
        {
            UserServiceId = arguments.UserServiceId,
        };
        requirement.RequestedScopes.AddRange(arguments.RequestedScopes);
        var blocker = new NyxIdAuthorizationRequiredEvent
        {
            ReasonCode = RequirementCode,
            SafeMessage = RequirementMessage,
            ServiceReauthorize = requirement,
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

    private sealed record ReauthorizeArguments(
        string UserServiceId,
        IReadOnlyList<string> RequestedScopes);

    private static bool TryParseArguments(string? argumentsJson, out ReauthorizeArguments arguments)
    {
        arguments = new ReauthorizeArguments(string.Empty, Array.Empty<string>());
        try
        {
            using var document = JsonDocument.Parse(argumentsJson ?? string.Empty);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.EnumerateObject().All(static property =>
                    property.Name is "user_service_id" or "requested_scopes") ||
                !root.TryGetProperty("user_service_id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                !TryNormalizeSafeIdentity(idElement.GetString(), out var userServiceId) ||
                !string.Equals(idElement.GetString(), userServiceId, StringComparison.Ordinal) ||
                !root.TryGetProperty("requested_scopes", out var scopesElement) ||
                scopesElement.ValueKind != JsonValueKind.Array ||
                !TryReadScopes(scopesElement, out var requestedScopes))
            {
                return false;
            }

            arguments = new ReauthorizeArguments(userServiceId, requestedScopes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadScopes(JsonElement scopesElement, out IReadOnlyList<string> scopes)
    {
        var values = new List<string>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in scopesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                scopes = Array.Empty<string>();
                return false;
            }

            var scope = item.GetString() ?? string.Empty;
            if (!TryNormalizeScope(scope, out var normalized) ||
                !string.Equals(scope, normalized, StringComparison.Ordinal) ||
                !distinct.Add(normalized))
            {
                scopes = Array.Empty<string>();
                return false;
            }

            values.Add(normalized);
        }

        scopes = values;
        return values.Count is >= 1 and <= MaxRequestedScopes;
    }

    private static bool TryNormalizeScope(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= 256 &&
               !normalized.Any(char.IsControl) &&
               !normalized.Any(char.IsWhiteSpace);
    }

    private static bool ResultMatches(string resultJson, ReauthorizeArguments arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("blocked", out var blocked) &&
                   blocked.ValueKind == JsonValueKind.True &&
                   root.TryGetProperty("action", out var action) &&
                   action.GetString() == "service.reauthorize" &&
                   root.TryGetProperty("user_service_id", out var resultUserServiceId) &&
                   resultUserServiceId.GetString() == arguments.UserServiceId &&
                   root.TryGetProperty("requested_scopes", out var resultScopes) &&
                   resultScopes.ValueKind == JsonValueKind.Array &&
                   resultScopes.EnumerateArray()
                       .Select(static scope => scope.GetString())
                       .SequenceEqual(arguments.RequestedScopes, StringComparer.Ordinal) &&
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
}
