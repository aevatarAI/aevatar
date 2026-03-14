namespace Aevatar.Workflow.Core.Execution;

internal interface IWorkflowExecutionItemsContext
{
    bool TryGetItem<TItem>(
        string itemKey,
        out TItem? value);

    void SetItem(
        string itemKey,
        object? value);

    bool RemoveItem(string itemKey);
}
