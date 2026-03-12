using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Connector invocation module.
/// Handles step_type == "connector_call" and delegates execution to a named connector.
/// </summary>
public sealed partial class ConnectorCallModule : IEventModule
{
    private readonly IConnectorRegistry _registry;

    public ConnectorCallModule(IConnectorRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public string Name => "connector_call";
    public int Priority => 9;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StepRequestEvent.Descriptor) ||
                payload.Is(SecureValueCapturedEvent.Descriptor) ||
                payload.Is(WorkflowCompletedEvent.Descriptor));
    }

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(SecureValueCapturedEvent.Descriptor))
        {
            var captured = envelope.Payload.Unpack<SecureValueCapturedEvent>();
            if (!string.IsNullOrWhiteSpace(captured.Variable) && !string.IsNullOrEmpty(captured.Value))
                SecureValueRuntimeStore.Set(ctx.AgentId, captured.RunId, captured.Variable, captured.Value);
            return;
        }

        if (envelope.Payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            SecureValueRuntimeStore.RemoveRun(ctx.AgentId, envelope.Payload.Unpack<WorkflowCompletedEvent>().RunId);
            return;
        }

        var request = envelope.Payload.Unpack<StepRequestEvent>();
        var canonicalStepType = WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType);
        var isSecureStep = string.Equals(canonicalStepType, "secure_connector_call", StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(canonicalStepType, "connector_call", StringComparison.OrdinalIgnoreCase) &&
            !isSecureStep)
        {
            return;
        }

        var connectorName = WorkflowParameterValueParser.GetString(
            request.Parameters,
            string.Empty,
            "connector",
            "connector_name").Trim();
        var operation = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "operation", "action");
        var retry = ParseBoundedInt(request.Parameters.GetValueOrDefault("retry", "0"), 0, 5, 0);
        var timeoutMs = ParseBoundedInt(request.Parameters.GetValueOrDefault("timeout_ms", "30000"), 100, 300_000, 30_000);
        var optional = ParseBool(request.Parameters.GetValueOrDefault("optional", "false"));
        var onMissing = request.Parameters.GetValueOrDefault("on_missing", "fail");
        var onError = request.Parameters.GetValueOrDefault("on_error", "fail");

        if (string.IsNullOrWhiteSpace(connectorName))
        {
            await PublishFailureAsync(ctx, request, "connector_call missing required parameter: connector", ct);
            return;
        }

        if (!_registry.TryGet(connectorName, out var connector) || connector == null)
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
            var allowed = WorkflowParameterValueParser.ParseStringList(allowedKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
                var runId = string.IsNullOrEmpty(request.RunId) ? envelope.CorrelationId : request.RunId;
                var connectorRequest = new ConnectorRequest
                {
                    RunId = runId,
                    StepId = request.StepId,
                    Connector = connectorName,
                    Operation = operation,
                    Payload = ResolvePayload(request, isSecureStep, ctx.AgentId) ?? string.Empty,
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

    private string? ResolvePayload(StepRequestEvent request, bool isSecureStep, string? agentId)
    {
        var mode = WorkflowParameterValueParser.GetString(
            request.Parameters,
            isSecureStep ? "secure_template" : "input",
            "stdin_mode",
            "stdin").Trim();
        if (string.Equals(mode, "input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "inherit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
        {
            return request.Input;
        }

        if (string.Equals(mode, "secure_variable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "secure_input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "secret_input", StringComparison.OrdinalIgnoreCase))
        {
            var variable = WorkflowParameterValueParser.GetString(
                request.Parameters,
                string.Empty,
                "stdin_secret_variable",
                "secret_variable",
                "secure_variable",
                "variable");
            return ResolveSecureVariable(agentId, request.RunId, variable);
        }

        if (string.Equals(mode, "template", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "secure_template", StringComparison.OrdinalIgnoreCase))
        {
            var template = WorkflowParameterValueParser.GetString(
                request.Parameters,
                request.Input ?? string.Empty,
                "stdin_template",
                "payload_template",
                "stdin_value");
            return ResolveSecureTemplate(agentId, request.RunId, template);
        }

        return request.Input;
    }

    private static string ResolveSecureVariable(string? agentId, string? runId, string variable)
    {
        var normalizedVariable = NormalizeSecureVariableName(variable);
        if (string.IsNullOrWhiteSpace(normalizedVariable))
            throw new InvalidOperationException("connector_call secure stdin requires 'stdin_secret_variable'.");

        if (SecureValueRuntimeStore.TryGet(agentId, runId, normalizedVariable, out var value))
            return value;

        throw new InvalidOperationException(
            $"connector_call is missing captured secure value '{normalizedVariable}' for run '{WorkflowRunIdNormalizer.Normalize(runId)}'.");
    }

    private static string ResolveSecureTemplate(string? agentId, string? runId, string template)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        var withJsonEscapedSecureValues = SecureJsonPlaceholderPattern().Replace(template, match =>
        {
            var variable = match.Groups[1].Value;
            var value = ResolveSecureVariable(agentId, runId, variable);
            return JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
        });

        return SecurePlaceholderPattern().Replace(withJsonEscapedSecureValues, match =>
        {
            var variable = match.Groups[1].Value;
            return ResolveSecureVariable(agentId, runId, variable);
        });
    }

    private static string NormalizeSecureVariableName(string? variable) =>
        string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();

    [GeneratedRegex(@"\[\[secure:([A-Za-z0-9_.:-]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex SecurePlaceholderPattern();

    [GeneratedRegex(@"\[\[secure_json:([A-Za-z0-9_.:-]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex SecureJsonPlaceholderPattern();

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
