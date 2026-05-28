namespace Aevatar.Workflow.Core.Modules;

internal interface IWorkflowStepIoExecutor
{
    // Refactor (iter110/cluster-1): Old pattern: tool_call modules executed external tool IO during actor handling.  New principle: a bounded executor consumes the typed tool intent and returns a tool-specific continuation result.
    Task<ToolCallContinuationResultEvent> ExecuteToolCallAsync(
        ToolCallIntentEvent intent,
        CancellationToken ct = default);

    // Refactor (iter110/cluster-1): Old pattern: connector_call modules executed external connector IO during actor handling.  New principle: a bounded executor consumes the typed connector intent and returns a connector-specific continuation result.
    Task<ConnectorCallContinuationResultEvent> ExecuteConnectorCallAsync(
        ConnectorCallIntentEvent intent,
        CancellationToken ct = default);
}
