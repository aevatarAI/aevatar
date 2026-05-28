using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Modules;

internal sealed class WorkflowStepIoExecutor : IWorkflowStepIoExecutor
{
    private readonly IServiceProvider _services;
    private readonly IWorkflowConnectorResolver _connectorResolver;
    private readonly ILogger<WorkflowStepIoExecutor> _logger;
    private volatile Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? _toolIndex;

    // Refactor (iter110/cluster-1): Old pattern: connector/tool modules owned concrete external IO dependencies.  New principle: one internal bounded executor owns connector/tool execution while public messages stay connector-specific.
    public WorkflowStepIoExecutor(
        IServiceProvider services,
        IWorkflowConnectorResolver connectorResolver,
        ILogger<WorkflowStepIoExecutor>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _connectorResolver = connectorResolver ?? throw new ArgumentNullException(nameof(connectorResolver));
        _logger = logger ?? NullLogger<WorkflowStepIoExecutor>.Instance;
    }

    // Refactor (iter110/cluster-1): Old pattern: tool_call module discovered and executed tools before actor turn completed.  New principle: executor resolves the typed tool intent and returns a typed continuation result.
    public async Task<ToolCallContinuationResultEvent> ExecuteToolCallAsync(
        ToolCallIntentEvent intent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        try
        {
            var toolIndex = await GetOrDiscoverToolsAsync(ct);
            if (!toolIndex.TryGetValue(intent.ToolName, out var tool))
            {
                return new ToolCallContinuationResultEvent
                {
                    StepId = intent.StepId,
                    RunId = intent.RunId,
                    ExecutionId = intent.ExecutionId,
                    ToolName = intent.ToolName,
                    Success = false,
                    Error = $"tool '{intent.ToolName}' execution failed: tool not found or no tool sources configured",
                };
            }

            var result = await tool.ExecuteAsync(intent.ArgumentsJson, ct);
            return new ToolCallContinuationResultEvent
            {
                StepId = intent.StepId,
                RunId = intent.RunId,
                ExecutionId = intent.ExecutionId,
                ToolName = intent.ToolName,
                Success = true,
                ResultJson = result,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ToolCall: step={StepId} tool={Tool} execution failed",
                intent.StepId,
                intent.ToolName);
            return new ToolCallContinuationResultEvent
            {
                StepId = intent.StepId,
                RunId = intent.RunId,
                ExecutionId = intent.ExecutionId,
                ToolName = intent.ToolName,
                Success = false,
                Error = $"tool '{intent.ToolName}' execution failed: {ex.Message}",
            };
        }
    }

    // Refactor (iter110/cluster-1): Old pattern: connector_call module resolved connector, retried, timed out, and mapped output inline.  New principle: executor resolves connector-call intent and returns a connector-specific typed continuation result.
    public async Task<ConnectorCallContinuationResultEvent> ExecuteConnectorCallAsync(
        ConnectorCallIntentEvent intent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var connector = await _connectorResolver.ResolveAsync(
            new ConnectorIntentExecutionContext(intent),
            intent.ConnectorName,
            ct);
        if (connector == null)
        {
            if (intent.Optional || string.Equals(intent.OnMissing, "skip", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "ConnectorCall: step={StepId} connector={Connector} not found, skip",
                    intent.StepId,
                    intent.ConnectorName);
                return ConnectorSkipped(intent, "connector_not_found");
            }

            return ConnectorFailure(intent, $"connector '{intent.ConnectorName}' not found");
        }

        var startedAt = TimeProvider.System.GetTimestamp();
        var request = new ConnectorRequest
        {
            Metadata = intent.Headers.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            RunId = intent.ConnectorRequestRunId,
            StepId = intent.StepId,
            Connector = intent.ConnectorName,
            Operation = intent.Operation,
            Payload = intent.Payload ?? string.Empty,
            Parameters = intent.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value),
        };

        var outcome = await WorkflowStepIoRetryHelper.ExecuteConnectorAsync(
            connector,
            request,
            intent.RetryCount,
            intent.TimeoutMs,
            (exception, attempt, attempts, error) =>
            {
                if (exception == null)
                {
                    _logger.LogWarning(
                        "ConnectorCall: step={StepId} connector={Connector} attempt={Attempt}/{Attempts} failed: {Error}",
                        intent.StepId,
                        intent.ConnectorName,
                        attempt,
                        attempts,
                        error);
                    return;
                }

                _logger.LogWarning(
                    exception,
                    "ConnectorCall: step={StepId} connector={Connector} attempt={Attempt}/{Attempts} exception",
                    intent.StepId,
                    intent.ConnectorName,
                    attempt,
                    attempts);
            },
            ct);

