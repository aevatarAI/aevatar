using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Core.Execution;

internal static class WorkflowToolExecutionContextAccess
{
    public static void ApplyFromCommand(
        WorkflowExecutionRuntimeContext runtimeContext,
        AgentToolExecutionContextPayload? payload,
        string? commandId,
        string? scopeId,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(runtimeContext);

        runtimeContext.ToolContext = payload == null
            ? null
            : NormalizeCommandContext(
                AgentToolExecutionContextMapper.FromPayload(payload),
                commandId,
                scopeId,
                sessionId);
    }

    public static AgentToolExecutionContext? GetForStep(
        IWorkflowExecutionContext executionContext,
        string? stepId)
    {
        ArgumentNullException.ThrowIfNull(executionContext);

        return executionContext is IWorkflowExecutionRuntimeContextAccessor accessor
            ? accessor.RuntimeContext.ToolContext?.WithCallId(stepId)
            : null;
    }

    private static AgentToolExecutionContext NormalizeCommandContext(
        AgentToolExecutionContext context,
        string? commandId,
        string? scopeId,
        string? sessionId)
    {
        var normalizedCommandId = Normalize(commandId);
        var normalizedScopeId = Normalize(scopeId) ?? context.Caller.ScopeId;
        var normalizedSessionId = Normalize(sessionId);

        return context with
        {
            Request = context.Request with
            {
                RequestId = context.Request.RequestId ?? normalizedCommandId,
            },
            Caller = context.Caller with
            {
                ScopeId = normalizedScopeId,
                ResponseId = context.Caller.ResponseId ?? normalizedSessionId ?? normalizedCommandId,
            },
        };
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
