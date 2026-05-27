using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Execution;

// Refactor (iter129/cluster-triage-workflow-llm-nyx-coupling): Old pattern: core modules built AI provider requests directly. New principle: core emits a workflow-owned LLM intent and adapters translate it at the integration boundary.
internal static class WorkflowLlmIntentBuilder
{
    public static WorkflowLlmExecutionIntent Build(
        string prompt,
        string sessionId,
        string runId,
        string stepId,
        string? targetRole,
        RoleDefinition? role,
        int timeoutMs,
        IWorkflowExecutionContext ctx,
        IEnumerable<WorkflowChatContentPart>? inputParts = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var intent = new WorkflowLlmExecutionIntent
        {
            Prompt = prompt ?? string.Empty,
            SessionId = sessionId ?? string.Empty,
            RunId = WorkflowRunIdNormalizer.Normalize(runId),
            StepId = stepId ?? string.Empty,
            TargetRole = targetRole ?? role?.Id ?? string.Empty,
            ProviderName = role?.Provider ?? string.Empty,
            Model = role?.Model ?? string.Empty,
            SystemPrompt = role?.SystemPrompt ?? string.Empty,
            MaxTokens = role?.MaxTokens ?? 0,
            MaxToolRounds = role?.MaxToolRounds ?? 0,
            MaxHistoryMessages = role?.MaxHistoryMessages ?? 0,
            TimeoutMs = timeoutMs,
        };

        if (role?.Temperature.HasValue == true)
            intent.Temperature = role.Temperature.Value;

        if (inputParts != null)
            intent.InputParts.Add(inputParts);

        if (WorkflowRunExecutionContextStateAccess.TryGetLlm(ctx, out var llm))
        {
            intent.ModelOverride = llm.ModelOverride ?? string.Empty;
            if (llm.HasMaxToolRoundsOverride)
                intent.MaxToolRoundsOverride = llm.MaxToolRoundsOverride;
            intent.UserMemoryPrompt = llm.UserMemoryPrompt ?? string.Empty;
        }

        WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(ctx, intent.Annotations);
        return intent;
    }
}
