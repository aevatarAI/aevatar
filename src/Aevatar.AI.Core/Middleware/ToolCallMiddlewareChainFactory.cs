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

        return Build(toolMiddlewares, approvalHandler, hooks);
    }

    private static IReadOnlyList<IToolCallMiddleware> Build(
        IEnumerable<IToolCallMiddleware> toolMiddlewares,
        IToolApprovalHandler? approvalHandler,
        AgentHookPipeline? hooks)
    {
        var effectiveToolMiddlewares = new List<IToolCallMiddleware>();
        effectiveToolMiddlewares.Add(new ToolCallCredentialPolicyMiddleware());
        effectiveToolMiddlewares.Add(new ToolApprovalMiddleware(
            approvalHandler ?? MissingApprovalHandler.Instance,
            hooks));
        effectiveToolMiddlewares.AddRange(toolMiddlewares.Where(static middleware =>
            middleware is not ToolApprovalMiddleware and not ToolCallCredentialPolicyMiddleware));
        return effectiveToolMiddlewares;
    }
}
