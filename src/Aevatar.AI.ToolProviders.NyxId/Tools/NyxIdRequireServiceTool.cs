using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdRequireServiceTool : INyxIdBuiltInTool
{
    private const int MaxRequestedScopes = 64;
    private const string ArgumentsInvalidCode = "NYXID_REQUIRE_SERVICE_ARGUMENTS_INVALID";
    private const string CatalogIdentityInvalidCode = "NYXID_REQUIRE_SERVICE_CATALOG_IDENTITY_INVALID";
    private const string CatalogUnavailableCode = "NYXID_REQUIRE_SERVICE_CATALOG_UNAVAILABLE";
    private const string ContextUnavailableCode = "NYXID_REQUIRE_SERVICE_CONTEXT_UNAVAILABLE";
    private const string ResultInvalidCode = "NYXID_REQUIRE_SERVICE_RESULT_INVALID";
    private const string ScopesRequiredCode = "NYXID_REQUIRE_SERVICE_SCOPES_REQUIRED";
    private const string CatalogIdentityInvalidMessage =
        "The requested NyxID catalog service identity could not be verified.";
    private const string CatalogUnavailableMessage =
        "The NyxID catalog is currently unavailable.";
    private const string ResultInvalidMessage = "NyxID service readiness returned an invalid result.";
    private const string ScopesRequiredMessage =
        "requested_scopes must select the intended capability from the NyxID catalog.";

    private readonly NyxIdApiClient _client;

    public NyxIdRequireServiceTool(NyxIdApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => "nyxid_require_service";

    public string Description =>
        "Final typed readiness gate for a connect, add, or authorize request after the exact NyxID " +
        "catalog slug and requested scopes have been copied from a current-turn catalog result. " +
        "Provider slugs, display names, and remembered values are not catalog service identities. " +
        "Verify live whether the service is absent, then emit the typed " +
        "authorization blocker used for the interactive service.connect handoff only when " +
        "registration is required.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "service_slug": {
              "type": "string",
              "description": "Exact catalog service slug copied from nyxid_catalog in this turn; never a provider slug, display name, or guessed value."
            },
            "service_label": { "type": "string" },
            "resource_uri": { "type": "string" },
            "requested_scopes": {
              "type": "array",
              "description": "Scopes selected from the current catalog entry for the intended capability. Do not omit scopes when the entry exposes a scope catalog.",
              "items": { "type": "string" },
              "maxItems": 64
            }
          },
          "required": ["service_slug", "requested_scopes"],
          "additionalProperties": false
        }
        """;

    public bool IsReadOnly => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var args = ToolArgs.Parse(argumentsJson);
        var serviceSlug = NormalizeSlug(args.Str("service_slug"));
        if (args.HasParseError ||
            serviceSlug is null ||
            !TryReadRequestedScopes(args, out var requestedScopes))
        {
            return ErrorResult(ArgumentsInvalidCode, "service_slug and requested_scopes must be valid");
        }

        if (!TryResolveAccess(out var access, out var error))
            return ErrorResult(ContextUnavailableCode, error!);

        var catalogVerification = await VerifyCatalogServiceAsync(access!, serviceSlug, ct);
        if (catalogVerification.Status == CatalogVerificationStatus.SourceUnavailable)
        {
            var sourceUnavailable = await InspectRegistrationAsync(access!, serviceSlug, ct);
            return SerializeReadiness(serviceSlug, sourceUnavailable);
        }
        if (catalogVerification.Status == CatalogVerificationStatus.Unavailable)
            return ErrorResult(CatalogUnavailableCode, CatalogUnavailableMessage);
        if (catalogVerification.Status != CatalogVerificationStatus.Verified)
            return ErrorResult(CatalogIdentityInvalidCode, CatalogIdentityInvalidMessage);
        if (catalogVerification.RequiresRequestedScopes && requestedScopes.Count == 0)
            return ErrorResult(ScopesRequiredCode, ScopesRequiredMessage);

        var readiness = await InspectRegistrationAsync(access!, serviceSlug, ct);
        return SerializeReadiness(serviceSlug, readiness);
    }

    private static string SerializeReadiness(
        string serviceSlug,
        ServiceRegistrationReadiness readiness)
    {
        var blocker = readiness.Blocker;
        var registrationRequired =
            readiness.Status == ExternalCapabilityReadinessStatus.ServiceRegistrationRequired &&
            blocker is not null &&
            !string.IsNullOrWhiteSpace(blocker.Code) &&
            !string.IsNullOrWhiteSpace(blocker.SafeMessage);
        return JsonSerializer.Serialize(new
        {
            blocked = registrationRequired,
            service_slug = serviceSlug,
            readiness_status = readiness.Status.ToString(),
            reason_code = blocker?.Code ?? string.Empty,
            safe_message = blocker?.SafeMessage ?? string.Empty,
        });
    }

    private async Task<CatalogVerification> VerifyCatalogServiceAsync(
        ExternalWorkflowCapabilityAccessContext access,
        string serviceSlug,
        CancellationToken ct)
    {
        var tokens = ResolveSourceTokens(access);
        if (tokens.Count == 0)
            return new CatalogVerification(CatalogVerificationStatus.SourceUnavailable, false);

        foreach (var token in tokens)
        {
            var response = await _client.GetCatalogEntryAsync(token, serviceSlug, ct);
            if (TryReadCatalogEntry(response, out var verifiedSlug, out var hasScopeCatalog))
            {
                return string.Equals(serviceSlug, verifiedSlug, StringComparison.Ordinal)
                    ? new CatalogVerification(CatalogVerificationStatus.Verified, hasScopeCatalog)
                    : new CatalogVerification(CatalogVerificationStatus.Invalid, false);
            }

            if (TryReadHttpErrorStatus(response, out var status) && status == 404)
                return new CatalogVerification(CatalogVerificationStatus.Invalid, false);
        }

        return new CatalogVerification(CatalogVerificationStatus.Unavailable, false);
    }

    private async Task<ServiceRegistrationReadiness> InspectRegistrationAsync(
        ExternalWorkflowCapabilityAccessContext access,
        string serviceSlug,
        CancellationToken ct)
    {
        var tokens = ResolveSourceTokens(access);
        var sourceUnavailable = tokens.Count == 0;
        foreach (var token in tokens)
        {
            var response = await _client.ListServicesAsync(token, ct);
            if (!TryReadServiceSlugs(response, out var serviceSlugs))
            {
                sourceUnavailable = true;
                continue;
            }

            if (serviceSlugs.Contains(serviceSlug))
                return new ServiceRegistrationReadiness(ExternalCapabilityReadinessStatus.Ready, null);
        }

        if (sourceUnavailable)
        {
            return new ServiceRegistrationReadiness(
                ExternalCapabilityReadinessStatus.SourceStale,
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.SourceStale,
                    Code = "NYXID_SOURCE_UNAVAILABLE",
                    SafeMessage = "NyxID service capability facts are currently unavailable.",
                });
        }

        return new ServiceRegistrationReadiness(
            ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
            new ExternalCapabilityBlocker
            {
                Status = ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
                Code = "USER_SERVICE_NOT_VISIBLE",
                SafeMessage = "No caller-visible NyxID UserService matches the requested service.",
            });
    }

    private static List<string> ResolveSourceTokens(ExternalWorkflowCapabilityAccessContext access)
    {
        var tokens = new List<string>();
        var sourceReadableBearerToken = access.NyxIdCallerCredential?.SourceReadableUserBearerToken;
        if (!string.IsNullOrWhiteSpace(sourceReadableBearerToken))
            tokens.Add(sourceReadableBearerToken);
        if (!string.IsNullOrWhiteSpace(access.NyxIdOrganizationBearerToken) &&
            !tokens.Contains(access.NyxIdOrganizationBearerToken, StringComparer.Ordinal))
        {
            tokens.Add(access.NyxIdOrganizationBearerToken);
        }

        return tokens;
    }

    private static bool TryReadCatalogEntry(
        string response,
        out string serviceSlug,
        out bool hasScopeCatalog)
    {
        serviceSlug = string.Empty;
        hasScopeCatalog = false;
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("slug", out var slug) ||
                slug.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            serviceSlug = NormalizeSlug(slug.GetString()) ?? string.Empty;
            hasScopeCatalog = root.TryGetProperty("scope_catalog", out var scopeCatalog) &&
                              scopeCatalog.ValueKind == JsonValueKind.Array &&
                              scopeCatalog.GetArrayLength() > 0;
            return serviceSlug.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadHttpErrorStatus(string response, out int status)
    {
        status = 0;
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.True &&
                   root.TryGetProperty("status", out var statusElement) &&
                   statusElement.TryGetInt32(out status);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadServiceSlugs(
        string response,
        out HashSet<string> serviceSlugs)
    {
        serviceSlugs = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (NyxIdUserServiceListJson.IsErrorEnvelope(root, out _) ||
                !NyxIdUserServiceListJson.HasServiceCollection(root))
            {
                return false;
            }

            foreach (var entry in NyxIdUserServiceListJson.EnumerateServiceEntries(root))
            {
                var slug = NormalizeSlug(NyxIdUserServiceListJson.ReadString(
                    entry,
                    "slug",
                    "catalog_service_slug",
                    "catalogServiceSlug",
                    "service_slug",
                    "serviceSlug"));
                if (slug is null)
                    return false;
                serviceSlugs.Add(slug);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        var args = ToolArgs.Parse(argumentsJson);
        var requestedSlug = NormalizeSlug(args.Str("service_slug"));
        if (args.HasParseError ||
            requestedSlug is null ||
            !TryReadRequestedScopes(args, out var requestedScopes))
        {
            return ErrorReceipt(
                callId,
                toolName,
                ArgumentsInvalidCode,
                "service_slug and requested_scopes must be valid");
        }

        if (TryReadError(resultJson, out var errorCode, out var errorMessage))
            return ErrorReceipt(callId, toolName, errorCode, errorMessage);

        if (!TryReadReadiness(
                resultJson,
                out var blocked,
                out var status,
                out var verifiedSlug,
                out var reasonCode,
                out var safeMessage) ||
            !string.Equals(requestedSlug, verifiedSlug, StringComparison.Ordinal))
        {
            return ErrorReceipt(callId, toolName, ResultInvalidCode, ResultInvalidMessage);
        }

        if (status == ExternalCapabilityReadinessStatus.Ready && !blocked)
        {
            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
            };
        }

        if (status != ExternalCapabilityReadinessStatus.ServiceRegistrationRequired ||
            !blocked ||
            string.IsNullOrWhiteSpace(reasonCode) ||
            string.IsNullOrWhiteSpace(safeMessage))
        {
            return status == ExternalCapabilityReadinessStatus.SourceStale &&
                   !blocked &&
                   !string.IsNullOrWhiteSpace(reasonCode) &&
                   !string.IsNullOrWhiteSpace(safeMessage)
                ? ErrorReceipt(callId, toolName, reasonCode, safeMessage)
                : ErrorReceipt(callId, toolName, ResultInvalidCode, ResultInvalidMessage);
        }

        var blocker = BuildVerifiedBlocker(
            args,
            verifiedSlug,
            reasonCode,
            safeMessage,
            requestedScopes);

        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = toolName ?? Name,
            Status = AgentToolReceiptStatus.AuthorizationRequired,
            ErrorCode = blocker.ReasonCode,
            ErrorMessage = blocker.SafeMessage,
            AuthorizationRequired = blocker,
        };
    }

    private static NyxIdAuthorizationRequiredEvent BuildVerifiedBlocker(
        ToolArgs args,
        string verifiedSlug,
        string reasonCode,
        string safeMessage,
        IReadOnlyList<string> requestedScopes)
    {
        var blocker = new NyxIdAuthorizationRequiredEvent
        {
            ServiceSlug = verifiedSlug,
            ReasonCode = reasonCode,
            SafeMessage = safeMessage,
        };
        var serviceLabel = NormalizeLabel(args.Str("service_label"));
        if (serviceLabel != null)
            blocker.ServiceLabel = serviceLabel;
        var resourceUri = NormalizeResourceUri(args.Str("resource_uri"));
        if (resourceUri != null)
            blocker.ResourceUri = resourceUri;
        blocker.RequestedScopes.Add(requestedScopes);
        return blocker;
    }

    private static bool TryReadRequestedScopes(
        ToolArgs args,
        out IReadOnlyList<string> requestedScopes)
    {
        requestedScopes = [];
        var element = args.Element("requested_scopes");
        if (element is null)
            return false;
        if (element.Value.ValueKind != JsonValueKind.Array ||
            element.Value.GetArrayLength() > MaxRequestedScopes)
        {
            return false;
        }

        var normalized = new List<string>();
        foreach (var item in element.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return false;
            var scope = Normalize(item.GetString());
            if (scope is null || scope.Length > 256 || scope.Any(char.IsControl))
                return false;
            if (!normalized.Contains(scope, StringComparer.Ordinal))
                normalized.Add(scope);
        }

        requestedScopes = normalized;
        return true;
    }

    private static AgentToolReceipt ErrorReceipt(
        string callId,
        string toolName,
        string errorCode,
        string errorMessage) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? "nyxid_require_service" : toolName,
            Status = AgentToolReceiptStatus.Error,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };

    private static bool TryResolveAccess(
        out ExternalWorkflowCapabilityAccessContext? access,
        out string? error)
    {
        var scopeId = Normalize(AgentToolRequestContext.OwnerScopeId);
        if (scopeId is null)
        {
            access = null;
            error = "owner_scope_id not available in request context";
            return false;
        }

        var authority = AgentToolRequestContext.NyxIdAuthority;
        var callerId = Normalize(authority.IsComplete ? authority.ExternalUserId : null);
        if (callerId is null)
        {
            access = null;
            error = "verified caller identity not available in request context";
            return false;
        }

        access = new ExternalWorkflowCapabilityAccessContext(
            scopeId,
            callerId,
            NyxIdCallerCredentialSelection.SourceReadableUserBearerOrNull(
                AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
                    AgentToolRequestContext.Current?.Credentials)),
            AgentToolRequestContext.NyxIdOrgToken);
        error = null;
        return true;
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
                !root.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("error_code", out var code) ||
                code.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("safe_message", out var message) ||
                message.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            errorCode = Normalize(code.GetString()) ?? string.Empty;
            errorMessage = Normalize(message.GetString()) ?? string.Empty;
            return errorCode is ArgumentsInvalidCode or
                                CatalogIdentityInvalidCode or
                                CatalogUnavailableCode or
                                ContextUnavailableCode or
                                ScopesRequiredCode &&
                   errorMessage.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadReadiness(
        string resultJson,
        out bool blocked,
        out ExternalCapabilityReadinessStatus status,
        out string serviceSlug,
        out string reasonCode,
        out string safeMessage)
    {
        blocked = false;
        status = ExternalCapabilityReadinessStatus.Unspecified;
        serviceSlug = string.Empty;
        reasonCode = string.Empty;
        safeMessage = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("blocked", out var blockedElement) ||
                blockedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("readiness_status", out var statusElement) ||
                statusElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("service_slug", out var slug) ||
                slug.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("reason_code", out var reason) ||
                reason.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("safe_message", out var message) ||
                message.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var statusName = statusElement.GetString();
            if (!Enum.TryParse(statusName, ignoreCase: false, out ExternalCapabilityReadinessStatus parsedStatus) ||
                !string.Equals(parsedStatus.ToString(), statusName, StringComparison.Ordinal))
                return false;

            blocked = blockedElement.GetBoolean();
            status = parsedStatus;
            serviceSlug = NormalizeSlug(slug.GetString()) ?? string.Empty;
            reasonCode = Normalize(reason.GetString()) ?? string.Empty;
            safeMessage = Normalize(message.GetString()) ?? string.Empty;
            return serviceSlug.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ErrorResult(string errorCode, string safeMessage) =>
        JsonSerializer.Serialize(new
        {
            error = true,
            error_code = errorCode,
            safe_message = safeMessage,
        });

    private static string? NormalizeSlug(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.Length <= 100 &&
               normalized.All(static character =>
                   char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? normalized
            : null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeLabel(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.Length <= 80
            ? normalized
            : null;
    }

    private sealed record ServiceRegistrationReadiness(
        ExternalCapabilityReadinessStatus Status,
        ExternalCapabilityBlocker? Blocker);

    private sealed record CatalogVerification(
        CatalogVerificationStatus Status,
        bool RequiresRequestedScopes);

    private enum CatalogVerificationStatus
    {
        Invalid,
        SourceUnavailable,
        Unavailable,
        Verified,
    }

    private static string? NormalizeResourceUri(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var delimiter = normalized.IndexOfAny(['?', '#']);
        if (delimiter >= 0)
            normalized = normalized[..delimiter];
        return normalized.Length is > 0 and <= 256 ? normalized : null;
    }
}
