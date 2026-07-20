using System.Text.Json;
using Aevatar.AI.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

internal static class NyxIdProxyReceiptFactory
{
    public static AgentToolReceipt? TryCreate(
        string callId,
        string toolName,
        string serviceSlug,
        string? serviceLabel,
        string? resourceUri,
        string resultJson)
    {
        if (!NyxIdApiClient.TryParseProxyError(resultJson, out var error) || error is null)
            return null;

        var normalizedSlug = NormalizeSlug(serviceSlug);
        if (error.IsAuthorizationRequired)
            return CreateAuthorizationRequired(
                callId,
                toolName,
                normalizedSlug,
                serviceLabel,
                resourceUri);

        var errorCode = error.HttpStatus switch
        {
            401 => "NYXID_PROXY_UNAUTHORIZED",
            403 => "NYXID_PROXY_FORBIDDEN",
            _ => $"NYXID_PROXY_HTTP_{error.HttpStatus}",
        };
        var safeMessage = error.HttpStatus == 403
            ? "The service request was denied."
            : "The service request failed.";

        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = toolName ?? string.Empty,
            Status = AgentToolReceiptStatus.Error,
            ErrorCode = errorCode,
            ErrorMessage = safeMessage,
            ResultJson = BuildSafeResult(errorCode, safeMessage),
        };
    }

    private static AgentToolReceipt CreateAuthorizationRequired(
        string callId,
        string toolName,
        string serviceSlug,
        string? serviceLabel,
        string? resourceUri)
    {
        const string reasonCode = "NYXID_UNAUTHORIZED";
        var safeMessage = $"Connect or reauthorize {serviceSlug} to continue.";
        var authorizationRequired = new NyxIdAuthorizationRequiredEvent
        {
            ServiceSlug = serviceSlug,
            ReasonCode = reasonCode,
            SafeMessage = safeMessage,
        };
        if (!string.IsNullOrWhiteSpace(serviceLabel))
            authorizationRequired.ServiceLabel = serviceLabel.Trim();
        var safeResourceUri = NormalizeResourceUri(resourceUri);
        if (safeResourceUri != null)
            authorizationRequired.ResourceUri = safeResourceUri;

        return new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = toolName ?? string.Empty,
            Status = AgentToolReceiptStatus.AuthorizationRequired,
            ErrorCode = reasonCode,
            ErrorMessage = safeMessage,
            ResultJson = BuildSafeResult(reasonCode, safeMessage),
            AuthorizationRequired = authorizationRequired,
        };
    }

    private static string NormalizeSlug(string? serviceSlug)
    {
        var normalized = serviceSlug?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.Length <= 100 &&
               normalized.All(static character =>
                   char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? normalized
            : "unknown";
    }

    private static string BuildSafeResult(string errorCode, string safeMessage) =>
        JsonSerializer.Serialize(new
        {
            error = errorCode,
            message = safeMessage,
        });

    private static string? NormalizeResourceUri(string? resourceUri)
    {
        if (string.IsNullOrWhiteSpace(resourceUri))
            return null;

        var normalized = resourceUri.Trim();
        var suffixIndex = normalized.IndexOfAny(['?', '#']);
        if (suffixIndex >= 0)
            normalized = normalized[..suffixIndex];

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
