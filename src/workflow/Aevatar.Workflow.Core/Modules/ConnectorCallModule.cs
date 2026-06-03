using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Core.Execution;
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
    private const string ModuleStateKey = "connector_call";
    private const string TimeoutCallbackPrefix = "workflow-connector-timeout";
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
                payload.Is(WorkflowConnectorTimeoutFiredEvent.Descriptor) ||
                payload.Is(WorkflowConnectorAttemptCompletedEvent.Descriptor) ||
                payload.Is(WorkflowCompletedEvent.Descriptor));
    }

    /// <inheritdoc />
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

        if (envelope.Payload.Is(WorkflowConnectorTimeoutFiredEvent.Descriptor))
        {
            await HandleTimeoutFiredAsync(envelope.Payload.Unpack<WorkflowConnectorTimeoutFiredEvent>(), envelope, ctx, ct);
            return;
        }

        if (envelope.Payload.Is(WorkflowConnectorAttemptCompletedEvent.Descriptor))
        {
            await HandleAttemptCompletedAsync(envelope.Payload.Unpack<WorkflowConnectorAttemptCompletedEvent>(), ctx, ct);
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

        var connector = await _connectorResolver.ResolveAsync(ctx, connectorName, ct);
        if (connector == null)
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

        // Refactor (iter89/cluster-089-workflow-module-clock-state):
        //   Old: Connector elapsed metadata used Stopwatch directly inside
        //        the module.
        //   New: Connector duration is measured through the workflow context
        //        monotonic elapsed API.
        var attempts = Math.Max(1, retry + 1);
        var runId = string.IsNullOrEmpty(request.RunId)
            ? envelope.Propagation?.CorrelationId ?? string.Empty
            : request.RunId;
        await StartAttemptAsync(
            envelope,
            request,
            runId,
            connectorName,
            operation,
            connector,
            attempt: 1,
            attempts,
            timeoutMs,
            string.Equals(onError, "continue", StringComparison.OrdinalIgnoreCase),
            isSecureStep,
            ctx,
            ct);
    }

    private async Task HandleTimeoutFiredAsync(
        WorkflowConnectorTimeoutFiredEvent evt,
        EventEnvelope envelope,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.OperationId))
            return;

        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.PendingByOperationId.TryGetValue(evt.OperationId, out var pending))
            return;

        if (!MatchesPendingTimeout(envelope, pending))
        {
            ctx.Logger.LogDebug(
                "ConnectorCall: ignore timeout without matching lease operation={OperationId}",
                evt.OperationId);
            return;
        }

        // Refactor (iter158/cluster-157-004-timeout-cts):
        //   Old: connector timeout used inline CTS/call-stack cancellation, so late
        //        connector completions raced with in-memory continuation state.
        //   New: the actor owns a typed durable timeout event keyed by operation id;
        //        timeout and late completion both reconcile through persisted module state.
        var completion = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            Success = pending.OnErrorContinue,
            Output = pending.OnErrorContinue ? pending.Input : string.Empty,
            Error = pending.OnErrorContinue ? string.Empty : $"connector call timed out after {evt.TimeoutMs}ms",
            ExecutionId = pending.ExecutionId,
        };
        completion.Annotations["connector.name"] = pending.ConnectorName;
        completion.Annotations["connector.step_id"] = pending.StepId;
        completion.Annotations["connector.run_id"] = pending.RunId;
        completion.Annotations["connector.type"] = pending.ConnectorType;
        completion.Annotations["connector.operation"] = pending.Operation;
        completion.Annotations["connector.attempts"] = pending.Attempt.ToString();
        completion.Annotations["connector.timeout_ms"] = evt.TimeoutMs.ToString();
        completion.Annotations["connector.duration_ms"] = evt.TimeoutMs.ToString("F2");
        completion.Annotations["connector.timeout_fired"] = "true";
        if (pending.OnErrorContinue)
        {
            completion.Annotations["connector.continued_on_error"] = "true";
            completion.Annotations["connector.error"] = completion.Error;
        }

        await ctx.PublishAsync(completion, TopologyAudience.Self, ct);
        RemovePending(state, pending);
        await SaveStateAsync(state, ctx, ct);
    }

    private async Task HandleAttemptCompletedAsync(
        WorkflowConnectorAttemptCompletedEvent evt,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.OperationId))
            return;

        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.PendingByOperationId.TryGetValue(evt.OperationId, out var pending))
            return;

        RemovePending(state, pending);
        await SaveStateAsync(state, ctx, ct);
        await WorkflowRuntimeCallbackLeaseSupport.CancelAsync(ctx, pending.TimeoutLease, CancellationToken.None);

        var durationMs = ParseDuration(evt);
        if (evt.Success)
        {
            var resolvedOutput = evt.Output ?? string.Empty;
            if (!TryAssertResponseOutput(pending.Parameters, resolvedOutput, out var assertionError))
            {
                await PublishPendingCompletionAsync(pending, false, string.Empty, assertionError, durationMs, evt.Annotations, ctx, ct);
                return;
            }

            if (ParseBool(pending.Parameters.GetValueOrDefault("pass_through_input", "false")))
                resolvedOutput = pending.Input ?? string.Empty;

            await PublishPendingCompletionAsync(pending, true, resolvedOutput, string.Empty, durationMs, evt.Annotations, ctx, ct);
            return;
        }

        var errorText = string.IsNullOrWhiteSpace(evt.Error) ? "connector call failed" : evt.Error;
        if (pending.Attempt < pending.Attempts)
        {
            ctx.Logger.LogWarning(
                "ConnectorCall: step={StepId} connector={Connector} attempt={Attempt}/{Attempts} failed: {Error}",
                pending.StepId, pending.ConnectorName, pending.Attempt, pending.Attempts, errorText);
            var nextRequest = BuildRetryStepRequest(pending);
            var connector = await _connectorResolver.ResolveAsync(ctx, pending.ConnectorName, ct);
            if (connector == null)
            {
                await PublishPendingCompletionAsync(
                    pending,
                    false,
                    string.Empty,
                    $"connector '{pending.ConnectorName}' not found",
                    durationMs,
                    evt.Annotations,
                    ctx,
                    ct);
                return;
            }

            await StartAttemptAsync(
                new EventEnvelope { Id = pending.OperationId },
                nextRequest,
                pending.RunId,
                pending.ConnectorName,
                pending.Operation,
                connector,
                pending.Attempt + 1,
                pending.Attempts,
                pending.TimeoutMs,
                pending.OnErrorContinue,
                pending.SecureStep,
                ctx,
                ct);
            return;
        }

        await PublishPendingCompletionAsync(
            pending,
            false,
            string.Empty,
            errorText,
            durationMs,
            evt.Annotations,
            ctx,
            ct);
    }

    private async Task StartAttemptAsync(
        EventEnvelope envelope,
        StepRequestEvent request,
        string runId,
        string connectorName,
        string operation,
        IConnector connector,
        int attempt,
        int attempts,
        int timeoutMs,
        bool onErrorContinue,
        bool isSecureStep,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var pending = await RegisterPendingAsync(
            envelope,
            request,
            runId,
            connectorName,
            operation,
            connector.Type,
            attempt,
            attempts,
            timeoutMs,
            onErrorContinue,
            isSecureStep,
            ctx,
            ct);
        var connectorRequest = new ConnectorRequest
        {
            HttpAuthorization = ExtractConnectorHttpAuthorization(ctx),
            RunId = runId,
            StepId = request.StepId,
            Connector = connectorName,
            Operation = operation,
            Payload = ResolvePayload(request, isSecureStep, ctx) ?? string.Empty,
            Parameters = request.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        _ = ExecuteConnectorAndSignalAsync(ctx, connector, connectorRequest, pending);
    }

    private static async Task ExecuteConnectorAndSignalAsync(
        IWorkflowExecutionContext ctx,
        IConnector connector,
        ConnectorRequest request,
        PendingConnectorCallState pending)
    {
        var startedAt = ctx.GetTimestamp();
        var completed = new WorkflowConnectorAttemptCompletedEvent
        {
            RunId = pending.RunId,
            StepId = pending.StepId,
            OperationId = pending.OperationId,
            Attempt = pending.Attempt,
            ExecutionId = pending.ExecutionId,
        };
        try
        {
            var response = await connector.ExecuteAsync(request, CancellationToken.None);
            completed.Success = response.Success;
            completed.Output = response.Output ?? string.Empty;
            completed.Error = response.Error ?? string.Empty;
            foreach (var (key, value) in response.Metadata)
                completed.Annotations[key] = value;
        }
        catch (Exception ex)
        {
            completed.Success = false;
            completed.Error = ex.Message;
        }

        completed.Annotations["connector.duration_ms"] = ctx.GetElapsedTime(startedAt).TotalMilliseconds.ToString("F2");
        await ctx.PublishAsync(completed, TopologyAudience.Self, CancellationToken.None);
    }

    private async Task<PendingConnectorCallState> RegisterPendingAsync(
        EventEnvelope envelope,
        StepRequestEvent request,
        string runId,
        string connectorName,
        string operation,
        string connectorType,
        int attempt,
        int attempts,
        int timeoutMs,
        bool onErrorContinue,
        bool isSecureStep,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var operationId = BuildOperationId(runId, request.StepId, attempt, request.ExecutionId, ResolveOriginEnvelopeId(envelope));
        var callbackId = RuntimeCallbackKeyComposer.BuildCallbackId(
            TimeoutCallbackPrefix,
            runId,
            request.StepId,
            operationId,
            attempt.ToString());
        var pending = new PendingConnectorCallState
        {
            StepId = request.StepId,
            RunId = runId,
            OperationId = operationId,
            Input = request.Input ?? string.Empty,
            ConnectorName = connectorName,
            Operation = operation,
            Attempt = attempt,
            Attempts = attempts,
            TimeoutMs = timeoutMs,
            OnErrorContinue = onErrorContinue,
            TimeoutCallbackId = callbackId,
            ExecutionId = request.ExecutionId,
            SecureStep = isSecureStep,
            ConnectorType = connectorType,
        };
        foreach (var (key, value) in request.Parameters)
            pending.Parameters[key] = value;

        var state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        RemovePendingForStep(state, runId, request.StepId);
        state.PendingByOperationId[operationId] = pending;
        state.PendingOperationIdByStepId[BuildStepKey(runId, request.StepId)] = operationId;
        await SaveStateAsync(state, ctx, ct);

        var lease = await ctx.ScheduleSelfDurableTimeoutAsync(
            callbackId,
            TimeSpan.FromMilliseconds(timeoutMs),
            new WorkflowConnectorTimeoutFiredEvent
            {
                RunId = runId,
                StepId = request.StepId,
                OperationId = operationId,
                TimeoutMs = timeoutMs,
                Attempt = attempt,
            },
            ct: ct);

        state = WorkflowExecutionStateAccess.Load<ConnectorCallModuleState>(ctx, ModuleStateKey);
        if (!state.PendingByOperationId.TryGetValue(operationId, out pending))
            return pending;

        pending.TimeoutLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
        state.PendingByOperationId[operationId] = pending;
        await SaveStateAsync(state, ctx, ct);
        return pending;
    }

    private static bool MatchesPendingTimeout(EventEnvelope envelope, PendingConnectorCallState pending)
    {
        if (pending.TimeoutLease != null)
            return WorkflowRuntimeCallbackLeaseSupport.MatchesLease(envelope, pending.TimeoutLease);

        return RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) &&
               string.Equals(callbackState.CallbackId, pending.TimeoutCallbackId, StringComparison.Ordinal);
    }

    private static void RemovePendingForStep(
        ConnectorCallModuleState state,
        string runId,
        string stepId)
    {
        var stepKey = BuildStepKey(runId, stepId);
        if (!state.PendingOperationIdByStepId.Remove(stepKey, out var operationId))
            return;

        state.PendingByOperationId.Remove(operationId);
    }

    private static void RemovePending(
        ConnectorCallModuleState state,
        PendingConnectorCallState pending)
    {
        state.PendingByOperationId.Remove(pending.OperationId);
        state.PendingOperationIdByStepId.Remove(BuildStepKey(pending.RunId, pending.StepId));
    }

    private static Task SaveStateAsync(
        ConnectorCallModuleState state,
        IWorkflowExecutionContext ctx,
        CancellationToken ct) =>
        WorkflowExecutionStateAccess.SaveAsync(ctx, ModuleStateKey, state, ct);

    private static string ResolveOriginEnvelopeId(EventEnvelope envelope) =>
        string.IsNullOrWhiteSpace(envelope.Id)
            ? Guid.NewGuid().ToString("N")
            : envelope.Id;

    private static string BuildOperationId(
        string runId,
        string stepId,
        int attempt,
        string executionId,
        string originEnvelopeId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            "connector-operation",
            runId,
            stepId,
            string.IsNullOrWhiteSpace(executionId) ? originEnvelopeId : executionId,
            attempt.ToString());

    private static string BuildStepKey(string runId, string stepId) =>
        $"{runId}:{stepId}";

    private static StepRequestEvent BuildRetryStepRequest(PendingConnectorCallState pending)
    {
        var request = new StepRequestEvent
        {
            StepId = pending.StepId,
            StepType = pending.SecureStep ? "secure_connector_call" : "connector_call",
            RunId = pending.RunId,
            Input = pending.Input,
            ExecutionId = pending.ExecutionId,
        };
        foreach (var (key, value) in pending.Parameters)
            request.Parameters[key] = value;
        return request;
    }

    private static double ParseDuration(WorkflowConnectorAttemptCompletedEvent evt)
    {
        return double.TryParse(
            evt.Annotations.GetValueOrDefault("connector.duration_ms", "0"),
            out var durationMs)
            ? durationMs
            : 0d;
    }

    private static async Task PublishPendingCompletionAsync(
        PendingConnectorCallState pending,
        bool success,
        string output,
        string error,
        double durationMs,
        IReadOnlyDictionary<string, string> responseAnnotations,
        IWorkflowExecutionContext ctx,
        CancellationToken ct)
    {
        var completed = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = pending.RunId,
            Success = success,
            Output = success ? output : string.Empty,
            Error = success ? string.Empty : error,
            ExecutionId = pending.ExecutionId,
        };
        AppendBaseMetadata(completed, pending, durationMs);
        foreach (var (key, value) in responseAnnotations)
        {
            if (!string.Equals(key, "connector.duration_ms", StringComparison.Ordinal))
                completed.Annotations[key] = value;
        }

        if (!success && pending.OnErrorContinue)
        {
            completed.Success = true;
            completed.Output = pending.Input;
            completed.Error = string.Empty;
            completed.Annotations["connector.continued_on_error"] = "true";
            completed.Annotations["connector.error"] = error ?? string.Empty;
        }

        await ctx.PublishAsync(completed, TopologyAudience.Self, ct);
    }

    private static async Task PublishFailureAsync(
        IWorkflowExecutionContext ctx,
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
        }, TopologyAudience.Self, ct);
    }

    private static async Task PublishSkippedAsync(
        IWorkflowExecutionContext ctx,
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
        skipped.Annotations["connector.skipped"] = "true";
        skipped.Annotations["connector.skip_reason"] = reason;
        skipped.Annotations["connector.name"] = connectorName;
        skipped.Annotations["connector.operation"] = operation;
        skipped.Annotations["connector.timeout_ms"] = timeoutMs.ToString();
        await ctx.PublishAsync(skipped, TopologyAudience.Self, ct);
    }

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

    private static string NormalizeSecureVariableName(string? variable) =>
        string.IsNullOrWhiteSpace(variable) ? string.Empty : variable.Trim();

    [GeneratedRegex(@"\[\[secure:([A-Za-z0-9_.:-]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex SecurePlaceholderPattern();

    [GeneratedRegex(@"\[\[secure_json:([A-Za-z0-9_.:-]+)\]\]", RegexOptions.Compiled)]
    private static partial Regex SecureJsonPlaceholderPattern();

    private static void AppendBaseMetadata(
        StepCompletedEvent evt,
        PendingConnectorCallState pending,
        double durationMs)
    {
        evt.Annotations["connector.name"] = pending.ConnectorName;
        evt.Annotations["connector.type"] = pending.ConnectorType;
        evt.Annotations["connector.operation"] = pending.Operation;
        evt.Annotations["connector.attempts"] = pending.Attempt.ToString();
        evt.Annotations["connector.timeout_ms"] = pending.TimeoutMs.ToString();
        evt.Annotations["connector.duration_ms"] = durationMs.ToString("F2");
    }

    private static bool TryAssertResponseOutput(
        IReadOnlyDictionary<string, string> parameters,
        string responseOutput,
        out string error)
    {
        error = string.Empty;
        var responsePath = WorkflowParameterValueParser.GetString(
            parameters,
            string.Empty,
            "assert_response_path");
        if (string.IsNullOrWhiteSpace(responsePath))
            return true;

        if (string.IsNullOrWhiteSpace(responseOutput))
        {
            error = $"connector_call assertion failed: response path '{responsePath}' is missing";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseOutput);
            if (!TryResolveJsonPath(document.RootElement, responsePath, out var value))
            {
                error = $"connector_call assertion failed: response path '{responsePath}' is missing";
                return false;
            }

            if (!IsTruthy(value))
            {
                error = $"connector_call assertion failed: response path '{responsePath}' was not truthy";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = $"connector_call assertion failed: response output is not valid JSON for path '{responsePath}'";
            return false;
        }
    }

    private static bool TryResolveJsonPath(JsonElement current, string path, out JsonElement value)
    {
        var normalizedSegments = path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (normalizedSegments.Length == 0)
        {
            value = current;
            return true;
        }

        foreach (var segment in normalizedSegments)
        {
            if (current.ValueKind == JsonValueKind.Object &&
                current.TryGetProperty(segment, out var property))
            {
                current = property;
                continue;
            }

            if (current.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, out var index) &&
                index >= 0 &&
                index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }

            value = default;
            return false;
        }

        value = current;
        return true;
    }

    private static bool IsTruthy(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => !string.Equals(value.GetRawText(), "0", StringComparison.Ordinal),
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()) &&
                                    !string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Null => false,
            JsonValueKind.Undefined => false,
            _ => true,
        };
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

    // Refactor (issue1422/phase9-first-slice):
    //   Old pattern: Connector execution rewrapped typed runtime authorization into Metadata.
    //   New principle: Connector authorization crosses the execution boundary as a typed request field.
    private static string ExtractConnectorHttpAuthorization(
        IWorkflowExecutionContext ctx)
    {
        if (ConnectorAuthorizationRuntimeContextAccess.TryGetAuthorization(ctx, out var authorization) &&
            !string.IsNullOrWhiteSpace(authorization))
        {
            return authorization.Trim();
        }

        return string.Empty;
    }
}
