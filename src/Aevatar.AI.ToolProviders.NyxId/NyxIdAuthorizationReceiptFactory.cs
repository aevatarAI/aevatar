using Aevatar.AI.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

internal static class NyxIdAuthorizationReceiptFactory
{
    public static AgentToolReceipt? TryCreate(
        string callId,
        string toolName,
        string serviceSlug,
        string? serviceLabel,
        string? resourceUri,
        string resultJson)
    {
        if (!NyxIdApiClient.TryParseProxyError(resultJson, out var error) ||
            error is not { IsAuthorizationRequired: true })
        {
            return null;
        }

        var normalizedSlug = string.IsNullOrWhiteSpace(serviceSlug)
            ? "unknown"
            : serviceSlug.Trim();
        var reasonCode = error.ErrorCode == 1001
            ? "NYXID_UNAUTHORIZED"
            : "NYXID_FORBIDDEN";
        var safeMessage = $"Connect or reauthorize {normalizedSlug} to continue.";
        var authorizationRequired = new NyxIdAuthorizationRequiredEvent
        {
            ServiceSlug = normalizedSlug,
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
            AuthorizationRequired = authorizationRequired,
        };
    }

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