        var durationMs = TimeProvider.System.GetElapsedTime(startedAt).TotalMilliseconds;
        return MapConnectorOutcome(intent, connector, outcome, durationMs);
    }

    private Task<IReadOnlyDictionary<string, IAgentTool>> GetOrDiscoverToolsAsync(CancellationToken ct)
    {
        while (true)
        {
            var current = _toolIndex;
            if (TryGetReusableTask(current, out var cached))
                return cached;

            // Refactor (iter110/cluster-1): Old pattern: tool discovery cache lived in ToolCallModule beside actor handling.  New principle: executor owns discovery caching because it owns external tool IO.
            var candidate = new Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>(
                () => DiscoverAllToolsAsync(_services.GetServices<IAgentToolSource>(), _logger, ct),
                LazyThreadSafetyMode.ExecutionAndPublication);
            var winner = Interlocked.CompareExchange(ref _toolIndex, candidate, current);
            if (ReferenceEquals(winner, current))
                return candidate.Value;
        }
    }

    private static bool TryGetReusableTask(
        Lazy<Task<IReadOnlyDictionary<string, IAgentTool>>>? current,
        out Task<IReadOnlyDictionary<string, IAgentTool>> task)
    {
        task = null!;
        if (current == null)
            return false;

        if (!current.IsValueCreated)
        {
            task = current.Value;
            return true;
        }

        var existing = current.Value;
        if (existing.IsFaulted || existing.IsCanceled)
            return false;

        task = existing;
        return true;
    }

    private static async Task<IReadOnlyDictionary<string, IAgentTool>> DiscoverAllToolsAsync(
        IEnumerable<IAgentToolSource> toolSources,
        ILogger logger,
        CancellationToken ct)
    {
        var index = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in toolSources)
        {
            IReadOnlyList<IAgentTool> tools;
            try
            {
                tools = await source.DiscoverToolsAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tool source discovery failed: {Source}", source.GetType().Name);
                continue;
            }

            foreach (var tool in tools)
                index[tool.Name] = tool;
        }

        return index;
    }

    private static ConnectorCallContinuationResultEvent MapConnectorOutcome(
        ConnectorCallIntentEvent intent,
        IConnector connector,
        ConnectorCallExecutionOutcome outcome,
        double durationMs)
    {
        if (outcome.Response is { Success: true })
        {
            var resolvedOutput = outcome.Response.Output ?? string.Empty;
            if (!TryAssertResponseOutput(intent.Parameters, resolvedOutput, out var assertionError))
            {
                return ConnectorFailure(intent, assertionError, connector, outcome.Attempts, durationMs);
            }

            if (ParseBool(intent.Parameters.GetValueOrDefault("pass_through_input", "false")))
                resolvedOutput = intent.Input ?? string.Empty;

            var ok = new ConnectorCallContinuationResultEvent
            {
                StepId = intent.StepId,
                RunId = intent.RunId,
                ExecutionId = intent.ExecutionId,
                ConnectorName = intent.ConnectorName,
                ConnectorType = connector.Type,
                Operation = intent.Operation,
                Success = true,
                Output = resolvedOutput,
                Attempts = outcome.Attempts,
                TimeoutMs = intent.TimeoutMs,
                DurationMs = durationMs,
            };
            foreach (var (key, value) in outcome.Response.Metadata)
                ok.Annotations[key] = value;
            return ok;
        }

        var errorText = outcome.Response?.Error;
        if (string.IsNullOrWhiteSpace(errorText))
            errorText = outcome.LastError?.Message ?? "connector call failed";

        if (string.Equals(intent.OnError, "continue", StringComparison.OrdinalIgnoreCase))
        {
            var continued = new ConnectorCallContinuationResultEvent
            {
                StepId = intent.StepId,
                RunId = intent.RunId,
                ExecutionId = intent.ExecutionId,
                ConnectorName = intent.ConnectorName,
                ConnectorType = connector.Type,
                Operation = intent.Operation,
                Success = true,
                Output = intent.Input ?? string.Empty,
                Attempts = outcome.Attempts,
                TimeoutMs = intent.TimeoutMs,
                DurationMs = durationMs,
            };
            continued.Annotations["connector.continued_on_error"] = "true";
            continued.Annotations["connector.error"] = errorText ?? string.Empty;
            return continued;
        }

        return ConnectorFailure(intent, errorText ?? "connector call failed", connector, outcome.Attempts, durationMs);
    }

    private static ConnectorCallContinuationResultEvent ConnectorFailure(
        ConnectorCallIntentEvent intent,
        string error,
        IConnector? connector = null,
        int attempts = 0,
        double durationMs = 0)
    {
        return new ConnectorCallContinuationResultEvent
        {
            StepId = intent.StepId,
            RunId = intent.RunId,
            ExecutionId = intent.ExecutionId,
            ConnectorName = intent.ConnectorName,
            ConnectorType = connector?.Type ?? string.Empty,
            Operation = intent.Operation,
            Success = false,
            Error = error,
            Attempts = attempts,
            TimeoutMs = intent.TimeoutMs,
            DurationMs = durationMs,
        };
    }

    private static ConnectorCallContinuationResultEvent ConnectorSkipped(
        ConnectorCallIntentEvent intent,
        string reason)
    {
        var result = new ConnectorCallContinuationResultEvent
        {
            StepId = intent.StepId,
            RunId = intent.RunId,
            ExecutionId = intent.ExecutionId,
            ConnectorName = intent.ConnectorName,
            Operation = intent.Operation,
            Success = true,
            Output = intent.Input ?? string.Empty,
            Attempts = 0,
            TimeoutMs = intent.TimeoutMs,
        };
        result.Annotations["connector.skipped"] = "true";
        result.Annotations["connector.skip_reason"] = reason;
        return result;
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

    private static bool ParseBool(string raw) =>
        string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
}
