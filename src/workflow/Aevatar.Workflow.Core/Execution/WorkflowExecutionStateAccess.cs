using Google.Protobuf;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowExecutionStateAccess
{
    public static string GetRunId(IWorkflowExecutionContext ctx) =>
        ctx.RunId;

    public static TState Load<TState>(IWorkflowExecutionContext ctx, string scopeKey)
        where TState : class, IMessage<TState>, new() =>
        ctx.LoadState<TState>(scopeKey);

    public static IReadOnlyList<KeyValuePair<string, TState>> LoadMany<TState>(
        IWorkflowExecutionContext ctx,
        string scopeKeyPrefix = "")
        where TState : class, IMessage<TState>, new() =>
        ctx.LoadStates<TState>(scopeKeyPrefix);

    public static Task SaveAsync<TState>(
        IWorkflowExecutionContext ctx,
        string scopeKey,
        TState state,
        CancellationToken ct)
        where TState : class, IMessage<TState> =>
        ctx.SaveStateAsync(scopeKey, state, ct);

    public static Task ClearAsync(
        IWorkflowExecutionContext ctx,
        string scopeKey,
        CancellationToken ct) =>
        ctx.ClearStateAsync(scopeKey, ct);
}
