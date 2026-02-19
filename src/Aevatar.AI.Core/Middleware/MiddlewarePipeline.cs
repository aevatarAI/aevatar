using Aevatar.AI.Abstractions.Middleware;

namespace Aevatar.AI.Core.Middleware;

/// <summary>
/// Builds and executes middleware chains for Agent Run, Tool Call, and LLM Call.
/// Follows the same pattern as ASP.NET Core middleware: each middleware calls next() to proceed.
/// </summary>
public static class MiddlewarePipeline
{
    /// <summary>Executes agent run middleware chain, then the core handler.</summary>
    public static Task RunAgentAsync(
        IReadOnlyList<IAgentRunMiddleware> middlewares,
        AgentRunContext context,
        Func<Task> coreHandler)
    {
        return Execute(middlewares, 0, context, coreHandler,
            static (mw, ctx, next) => mw.InvokeAsync(ctx, next));
    }

    /// <summary>Executes tool call middleware chain, then the core handler.</summary>
    public static Task RunToolCallAsync(
        IReadOnlyList<IToolCallMiddleware> middlewares,
        ToolCallContext context,
        Func<Task> coreHandler)
    {
        return Execute(middlewares, 0, context, coreHandler,
            static (mw, ctx, next) => mw.InvokeAsync(ctx, next));
    }

    /// <summary>Executes LLM call middleware chain, then the core handler.</summary>
    public static Task RunLLMCallAsync(
        IReadOnlyList<ILLMCallMiddleware> middlewares,
        LLMCallContext context,
        Func<Task> coreHandler)
    {
        return Execute(middlewares, 0, context, coreHandler,
            static (mw, ctx, next) => mw.InvokeAsync(ctx, next));
    }

    private static Task Execute<TMiddleware, TContext>(
        IReadOnlyList<TMiddleware> middlewares,
        int index,
        TContext context,
        Func<Task> coreHandler,
        Func<TMiddleware, TContext, Func<Task>, Task> invoker)
    {
        if (index >= middlewares.Count)
            return coreHandler();

        return invoker(middlewares[index], context, () => Execute(middlewares, index + 1, context, coreHandler, invoker));
    }
}
