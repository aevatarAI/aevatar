using Aevatar.AI.Abstractions.Middleware;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Middleware;

namespace Aevatar.AI.Core.Tools;

public sealed class AgentToolExecutionPort : IAgentToolExecutionPort
{
    private readonly IReadOnlyList<IToolCallMiddleware> _toolMiddlewares;

    public AgentToolExecutionPort(IReadOnlyList<IToolCallMiddleware> toolMiddlewares)
    {
        _toolMiddlewares = toolMiddlewares ?? throw new ArgumentNullException(nameof(toolMiddlewares));
    }

    public AgentToolExecutionPort(IEnumerable<IToolCallMiddleware> toolMiddlewares)
        : this(toolMiddlewares?.ToArray() ?? throw new ArgumentNullException(nameof(toolMiddlewares)))
    {
    }

    public async Task<AgentToolExecutionResult> ExecuteAsync(
        AgentToolExecutionRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var context = new ToolCallContext
            {
                Tool = request.Tool,
                ToolName = request.ToolName,
                ToolCallId = request.ToolCallId,
                ArgumentsJson = request.ArgumentsJson,
                CancellationToken = ct,
            };

            await MiddlewarePipeline.RunToolCallAsync(_toolMiddlewares, context, async () =>
            {
                if (context.Terminate)
                    return;

                context.Result = await context.Tool.ExecuteAsync(context.ArgumentsJson, ct);
            });

            if (context.Terminate)
            {
                return new AgentToolExecutionResult(
                    MapTerminationStatus(context.TerminationKind),
                    context.Result,
                    ResolveTerminationError(context));
            }

            if (context.Result == null)
                return AgentToolExecutionResult.Failed($"Tool '{context.ToolName}' returned no result.");

            return AgentToolExecutionResult.Succeeded(context.Result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AgentToolExecutionResult.Failed(ex.Message);
        }
    }

    private static AgentToolExecutionStatus MapTerminationStatus(ToolCallTerminationKind kind) =>
        kind switch
        {
            ToolCallTerminationKind.ApprovalDenied => AgentToolExecutionStatus.ApprovalDenied,
            ToolCallTerminationKind.ApprovalTimedOut => AgentToolExecutionStatus.ApprovalTimedOut,
            ToolCallTerminationKind.ApprovalPending => AgentToolExecutionStatus.ApprovalPending,
            _ => AgentToolExecutionStatus.MiddlewareTerminated,
        };

    private static string? ResolveTerminationError(ToolCallContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.TerminationReason))
            return context.TerminationReason;

        if (context.TerminationKind == ToolCallTerminationKind.None)
            return "Tool execution was terminated by middleware.";

        return context.Result;
    }
}
