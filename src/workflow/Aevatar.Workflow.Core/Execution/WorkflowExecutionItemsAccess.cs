using Aevatar.Workflow.Abstractions.Execution;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowExecutionItemsAccess
{
    public static bool TryGetItem<TItem>(
        IWorkflowExecutionContext ctx,
        string itemKey,
        out TItem? value)
    {
        if (ctx is IWorkflowExecutionItemsContext itemsContext)
            return itemsContext.TryGetItem(itemKey, out value);

        value = default;
        return false;
    }

    public static void SetItem(
        IWorkflowExecutionContext ctx,
        string itemKey,
        object? value)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);

        if (ctx is not IWorkflowExecutionItemsContext itemsContext)
        {
            throw new InvalidOperationException(
                $"Workflow execution context `{ctx.GetType().FullName}` does not support actor-local items.");
        }

        itemsContext.SetItem(itemKey, value);
    }

    public static bool RemoveItem(
        IWorkflowExecutionContext ctx,
        string itemKey)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKey);

        return ctx is IWorkflowExecutionItemsContext itemsContext &&
               itemsContext.RemoveItem(itemKey);
    }
}
