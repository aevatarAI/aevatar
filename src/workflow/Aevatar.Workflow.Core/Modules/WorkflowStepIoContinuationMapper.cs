using Aevatar.Foundation.Abstractions.Connectors;

namespace Aevatar.Workflow.Core.Modules;

internal static class WorkflowStepIoContinuationMapper
{
    // Refactor (iter110/cluster-1): Old pattern: modules duplicated IO error-to-StepCompletedEvent mapping around connector/tool calls.  New principle: shared mapping is internal and connector/tool-specific messages stay the public/core contract.
    public static StepCompletedEvent FromToolResult(ToolCallContinuationResultEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new StepCompletedEvent
        {
            StepId = result.StepId,
            RunId = result.RunId,
            ExecutionId = result.ExecutionId,
            Success = result.Success,
            Output = result.Success ? result.ResultJson : string.Empty,
            Error = result.Success ? string.Empty : result.Error,
        };
    }

    // Refactor (iter110/cluster-1): Old pattern: connector_call completion metadata was built where external IO ran.  New principle: actor reconciliation maps connector-specific continuation results to StepCompletedEvent in one internal helper.
    public static StepCompletedEvent FromConnectorResult(ConnectorCallContinuationResultEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var completed = new StepCompletedEvent
        {
            StepId = result.StepId,
            RunId = result.RunId,
            ExecutionId = result.ExecutionId,
            Success = result.Success,
            Output = result.Success ? result.Output : string.Empty,
            Error = result.Success ? string.Empty : result.Error,
        };

        AppendConnectorAnnotations(completed, result);
        foreach (var (key, value) in result.Annotations)
            completed.Annotations[key] = value;

        return completed;
    }

    // Refactor (iter110/cluster-1): Old pattern: retry/timeout annotations were assembled beside connector.ExecuteAsync.  New principle: connector continuation carries typed execution facts and this helper renders stable annotations.
    public static void AppendConnectorAnnotations(
        StepCompletedEvent evt,
        ConnectorCallContinuationResultEvent result)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(result);

        evt.Annotations["connector.name"] = result.ConnectorName;
        evt.Annotations["connector.type"] = result.ConnectorType;
        evt.Annotations["connector.operation"] = result.Operation;
        evt.Annotations["connector.attempts"] = result.Attempts.ToString();
        evt.Annotations["connector.timeout_ms"] = result.TimeoutMs.ToString();
        evt.Annotations["connector.duration_ms"] = result.DurationMs.ToString("F2");
    }

    // Refactor (iter110/cluster-1): Old pattern: connector failures were emitted directly as StepCompletedEvent.  New principle: connector validation emits connector-specific typed results, then actor reconciliation owns completion.
    public static ConnectorCallContinuationResultEvent ConnectorFailure(
        StepRequestEvent request,
        string connectorName,
        string operation,
        string error,
        int timeoutMs = 0,
        string connectorType = "")
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ConnectorCallContinuationResultEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            ConnectorName = connectorName,
            ConnectorType = connectorType,
            Operation = operation,
            Success = false,
            Error = error,
            Attempts = 0,
            TimeoutMs = timeoutMs,
        };
    }

    // Refactor (iter110/cluster-1): Old pattern: optional missing connectors bypassed continuation semantics.  New principle: connector-specific continuation represents skip/continue/failure outcomes uniformly.
    public static ConnectorCallContinuationResultEvent ConnectorSkipped(
        StepRequestEvent request,
        string connectorName,
        string operation,
        string reason,
        int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = new ConnectorCallContinuationResultEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            ConnectorName = connectorName,
            Operation = operation,
            Success = true,
            Output = request.Input ?? string.Empty,
            Attempts = 0,
            TimeoutMs = timeoutMs,
        };
        result.Annotations["connector.skipped"] = "true";
        result.Annotations["connector.skip_reason"] = reason;
        return result;
    }

    // Refactor (iter110/cluster-1): Old pattern: tool failures were emitted as StepCompletedEvent by the tool_call module.  New principle: tool_call validation emits a typed tool continuation result for actor reconciliation.
    public static ToolCallContinuationResultEvent ToolFailure(
        StepRequestEvent request,
        string toolName,
        string error)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errorMessage = $"tool '{toolName}' execution failed: {error}";
        return new ToolCallContinuationResultEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            ExecutionId = request.ExecutionId,
            ToolName = toolName,
            Success = false,
            Error = errorMessage,
        };
    }

    // Refactor (iter110/cluster-1): Old pattern: connector authorization lived in same-turn process context beside IO execution.  New principle: typed connector intent carries stable headers into the bounded executor.
    public static IReadOnlyDictionary<string, string> ExtractConnectorHeaders(IWorkflowExecutionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ConnectorAuthorizationRuntimeContextAccess.TryGetAuthorization(ctx, out var authorization) &&
            !string.IsNullOrWhiteSpace(authorization))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ConnectorRequest.HttpAuthorizationMetadataKey] = authorization.Trim(),
            };
        }

        return new Dictionary<string, string>();
    }
}
