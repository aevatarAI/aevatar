using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Connector invocation module.
/// Handles step_type == "connector_call" and delegates execution to a named connector.
/// </summary>
public sealed partial class ConnectorCallModule : IEventModule<IWorkflowExecutionContext>
{
    private readonly IWorkflowConnectorResolver _connectorResolver;

    public ConnectorCallModule(IWorkflowConnectorResolver connectorResolver)
    {
        _connectorResolver = connectorResolver ?? throw new ArgumentNullException(nameof(connectorResolver));
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
    // Refactor (iter110/cluster-1): Old pattern: connector_call resolved connectors, ran external IO, retried/time-limited, and published StepCompletedEvent in the module turn.  New principle: connector_call publishes connector-specific typed intent/result and only continuation reconciliation completes the step.
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(SecureValueCapturedEvent.Descriptor))
        {
            var captured = envelope.Payload.Unpack<SecureValueCapturedEvent>();
            if (!string.IsNullOrWhiteSpace(captured.Variable) && !string.IsNullOrEmpty(captured.Value))
            {
                await SecureInputRuntimeContextAccess.SetCapturedValueAsync(
                    ctx,
                    captured.RunId,
                    captured.Variable,
                    captured.Value,
                    ct);
            }
            return;
        }

        if (envelope.Payload.Is(WorkflowCompletedEvent.Descriptor))
        {
            await SecureInputRuntimeContextAccess.RemoveRunAsync(
                ctx,
                envelope.Payload.Unpack<WorkflowCompletedEvent>().RunId,
                ct);
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
            await PublishConnectorResultAsync(
                ctx,
                WorkflowStepIoContinuationMapper.ConnectorFailure(
                    request,
                    connectorName,
                    operation,
                    "connector_call missing required parameter: connector",
                    timeoutMs),
                ct);
            return;
        }

        var connector = await _connectorResolver.ResolveAsync(ctx, connectorName, ct);
        if (connector == null)
        {
            if (optional || string.Equals(onMissing, "skip", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Logger.LogWarning("ConnectorCall: step={StepId} connector={Connector} not found, skip", request.StepId, connectorName);
                await PublishConnectorResultAsync(
                    ctx,
                    WorkflowStepIoContinuationMapper.ConnectorSkipped(
                        request,
                        connectorName,
                        operation,
                        "connector_not_found",
                        timeoutMs),
                    ct);
                return;
            }

            await PublishConnectorResultAsync(
                ctx,
                WorkflowStepIoContinuationMapper.ConnectorFailure(
                    request,
                    connectorName,
                    operation,
                    $"connector '{connectorName}' not found",
                    timeoutMs),
                ct);
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
                await PublishConnectorResultAsync(
                    ctx,
                    WorkflowStepIoContinuationMapper.ConnectorFailure(
                        request,
                        connectorName,
                        operation,
                        $"connector '{connectorName}' is not allowed for this role (allowed: {string.Join(", ", allowed)})",
                        timeoutMs,
                        connector.Type),
                    ct);
                return;
            }
        }

        var runId = string.IsNullOrEmpty(request.RunId)
            ? envelope.Propagation?.CorrelationId ?? string.Empty
            : request.RunId;
        var intent = new ConnectorCallIntentEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            ConnectorRequestRunId = runId,
            ConnectorName = connectorName,
            Operation = operation,
            Input = request.Input ?? string.Empty,
            Payload = ResolvePayload(request, isSecureStep, ctx) ?? string.Empty,
            RetryCount = retry,
            TimeoutMs = timeoutMs,
            Optional = optional,
            OnMissing = onMissing,
            OnError = onError,
        };
        foreach (var (key, value) in request.Parameters)
            intent.Parameters[key] = value;
        foreach (var (key, value) in WorkflowStepIoContinuationMapper.ExtractConnectorHeaders(ctx))
            intent.Headers[key] = value;

        await ctx.PublishAsync(intent, TopologyAudience.Self, ct);
        await WorkflowStepIoExecutorDispatcher.DispatchConnectorCallAsync(ctx, intent, ct);
    }

    private static async Task PublishConnectorResultAsync(
        IWorkflowExecutionContext ctx,
        ConnectorCallContinuationResultEvent result,
        CancellationToken ct)
    {
        await ctx.PublishAsync(result, TopologyAudience.Self, ct);
    }

    private static bool ParseBool(string raw) =>
        string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);

    private static int ParseBoundedInt(string raw, int min, int max, int fallback)
    {
        if (!int.TryParse(raw, out var parsed)) return fallback;
        if (parsed < min) return min;
        if (parsed > max) return max;
        return parsed;
    }

    private static string NormalizeSecureVariableName(string? variable) =>
        string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();

    [GeneratedRegex(@"\[\[secure:([A-Za-z0-9_.:-]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex SecurePlaceholderPattern();

    [GeneratedRegex(@"\[\[secure_json:([A-Za-z0-9_.:-]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex SecureJsonPlaceholderPattern();

    private string? ResolvePayload(
        StepRequestEvent request,
        bool isSecureStep,
        IWorkflowExecutionContext ctx)
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
            return ResolveSecureVariable(ctx, request.RunId, variable);
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
            return ResolveSecureTemplate(ctx, request.RunId, template);
        }

        return request.Input;
    }

    private static string ResolveSecureVariable(
        IWorkflowExecutionContext ctx,
        string? runId,
        string variable)
    {
        var normalizedVariable = NormalizeSecureVariableName(variable);
        if (string.IsNullOrWhiteSpace(normalizedVariable))
            throw new InvalidOperationException("connector_call secure stdin requires 'stdin_secret_variable'.");

        if (SecureInputRuntimeContextAccess.TryGetCapturedValue(ctx, runId, normalizedVariable, out var value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"connector_call is missing captured secure value '{normalizedVariable}' for run '{WorkflowRunIdNormalizer.Normalize(runId)}'.");
    }

    // Refactor (iter110/cluster-1): Old pattern: secure connector payloads were resolved immediately before inline connector IO.  New principle: module resolves secure payload into the typed connector intent, then executor handles IO separately.
    private static string ResolveSecureTemplate(
        IWorkflowExecutionContext ctx,
        string? runId,
        string template)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        var withJsonEscapedSecureValues = SecureJsonPlaceholderPattern().Replace(template, match =>
        {
            var variable = match.Groups[1].Value;
            var value = ResolveSecureVariable(ctx, runId, variable);
            return JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
        });

        return SecurePlaceholderPattern().Replace(withJsonEscapedSecureValues, match =>
        {
            var variable = match.Groups[1].Value;
            return ResolveSecureVariable(ctx, runId, variable);
        });
    }
}
