using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Execution;

internal interface IWorkflowExecutionStateHost
{
    string RunId { get; }

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
