using System.Text.Json;
using Aevatar.AI.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

internal static class NyxIdProxyReceiptFactory
{
    private const string UserServiceSubjectKind = "nyxid.user-service";

    public static AgentToolReceipt? TryCreate(
        string callId,
        string toolName,
        string serviceSlug,
        string? userServiceId,
        string? serviceLabel,
        string? resourceUri,
        string resultJson)
    {
        var normalizedUserServiceId = NormalizeUserServiceId(userServiceId);
        if (!NyxIdApiClient.TryParseProxyError(resultJson, out var error) || error is null)
            return CreateSuccess(callId, toolName, normalizedUserServiceId, resultJson);

        var normalizedSlug = NormalizeSlug(serviceSlug);
        if (error.IsAuthorizationRequired)
            return CreateAuthorizationRequired(
                callId,
                toolName,
                normalizedSlug,
                normalizedUserServiceId,
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

        return CreateError(
            callId,
            toolName,
            normalizedUserServiceId,
            errorCode,
            safeMessage,
            BuildSafeResult(errorCode, safeMessage));
    }

    public static AgentToolReceipt? CreateSuccess(
        string callId,
        string toolName,
        string? userServiceId,
        string resultJson)
    {
        var normalizedUserServiceId = NormalizeUserServiceId(userServiceId);
        return normalizedUserServiceId == null
            ? null
            : new AgentToolReceipt
            {
                CallId = callId ?? string.Empty,
                ToolName = toolName ?? string.Empty,
                Status = AgentToolReceiptStatus.Success,
                SubjectKind = UserServiceSubjectKind,
                SubjectId = normalizedUserServiceId,
                ResultJson = resultJson ?? string.Empty,
            };
    }

    public static AgentToolReceipt CreateError(
        string callId,
        string toolName,
        string? userServiceId,
        string errorCode,
        string errorMessage,
        string resultJson)
    {
        var receipt = new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = toolName ?? string.Empty,
            Status = AgentToolReceiptStatus.Error,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = errorMessage ?? string.Empty,
            ResultJson = resultJson ?? string.Empty,
        };
        AttachUserServiceSubject(receipt, NormalizeUserServiceId(userServiceId));
        return receipt;
    }

    private static AgentToolReceipt CreateAuthorizationRequired(
        string callId,
        string toolName,
        string serviceSlug,
        string? userServiceId,
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
        if (userServiceId != null)
            authorizationRequired.UserServiceId = userServiceId;
        if (!string.IsNullOrWhiteSpace(serviceLabel))
            authorizationRequired.ServiceLabel = serviceLabel.Trim();
        var safeResourceUri = NormalizeResourceUri(resourceUri);
        if (safeResourceUri != null)
            authorizationRequired.ResourceUri = safeResourceUri;

        var receipt = new AgentToolReceipt
        {
            CallId = callId ?? string.Empty,
            ToolName = toolName ?? string.Empty,
            Status = AgentToolReceiptStatus.AuthorizationRequired,
            ErrorCode = reasonCode,
            ErrorMessage = safeMessage,
            ResultJson = BuildSafeResult(reasonCode, safeMessage),
            AuthorizationRequired = authorizationRequired,
        };
        AttachUserServiceSubject(receipt, userServiceId);
        return receipt;
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

    private static string? NormalizeUserServiceId(string? userServiceId)
    {
        var normalized = userServiceId?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) &&
               normalized.Length <= 256 &&
               normalized.All(static character => !char.IsControl(character))
            ? normalized
            : null;
    }

    private static void AttachUserServiceSubject(
        AgentToolReceipt receipt,
        string? normalizedUserServiceId)
    {
        if (normalizedUserServiceId == null)
            return;

        receipt.SubjectKind = UserServiceSubjectKind;
        receipt.SubjectId = normalizedUserServiceId;
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
