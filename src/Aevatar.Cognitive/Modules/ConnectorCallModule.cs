using System.Diagnostics;
using Aevatar;
using Aevatar.Connectors;
using Aevatar.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Cognitive.Modules;

/// <summary>
/// Connector invocation module.
/// Handles step_type == "connector_call" and delegates execution to a named connector.
/// </summary>
public sealed class ConnectorCallModule : IEventModule
{
    public string Name => "connector_call";
    public int Priority => 9;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.TypeUrl?.Contains("StepRequestEvent") == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
        if (!string.Equals(request.StepType, "connector_call", StringComparison.OrdinalIgnoreCase)) return;

        var connectorName = request.Parameters.GetValueOrDefault("connector", "").Trim();
        var operation = request.Parameters.GetValueOrDefault("operation", "");
        var retry = ParseBoundedInt(request.Parameters.GetValueOrDefault("retry", "0"), 0, 5, 0);
        var timeoutMs = ParseBoundedInt(request.Parameters.GetValueOrDefault("timeout_ms", "30000"), 100, 300_000, 30_000);
        var optional = ParseBool(request.Parameters.GetValueOrDefault("optional", "false"));
        var onMissing = request.Parameters.GetValueOrDefault("on_missing", "fail");
        var onError = request.Parameters.GetValueOrDefault("on_error", "fail");

        var registry = ctx.Services.GetService(typeof(IConnectorRegistry)) as IConnectorRegistry;
        if (registry == null)
        {
            await PublishFailureAsync(ctx, request, "connector registry is not registered", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(connectorName))
        {
            await PublishFailureAsync(ctx, request, "connector_call missing required parameter: connector", ct);
            return;
        }

        if (!registry.TryGet(connectorName, out var connector) || connector == null)
        {
            if (optional || string.Equals(onMissing, "skip", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Logger.LogWarning("ConnectorCall: step={StepId} connector={Connector} not found, skip", request.StepId, connectorName);
                await PublishSkippedAsync(ctx, request, connectorName, operation, "connector_not_found", timeoutMs, ct);
                return;
            }

            await PublishFailureAsync(ctx, request, $"connector '{connectorName}' not found", ct);
            return;
        }

        // 当步骤带有 role 且该 role 配置了 connectors 允许列表时，校验当前 connector 是否在列表中
        var allowedKey = request.Parameters.GetValueOrDefault("allowed_connectors", "").Trim();
        if (!string.IsNullOrEmpty(allowedKey))
        {
            var allowed = allowedKey.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allowed.Count > 0 && !allowed.Contains(connectorName))
            {
                await PublishFailureAsync(ctx, request,
                    $"connector '{connectorName}' is not allowed for this role (allowed: {string.Join(", ", allowed)})", ct);
                return;
            }
        }

        var sw = Stopwatch.StartNew();
        var attempts = Math.Max(1, retry + 1);
        ConnectorResponse? response = null;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

            try
            {
                var connectorRequest = new ConnectorRequest
                {
                    RunId = request.RunId,
                    StepId = request.StepId,
                    Connector = connectorName,
                    Operation = operation,
                    Payload = request.Input,
                    Parameters = request.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value),
                };

                response = await connector.ExecuteAsync(connectorRequest, timeoutCts.Token);
                if (response.Success) break;

                lastError = new InvalidOperationException(response.Error);
                if (attempt < attempts)
                {
                    ctx.Logger.LogWarning(
                        "ConnectorCall: step={StepId} connector={Connector} attempt={Attempt}/{Attempts} failed: {Error}",
                        request.StepId, connectorName, attempt, attempts, response.Error);
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < attempts)
                {
                    ctx.Logger.LogWarning(
                        ex,
                        "ConnectorCall: step={StepId} connector={Connector} attempt={Attempt}/{Attempts} exception",
                        request.StepId, connectorName, attempt, attempts);
                }
            }
        }

        sw.Stop();
        if (response is { Success: true })
        {
            var ok = new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = true,
                Output = response.Output ?? "",
            };
            AppendBaseMetadata(ok, connector, connectorName, operation, attempts, timeoutMs, sw.Elapsed.TotalMilliseconds);
            foreach (var (key, value) in response.Metadata)
                ok.Metadata[key] = value;
            await ctx.PublishAsync(ok, EventDirection.Self, ct);
            return;
        }

        var errorText = response?.Error;
        if (string.IsNullOrWhiteSpace(errorText))
            errorText = lastError?.Message ?? "connector call failed";

        if (string.Equals(onError, "continue", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Logger.LogWarning(
                "ConnectorCall: step={StepId} connector={Connector} failed but continue: {Error}",
                request.StepId, connectorName, errorText);

            var continued = new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = true,
                Output = request.Input,
            };
            AppendBaseMetadata(continued, connector, connectorName, operation, attempts, timeoutMs, sw.Elapsed.TotalMilliseconds);
            continued.Metadata["connector.continued_on_error"] = "true";
            continued.Metadata["connector.error"] = errorText ?? "";
            await ctx.PublishAsync(continued, EventDirection.Self, ct);
            return;
        }

        var failed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = errorText ?? "connector call failed",
        };
        AppendBaseMetadata(failed, connector, connectorName, operation, attempts, timeoutMs, sw.Elapsed.TotalMilliseconds);
        await ctx.PublishAsync(failed, EventDirection.Self, ct);
    }

    private static async Task PublishFailureAsync(
        IEventHandlerContext ctx,
        StepRequestEvent request,
        string error,
        CancellationToken ct)
    {
        await ctx.PublishAsync(new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = false,
            Error = error,
        }, EventDirection.Self, ct);
    }

    private static async Task PublishSkippedAsync(
        IEventHandlerContext ctx,
        StepRequestEvent request,
        string connectorName,
        string operation,
        string reason,
        int timeoutMs,
        CancellationToken ct)
    {
        var skipped = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = request.Input,
        };
        skipped.Metadata["connector.skipped"] = "true";
        skipped.Metadata["connector.skip_reason"] = reason;
        skipped.Metadata["connector.name"] = connectorName;
        skipped.Metadata["connector.operation"] = operation;
        skipped.Metadata["connector.timeout_ms"] = timeoutMs.ToString();
        await ctx.PublishAsync(skipped, EventDirection.Self, ct);
    }

    private static void AppendBaseMetadata(
        StepCompletedEvent evt,
        IConnector connector,
        string connectorName,
        string operation,
        int attempts,
        int timeoutMs,
        double durationMs)
    {
        evt.Metadata["connector.name"] = connectorName;
        evt.Metadata["connector.type"] = connector.Type;
        evt.Metadata["connector.operation"] = operation;
        evt.Metadata["connector.attempts"] = attempts.ToString();
        evt.Metadata["connector.timeout_ms"] = timeoutMs.ToString();
        evt.Metadata["connector.duration_ms"] = durationMs.ToString("F2");
    }

    private static int ParseBoundedInt(string raw, int min, int max, int fallback)
    {
        if (!int.TryParse(raw, out var parsed)) return fallback;
        if (parsed < min) return min;
        if (parsed > max) return max;
        return parsed;
    }

    private static bool ParseBool(string raw) =>
        string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
}
