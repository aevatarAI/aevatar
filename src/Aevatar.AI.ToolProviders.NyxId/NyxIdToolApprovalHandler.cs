// ─────────────────────────────────────────────────────────────
// NyxIdToolApprovalHandler — NyxID 远程审批
// 通过 NyxID Approvals API 创建审批请求并轮询结果
// ─────────────────────────────────────────────────────────────

using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// NyxID 远程审批处理器。通过 NyxID approvals API 创建审批请求，轮询结果。
/// </summary>
public sealed class NyxIdToolApprovalHandler : IToolApprovalHandler
{
    private const int DefaultTimeoutSeconds = 45;
    private const int PollIntervalMs = 2000;

    private readonly NyxIdApiClient _apiClient;
    private readonly ILogger _logger;
    private readonly int _timeoutSeconds;

    public NyxIdToolApprovalHandler(
        NyxIdApiClient apiClient,
        int timeoutSeconds = DefaultTimeoutSeconds,
        ILogger? logger = null)
    {
        _apiClient = apiClient;
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<ToolApprovalResult> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return ToolApprovalResult.Denied("NyxID authentication required for remote approval.");

        try
        {
            // Create approval request
            var body = JsonSerializer.Serialize(new
            {
                tool_name = request.ToolName,
                tool_call_id = request.ToolCallId,
                arguments = request.ArgumentsJson,
                is_destructive = request.IsDestructive,
                approval_mode = request.ApprovalMode.ToString().ToLowerInvariant(),
            });

            var createResponse = await _apiClient.CreateApprovalRequestAsync(token, body, ct);
            var approvalId = ExtractId(createResponse);
            if (string.IsNullOrWhiteSpace(approvalId))
            {
                _logger.LogWarning("Failed to create NyxID approval request: {Response}", createResponse);
                return ToolApprovalResult.Denied("Failed to create remote approval request.");
            }

            // Poll for result
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            while (!timeoutCts.Token.IsCancellationRequested)
            {
                await Task.Delay(PollIntervalMs, timeoutCts.Token);

                var statusResponse = await _apiClient.GetApprovalAsync(token, approvalId, timeoutCts.Token);
                var status = ExtractStatus(statusResponse);

                switch (status)
                {
                    case "approved":
                        return ToolApprovalResult.Approved("Approved via NyxID.");
                    case "denied":
                        var reason = ExtractReason(statusResponse);
                        return ToolApprovalResult.Denied(reason ?? "Denied via NyxID.");
                    case "pending":
                        continue; // keep polling
                    default:
                        _logger.LogWarning("Unknown NyxID approval status: {Status}", status);
                        continue;
                }
            }

            return ToolApprovalResult.TimedOut("NyxID remote approval timed out.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolApprovalResult.TimedOut("NyxID remote approval timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NyxID approval request failed");
            return ToolApprovalResult.Denied($"NyxID approval error: {ex.Message}");
        }
    }

    private static string? ExtractId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("id", out var id))
                return id.GetString();
            if (doc.RootElement.TryGetProperty("request_id", out var rid))
                return rid.GetString();
            return null;
        }
        catch { return null; }
    }

    private static string? ExtractStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("status", out var status))
                return status.GetString()?.ToLowerInvariant();
            return null;
        }
        catch { return null; }
    }

    private static string? ExtractReason(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("reason", out var reason))
                return reason.GetString();
            return null;
        }
        catch { return null; }
    }
}
