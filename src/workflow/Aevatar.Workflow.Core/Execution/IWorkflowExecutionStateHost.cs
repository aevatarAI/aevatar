using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Execution;

internal interface IWorkflowExecutionStateHost
{
    string RunId { get; }

    Any? GetExecutionState(string scopeKey);

    IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates();

    bool TryGetExecutionItem(
        string itemKey,
        out object? value)
    {
        value = null;
        return false;
    }

    void SetExecutionItem(
        string itemKey,
        object? value)
    {
        _ = itemKey;
        _ = value;
    }

    bool RemoveExecutionItem(string itemKey)
    {
        _ = itemKey;
        return false;
    }

    Task UpsertExecutionStateAsync(
        string scopeKey,
        Any state,
        CancellationToken ct = default);

    Task ClearExecutionStateAsync(
        string scopeKey,
        CancellationToken ct = default);
}
