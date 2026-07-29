using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdRequireServiceTool : INyxIdBuiltInTool
{
    private readonly NyxIdApiClient _client;

    public NyxIdRequireServiceTool(NyxIdApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => "nyxid_require_service";

    public string Description =>
        "Verify through live typed readiness whether a required NyxID service is absent, then emit a blocker only when registration is required.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "service_slug": { "type": "string" },
            "service_label": { "type": "string" },
            "resource_uri": { "type": "string" }
          },
          "required": ["service_slug"]
        }
        """;

    public bool IsReadOnly => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var args = ToolArgs.Parse(argumentsJson);
        var serviceSlug = NormalizeSlug(args.Str("service_slug"));
        if (args.HasParseError || serviceSlug is null)
            return """{"error":"service_slug is required"}""";

        if (!TryResolveAccess(out var access, out var error))
            return JsonSerializer.Serialize(new { error });

        var readiness = await InspectRegistrationAsync(access!, serviceSlug, ct);
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

    private async Task<ServiceRegistrationReadiness> InspectRegistrationAsync(
        ExternalWorkflowCapabilityAccessContext access,
        string serviceSlug,
        CancellationToken ct)
    {
        var tokens = new List<string>();
        if (!string.IsNullOrWhiteSpace(access.NyxIdCallerBearerToken))
            tokens.Add(access.NyxIdCallerBearerToken);
        if (!string.IsNullOrWhiteSpace(access.NyxIdOrganizationBearerToken) &&
            !tokens.Contains(access.NyxIdOrganizationBearerToken, StringComparer.Ordinal))
        {
            tokens.Add(access.NyxIdOrganizationBearerToken);
        }

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
        var blocker = BuildVerifiedBlocker(argumentsJson, resultJson);
        if (blocker == null)
            return null;

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

    private static NyxIdAuthorizationRequiredEvent? BuildVerifiedBlocker(
        string argumentsJson,
        string resultJson)
    {
        if (!TryReadVerifiedRegistrationRequired(
                resultJson,
                out var verifiedSlug,
                out var reasonCode,
                out var safeMessage))
        {
            return null;
        }

        var args = ToolArgs.Parse(argumentsJson);
        if (args.HasParseError ||
            !string.Equals(NormalizeSlug(args.Str("service_slug")), verifiedSlug, StringComparison.Ordinal))
        {
            return null;
        }

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
        return blocker;
    }

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
            AgentToolRequestContext.NyxIdAccessToken,
            AgentToolRequestContext.NyxIdOrgToken);
        error = null;
        return true;
    }

    private static bool TryReadVerifiedRegistrationRequired(
        string resultJson,
        out string serviceSlug,
        out string reasonCode,
        out string safeMessage)
    {
        serviceSlug = string.Empty;
        reasonCode = string.Empty;
        safeMessage = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("blocked", out var blocked) ||
                blocked.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("readiness_status", out var status) ||
                !string.Equals(
                    status.GetString(),
                    nameof(ExternalCapabilityReadinessStatus.ServiceRegistrationRequired),
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("service_slug", out var slug) ||
                !root.TryGetProperty("reason_code", out var reason) ||
                !root.TryGetProperty("safe_message", out var message))
            {
                return false;
            }

            serviceSlug = NormalizeSlug(slug.GetString()) ?? string.Empty;
            reasonCode = Normalize(reason.GetString()) ?? string.Empty;
            safeMessage = Normalize(message.GetString()) ?? string.Empty;
            return serviceSlug.Length > 0 && reasonCode.Length > 0 && safeMessage.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
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
        ExternalCapabilityBlocker? Blocker);

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
