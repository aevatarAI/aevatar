using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Execution;

// Refactor (iter16/cluster-031):
//   Old pattern: WorkflowRunGAgent kept Dictionary<string, object?> _executionItems
//                bag for request metadata, LLM overrides, authorization, secure values
//   New principle: typed non-durable actor-owned WorkflowExecutionRuntimeContext;
//                  no facts seam, no proto change
internal interface IWorkflowExecutionStateHost
{
    string RunId { get; }

    WorkflowExecutionRuntimeContext RuntimeContext { get; }

    Any? GetExecutionState(string scopeKey);

    IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates();

    Task UpsertExecutionStateAsync(
        string scopeKey,
        Any state,
        CancellationToken ct = default);

    Task ClearExecutionStateAsync(
        string scopeKey,
        CancellationToken ct = default);
}
