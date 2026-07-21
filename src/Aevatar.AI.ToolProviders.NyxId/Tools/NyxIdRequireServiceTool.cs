using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

public sealed class NyxIdRequireServiceTool : INyxIdBuiltInTool
{
    public string Name => "nyxid_require_service";

    public string Description =>
        "Emit a typed authorization blocker when a required service is absent from connected-services.";

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

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var blocker = BuildBlocker(argumentsJson);
        return Task.FromResult(blocker == null
            ? """{"error":"service_slug is required"}"""
            : JsonSerializer.Serialize(new
            {
                blocked = true,
                service_slug = blocker.ServiceSlug,
                reason_code = blocker.ReasonCode,
                safe_message = blocker.SafeMessage,
            }));
    }

    public AgentToolReceipt? CreateResultReceipt(
        string callId,
        string toolName,
        string argumentsJson,
        string resultJson)
    {
        var blocker = BuildBlocker(argumentsJson);
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

    private static NyxIdAuthorizationRequiredEvent? BuildBlocker(string argumentsJson)
    {
        var args = ToolArgs.Parse(argumentsJson);
        var serviceSlug = NormalizeSlug(args.Str("service_slug"));
        if (args.HasParseError || serviceSlug == null)
            return null;

        var blocker = new NyxIdAuthorizationRequiredEvent
        {
            ServiceSlug = serviceSlug,
            ReasonCode = "NYXID_SERVICE_NOT_CONNECTED",
            SafeMessage = $"Connect {serviceSlug} to continue.",
        };
        var serviceLabel = NormalizeLabel(args.Str("service_label"));
        if (serviceLabel != null)
            blocker.ServiceLabel = serviceLabel;
        var resourceUri = NormalizeResourceUri(args.Str("resource_uri"));
        if (resourceUri != null)
            blocker.ResourceUri = resourceUri;
        return blocker;
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

    private static string? NormalizeLabel(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.Length <= 80
            ? normalized
            : null;
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
