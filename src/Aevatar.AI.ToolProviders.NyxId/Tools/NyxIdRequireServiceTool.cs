using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
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
    private const string InventoryInvalidCode = "NYXID_REQUIRE_SERVICE_INVENTORY_INVALID";
    private const string ServiceAccessRequiredCode = "USER_SERVICE_ACCESS_REQUIRED";
    private const string ScopesInvalidCode = "NYXID_REQUIRE_SERVICE_SCOPES_INVALID";
    private const string ScopesRequiredCode = "NYXID_REQUIRE_SERVICE_SCOPES_REQUIRED";
    private const string CatalogIdentityInvalidMessage =
        "The requested NyxID catalog service identity could not be verified.";
    private const string CatalogUnavailableMessage =
        "The NyxID catalog is currently unavailable.";
    private const string ResultInvalidMessage = "NyxID service readiness returned an invalid result.";
    private const string ScopesInvalidMessage =
        "requested_scopes contains a scope that is not present in the NyxID catalog entry.";
    private const string ScopesRequiredMessage =
        "requested_scopes must select the intended capability from the NyxID catalog.";
    private const string ServiceAccessRequiredMessage =
        "The connected NyxID UserService is not authorized for the current caller bearer.";

    private static readonly TimeSpan McpCatalogFreshnessWindow = TimeSpan.FromMinutes(5);

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
            var sourceUnavailable = await InspectRegistrationAsync(
                access!,
                serviceSlug,
                catalogVerification.ResourceUri,
                ct);
            return SerializeReadiness(serviceSlug, sourceUnavailable);
        }
        if (catalogVerification.Status == CatalogVerificationStatus.Unavailable)
            return ErrorResult(CatalogUnavailableCode, CatalogUnavailableMessage);
        if (catalogVerification.Status != CatalogVerificationStatus.Verified)
            return ErrorResult(CatalogIdentityInvalidCode, CatalogIdentityInvalidMessage);
        if (catalogVerification.AllowedRequestedScopes.Count > 0 && requestedScopes.Count == 0)
            return ErrorResult(ScopesRequiredCode, ScopesRequiredMessage);
        if (requestedScopes.Any(scope => !catalogVerification.AllowedRequestedScopes.Contains(scope)))
            return ErrorResult(ScopesInvalidCode, ScopesInvalidMessage);

        var readiness = await InspectRegistrationAsync(
            access!,
            serviceSlug,
            catalogVerification.ResourceUri,
            ct);
        return SerializeReadiness(serviceSlug, readiness);
    }

    private static string SerializeReadiness(
        string serviceSlug,
        ServiceRegistrationReadiness readiness)
    {
        var blocker = readiness.Blocker;
        var registrationRequired =
            readiness.Status is
                (ExternalCapabilityReadinessStatus.ServiceRegistrationRequired or
                 ExternalCapabilityReadinessStatus.ServiceAccessDenied) &&
            blocker is not null &&
            !string.IsNullOrWhiteSpace(blocker.Code) &&
            !string.IsNullOrWhiteSpace(blocker.SafeMessage);
        return JsonSerializer.Serialize(new
        {
            blocked = registrationRequired,
            service_slug = serviceSlug,
            user_service_id = readiness.UserServiceId ?? string.Empty,
            resource_uri = readiness.ResourceUri ?? string.Empty,
            readiness_status = readiness.Status.ToString(),
            reason_code = blocker?.Code ?? string.Empty,
            safe_message = blocker?.SafeMessage ?? string.Empty,
        });
    }

    private async Task<CatalogVerification> VerifyCatalogServiceAsync(
        RequireServiceReadAccess access,
        string serviceSlug,
        CancellationToken ct)
    {
        var tokens = ResolveManagementReadTokens(access);
        if (tokens.Count == 0)
            return CatalogVerification.SourceUnavailable;

        foreach (var token in tokens)
        {
            var response = await _client.GetCatalogEntryAsync(token, serviceSlug, ct);
            if (TryReadCatalogEntry(
                    response,
                    out var verifiedSlug,
                    out var resourceUri,
                    out var allowedRequestedScopes))
            {
                return string.Equals(serviceSlug, verifiedSlug, StringComparison.Ordinal)
                    ? new CatalogVerification(
                        CatalogVerificationStatus.Verified,
                        resourceUri,
                        allowedRequestedScopes)
                    : CatalogVerification.Invalid;
            }

            if (TryReadHttpErrorStatus(response, out var status) && status == 404)
                return CatalogVerification.Invalid;
        }

        return CatalogVerification.Unavailable;
    }

    private async Task<ServiceRegistrationReadiness> InspectRegistrationAsync(
        RequireServiceReadAccess access,
        string serviceSlug,
        string? resourceUri,
        CancellationToken ct)
    {
        var tokens = ResolveManagementReadTokens(access);
        var sourceUnavailable = tokens.Count == 0;
        var matchingServices = new Dictionary<string, NyxIdUserServiceKey>(StringComparer.Ordinal);
        var inventoryConflict = false;
        foreach (var token in tokens)
        {
            var response = await _client.ListServicesAsync(token, ct);
            var inventory = NyxIdApiAccessResponseParser.ParseUserServiceKeys(response);
            if (!inventory.Succeeded)
            {
                sourceUnavailable = true;
                continue;
            }

            foreach (var service in inventory.Value!.Services.Where(service =>
                         string.Equals(
                             service.CatalogServiceSlug ?? service.Slug,
                             serviceSlug,
                             StringComparison.Ordinal) &&
                         service.IsActive &&
                         service.Connected &&
                         service.CredentialStatus == NyxIdUserServiceCredentialStatus.Active &&
                         service.CredentialSource.Allowed))
            {
                if (matchingServices.TryGetValue(service.Id, out var existing) &&
                    existing != service)
                {
                    inventoryConflict = true;
                    continue;
                }

                matchingServices[service.Id] = service;
            }
        }

        if (sourceUnavailable)
        {
            return new ServiceRegistrationReadiness(
                ExternalCapabilityReadinessStatus.SourceStale,
                null,
                resourceUri,
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.SourceStale,
                    Code = "NYXID_SOURCE_UNAVAILABLE",
                    SafeMessage = "NyxID service capability facts are currently unavailable.",
                });
        }

        if (!inventoryConflict && matchingServices.Count == 1)
        {
            var userServiceId = matchingServices.Values.Single().Id;
            var accessVisibility = await InspectCurrentBearerServiceAccessAsync(
                    access,
                    serviceSlug,
                    userServiceId,
                    ct)
                .ConfigureAwait(false);
            if (accessVisibility == ServiceAccessVisibility.Authorized)
            {
                return new ServiceRegistrationReadiness(
                    ExternalCapabilityReadinessStatus.Ready,
                    userServiceId,
                    resourceUri,
                    null);
            }

            if (accessVisibility == ServiceAccessVisibility.NotAuthorized)
            {
                // The catalog read can be degraded while the bearer probe still
                // proves "connected but not authorized for this session". Derive
                // the canonical resource indicator from the pinned proxy route
                // shape so the actionable access-review blocker survives catalog
                // degradation instead of collapsing into an opaque inventory error.
                var effectiveResourceUri = string.IsNullOrWhiteSpace(resourceUri)
                    ? _client.BuildServiceProxyResourceUri(serviceSlug)
                    : resourceUri;
                return new ServiceRegistrationReadiness(
                    ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                    userServiceId,
                    effectiveResourceUri,
                    new ExternalCapabilityBlocker
                    {
                        Status = ExternalCapabilityReadinessStatus.ServiceAccessDenied,
                        Code = ServiceAccessRequiredCode,
                        SafeMessage = ServiceAccessRequiredMessage,
                    });
            }

            return new ServiceRegistrationReadiness(
                ExternalCapabilityReadinessStatus.SourceStale,
                null,
                resourceUri,
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.SourceStale,
                    Code = InventoryInvalidCode,
                    SafeMessage =
                        "NyxID service access readiness could not verify one exact current-bearer route.",
                });
        }

        if (inventoryConflict || matchingServices.Count > 1)
        {
            return new ServiceRegistrationReadiness(
                ExternalCapabilityReadinessStatus.SourceStale,
                null,
                resourceUri,
                new ExternalCapabilityBlocker
                {
                    Status = ExternalCapabilityReadinessStatus.SourceStale,
                    Code = InventoryInvalidCode,
                    SafeMessage =
                        "NyxID service readiness did not identify one exact caller-visible UserService.",
                });
        }

        return new ServiceRegistrationReadiness(
            ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
            null,
            resourceUri,
            new ExternalCapabilityBlocker
            {
                Status = ExternalCapabilityReadinessStatus.ServiceRegistrationRequired,
                Code = "USER_SERVICE_NOT_VISIBLE",
                SafeMessage = "No caller-visible NyxID UserService matches the requested service.",
            });
    }

    private async Task<ServiceAccessVisibility> InspectCurrentBearerServiceAccessAsync(
        RequireServiceReadAccess access,
        string serviceSlug,
        string userServiceId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(access.ExecutionBearerToken))
            return ServiceAccessVisibility.SourceUnavailable;

        var response = await _client.GetMcpConfigAsync(access.ExecutionBearerToken, ct)
            .ConfigureAwait(false);
        var catalog = NyxIdMcpOperationCatalog.Parse(
            response,
            "require-service",
            DateTimeOffset.UtcNow,
            McpCatalogFreshnessWindow);
        if (catalog.AccessDenied || catalog.SourceUnavailable)
            return ServiceAccessVisibility.SourceUnavailable;

        var matches = catalog.Services
            .Where(service => string.Equals(
                service.UserServiceId,
                userServiceId,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
            return ServiceAccessVisibility.NotAuthorized;
        if (matches.Length != 1 ||
            !string.Equals(matches[0].ServiceSlug, serviceSlug, StringComparison.Ordinal))
        {
            return ServiceAccessVisibility.Conflict;
        }

        return ServiceAccessVisibility.Authorized;
    }

    private static List<string> ResolveManagementReadTokens(RequireServiceReadAccess access)
    {
        var tokens = new List<string>();
        AddDistinct(tokens, access.SourceReadableUserBearerToken);
        AddDistinct(tokens, access.OrganizationBearerToken);

        // This authority is deliberately local to the two NyxID account:read operations owned by
        // this tool: GET /api/v1/catalog/{slug} and GET /api/v1/keys. It must not be exposed as a
        // generic source-readable credential or reused for management writes.
        AddDistinct(tokens, access.DelegatedManagementReadBearerToken);
        return tokens;
    }

    private static void AddDistinct(List<string> tokens, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token) &&
            !tokens.Contains(token, StringComparer.Ordinal))
        {
            tokens.Add(token);
        }
    }

    private static bool TryReadCatalogEntry(
        string response,
        out string serviceSlug,
        out string? resourceUri,
        out IReadOnlySet<string> allowedRequestedScopes)
    {
        serviceSlug = string.Empty;
        resourceUri = null;
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        allowedRequestedScopes = scopes;
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
            resourceUri = root.TryGetProperty("resource_uri", out var resource) &&
                          resource.ValueKind == JsonValueKind.String
                ? NormalizeResourceUri(resource.GetString())
                : null;
            if (root.TryGetProperty("scope_catalog", out var scopeCatalog) &&
                scopeCatalog.ValueKind != JsonValueKind.Null)
            {
                if (scopeCatalog.ValueKind != JsonValueKind.Array)
                    return false;

                foreach (var entry in scopeCatalog.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object ||
                        !entry.TryGetProperty("scope", out var scopeElement) ||
                        scopeElement.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    var scope = Normalize(scopeElement.GetString());
                    if (scope is null || scope.Length > 256 || scope.Any(char.IsControl))
                        return false;
                    scopes.Add(scope);
                }
            }

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
                "service_slug and requested_scopes must be valid",
                ErrorResult(
                    ArgumentsInvalidCode,
                    "service_slug and requested_scopes must be valid"));
        }

        if (TryReadError(resultJson, out var errorCode, out var errorMessage))
            return ErrorReceipt(callId, toolName, errorCode, errorMessage, resultJson);

        if (!TryReadReadiness(
                resultJson,
                out var blocked,
                out var status,
                out var verifiedSlug,
                out var userServiceId,
                out var resourceUri,
                out var reasonCode,
                out var safeMessage) ||
            !string.Equals(requestedSlug, verifiedSlug, StringComparison.Ordinal))
        {
            return ErrorReceipt(
                callId,
                toolName,
                ResultInvalidCode,
                ResultInvalidMessage,
                ErrorResult(ResultInvalidCode, ResultInvalidMessage));
        }

        if (status == ExternalCapabilityReadinessStatus.Ready && !blocked)
        {
            if (string.IsNullOrWhiteSpace(userServiceId))
            {
                return ErrorReceipt(
                    callId,
                    toolName,
                    ResultInvalidCode,
                    ResultInvalidMessage,
                    ErrorResult(ResultInvalidCode, ResultInvalidMessage));
            }

            return new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = string.IsNullOrWhiteSpace(toolName) ? Name : toolName,
                Status = AgentToolReceiptStatus.Success,
                ResultJson = resultJson,
                ProviderResourceId = userServiceId,
            };
        }

        if (status is not
                (ExternalCapabilityReadinessStatus.ServiceRegistrationRequired or
                 ExternalCapabilityReadinessStatus.ServiceAccessDenied) ||
            !blocked ||
            string.IsNullOrWhiteSpace(reasonCode) ||
            string.IsNullOrWhiteSpace(safeMessage) ||
            (status == ExternalCapabilityReadinessStatus.ServiceAccessDenied &&
             (string.IsNullOrWhiteSpace(userServiceId) || string.IsNullOrWhiteSpace(resourceUri))))
        {
            return status == ExternalCapabilityReadinessStatus.SourceStale &&
                   !blocked &&
                   !string.IsNullOrWhiteSpace(reasonCode) &&
                   !string.IsNullOrWhiteSpace(safeMessage)
                ? ErrorReceipt(callId, toolName, reasonCode, safeMessage, resultJson)
                : ErrorReceipt(
                    callId,
                    toolName,
                    ResultInvalidCode,
                    ResultInvalidMessage,
                    ErrorResult(ResultInvalidCode, ResultInvalidMessage));
        }

        var blocker = BuildVerifiedBlocker(
            args,
            verifiedSlug,
            reasonCode,
            safeMessage,
            requestedScopes,
            userServiceId,
            resourceUri);

        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = toolName ?? Name,
            Status = AgentToolReceiptStatus.AuthorizationRequired,
            ResultJson = resultJson,
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
        IReadOnlyList<string> requestedScopes,
        string userServiceId,
        string resourceUri)
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
        var verifiedResourceUri = NormalizeResourceUri(resourceUri) ??
                                  NormalizeResourceUri(args.Str("resource_uri"));
        if (verifiedResourceUri != null)
            blocker.ResourceUri = verifiedResourceUri;
        if (!string.IsNullOrWhiteSpace(userServiceId))
            blocker.UserServiceId = userServiceId;
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
        string errorMessage,
        string resultJson) =>
        new()
        {
            CallId = callId ?? string.Empty,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? "nyxid_require_service" : toolName,
            Status = AgentToolReceiptStatus.Error,
            ResultJson = resultJson,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };

    private static bool TryResolveAccess(
        out RequireServiceReadAccess? access,
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

        var credentials = AgentToolRequestContext.Current?.Credentials;
        access = new RequireServiceReadAccess(
            scopeId,
            callerId,
            NormalizeBearerToken(credentials?.NyxIdAccessToken),
            AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(credentials),
            NormalizeBearerToken(AgentToolRequestContext.NyxIdOrgToken),
            credentials?.NyxIdCredentialKind == AgentToolNyxIdCredentialKind.ProxyDelegation
                ? NormalizeBearerToken(credentials.NyxIdAccessToken)
                : null);
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
                                ResultInvalidCode or
                                ScopesInvalidCode or
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
        out string userServiceId,
        out string resourceUri,
        out string reasonCode,
        out string safeMessage)
    {
        blocked = false;
        status = ExternalCapabilityReadinessStatus.Unspecified;
        serviceSlug = string.Empty;
        userServiceId = string.Empty;
        resourceUri = string.Empty;
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
                !root.TryGetProperty("user_service_id", out var resourceId) ||
                resourceId.ValueKind != JsonValueKind.String ||
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
            userServiceId = Normalize(resourceId.GetString()) ?? string.Empty;
            resourceUri = root.TryGetProperty("resource_uri", out var resourceUriElement) &&
                          resourceUriElement.ValueKind == JsonValueKind.String
                ? NormalizeResourceUri(resourceUriElement.GetString()) ?? string.Empty
                : string.Empty;
            reasonCode = Normalize(reason.GetString()) ?? string.Empty;
            safeMessage = Normalize(message.GetString()) ?? string.Empty;
            return serviceSlug.Length > 0 &&
                   (status != ExternalCapabilityReadinessStatus.Ready || userServiceId.Length > 0);
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
            reason_code = errorCode,
            safe_message = safeMessage,
        });

    private static string? NormalizeBearerToken(string? token)
    {
        var normalized = Normalize(token);
        if (normalized is null ||
            string.Equals(normalized, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            normalized.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return normalized;
    }

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
        string? UserServiceId,
        string? ResourceUri,
        ExternalCapabilityBlocker? Blocker);

    private sealed record CatalogVerification(
        CatalogVerificationStatus Status,
        string? ResourceUri,
        IReadOnlySet<string> AllowedRequestedScopes)
    {
        public static CatalogVerification Invalid { get; } =
            new(CatalogVerificationStatus.Invalid, null, EmptyScopes());

        public static CatalogVerification SourceUnavailable { get; } =
            new(CatalogVerificationStatus.SourceUnavailable, null, EmptyScopes());

        public static CatalogVerification Unavailable { get; } =
            new(CatalogVerificationStatus.Unavailable, null, EmptyScopes());

        private static IReadOnlySet<string> EmptyScopes() =>
            new HashSet<string>(StringComparer.Ordinal);
    }

    private sealed record RequireServiceReadAccess(
        string ScopeId,
        string CallerId,
        string? ExecutionBearerToken,
        string? SourceReadableUserBearerToken,
        string? OrganizationBearerToken,
        string? DelegatedManagementReadBearerToken);

    private enum CatalogVerificationStatus
    {
        Invalid,
        SourceUnavailable,
        Unavailable,
        Verified,
    }

    private enum ServiceAccessVisibility
    {
        SourceUnavailable,
        NotAuthorized,
        Conflict,
        Authorized,
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
