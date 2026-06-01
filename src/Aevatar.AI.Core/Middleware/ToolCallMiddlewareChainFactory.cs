using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Hooks;

namespace Aevatar.AI.Core.Middleware;

public static class ToolCallMiddlewareChainFactory
{
    public static IReadOnlyList<IToolCallMiddleware> ForAgentRuntime(
        IEnumerable<IToolCallMiddleware> toolMiddlewares,
        IToolApprovalHandler? approvalHandler,
        AgentHookPipeline? hooks)
    {
        ArgumentNullException.ThrowIfNull(toolMiddlewares);

        return Build(
            toolMiddlewares,
            approvalHandler,
            hooks,
            failClosedWhenMissingApprovalHandler: false);
    }

    public static IReadOnlyList<IToolCallMiddleware> ForPort(
        IEnumerable<IToolCallMiddleware> toolMiddlewares,
        IToolApprovalHandler? approvalHandler,
        AgentHookPipeline? hooks)
    {
        ArgumentNullException.ThrowIfNull(toolMiddlewares);

        return Build(
            toolMiddlewares,
            approvalHandler,
            hooks,
            failClosedWhenMissingApprovalHandler: true);
    }

    private static IReadOnlyList<IToolCallMiddleware> Build(
        IEnumerable<IToolCallMiddleware> toolMiddlewares,
        IToolApprovalHandler? approvalHandler,
        AgentHookPipeline? hooks,
        bool failClosedWhenMissingApprovalHandler)
    {
        var effectiveToolMiddlewares = new List<IToolCallMiddleware>();
        var effectiveApprovalHandler = approvalHandler
                                       ?? (failClosedWhenMissingApprovalHandler
                                           ? MissingApprovalHandler.Instance
                                           : null);
        if (effectiveApprovalHandler != null)
            effectiveToolMiddlewares.Add(new ToolApprovalMiddleware(effectiveApprovalHandler, hooks));
        effectiveToolMiddlewares.AddRange(toolMiddlewares);
        return effectiveToolMiddlewares;
    }

    private sealed class MissingApprovalHandler : IToolApprovalHandler
    {
        public static readonly MissingApprovalHandler Instance = new();

        public Task<ToolApprovalResult> RequestApprovalAsync(
            ToolApprovalRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ToolApprovalResult.Denied("approval handler is not configured"));
        }
    }
}
