using System.Globalization;
using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

// Refactor (iter23/cluster-001-nyxid-tool-approval-polling):
//   Old pattern: NyxID approval handler created a request and blocked on Task.Delay polling.
//   New principle: adapter performs one submit or one status read; RoleGAgent owns continuation.
/// <summary>NyxID remote approval submit/status adapter.</summary>
public sealed class NyxIdRemoteToolApprovalPort : IRemoteToolApprovalPort
{
    private readonly NyxIdApiClient _apiClient;
    private readonly ILogger _logger;

    public NyxIdRemoteToolApprovalPort(
        NyxIdApiClient apiClient,
        ILogger<NyxIdRemoteToolApprovalPort>? logger = null)
    {
        _apiClient = apiClient;
        _logger = logger ?? NullLogger<NyxIdRemoteToolApprovalPort>.Instance;
    }

    public async Task<RemoteToolApprovalSubmission> SubmitAsync(
        RemoteToolApprovalRequest request,
        CancellationToken ct)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("NyxID authentication required for remote approval.");

        var body = JsonSerializer.Serialize(new
        {
            tool_name = request.ToolName,
            tool_call_id = request.ToolCallId,
            arguments = request.ArgumentsJson,
            is_destructive = request.IsDestructive,
            approval_mode = request.ApprovalMode.ToString().ToLowerInvariant(),
        });

        var response = await _apiClient.CreateApprovalRequestAsync(token, body, ct);
        var id = ExtractString(response, "id") ?? ExtractString(response, "request_id");
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("Failed to create NyxID approval request: {Response}", response);
            throw new InvalidOperationException("Failed to create remote approval request.");
        }

        return new RemoteToolApprovalSubmission(
            id,
            ExtractDateTimeOffset(response, "expires_at"));
    }

    public async Task<RemoteToolApprovalStatusSnapshot> GetStatusAsync(
        RemoteToolApprovalStatusQuery query,
        CancellationToken ct)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("NyxID authentication required for remote approval.");

        var response = await _apiClient.GetApprovalStatusAsync(token, query.RemoteApprovalId, ct);
        return new RemoteToolApprovalStatusSnapshot(
            MapStatus(ExtractString(response, "status")),
            ExtractString(response, "reason"),
            ExtractDateTimeOffset(response, "expires_at"));
    }

    public async Task<RemoteToolApprovalDecisionResult> DecideAsync(
        RemoteToolApprovalDecision decision,
        CancellationToken ct)
    {
        var token = AgentToolRequestContext.NyxIdAccessToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("NyxID authentication required for remote approval.");

        var response = await _apiClient.DecideApprovalAsync(token, decision.RequestId, decision.Approved, ct);
        return TryReadDecisionError(response, out var result)
            ? result
            : new RemoteToolApprovalDecisionResult(true);
    }

    private static RemoteToolApprovalStatus MapStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "approved" => RemoteToolApprovalStatus.Approved,
            "rejected" or "denied" => RemoteToolApprovalStatus.Rejected,
            "expired" => RemoteToolApprovalStatus.Expired,
            "cancelled" or "canceled" => RemoteToolApprovalStatus.Cancelled,
            "pending" => RemoteToolApprovalStatus.Pending,
            _ => RemoteToolApprovalStatus.Unknown,
        };
    }

    private static string? ExtractString(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ExtractDateTimeOffset(string json, string propertyName)
    {
        var value = ExtractString(json, propertyName);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryReadDecisionError(string response, out RemoteToolApprovalDecisionResult result)
    {
        result = new RemoteToolApprovalDecisionResult(false, Detail: "empty_response");
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var errorProp) ||
                errorProp.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            var status = ReadOptionalInt(root, "status");
            var body = ReadOptionalString(root, "body");
            var message = ReadOptionalString(root, "message");
            var errorKey = ReadOptionalString(root, "error_key") ?? ReadOptionalString(root, "error");
            TryMergeBodyError(body, ref errorKey, ref message);
            result = new RemoteToolApprovalDecisionResult(
                false,
                errorKey,
                message ?? body ?? response,
                status);
            return true;
        }
        catch (JsonException)
        {
            result = new RemoteToolApprovalDecisionResult(
                false,
                "invalid_error_response",
                response);
            return true;
        }
    }

    private static void TryMergeBodyError(string? body, ref string? errorKey, ref string? message)
    {
        if (string.IsNullOrWhiteSpace(body))
            return;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            errorKey ??= ReadOptionalString(root, "error") ?? ReadOptionalString(root, "code");
            message ??= ReadOptionalString(root, "message");
        }
        catch (JsonException)
        {
            if (body.Contains("already_decided", StringComparison.OrdinalIgnoreCase))
                errorKey ??= "already_decided";
            else if (body.Contains("expired", StringComparison.OrdinalIgnoreCase))
                errorKey ??= "forbidden";
        }
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadOptionalInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var value)
            ? value
            : null;
}
