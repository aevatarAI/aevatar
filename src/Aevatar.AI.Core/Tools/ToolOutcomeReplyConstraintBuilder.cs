using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

internal static class ToolOutcomeReplyConstraintBuilder
{
    internal const string ReceiptGroundingConstraintText =
        "System constraint: Every claim that an external action completed must be grounded in a successful mutating tool receipt from this turn whose tool, side effect, and subject match that exact action. " +
        "A success receipt for a different action, including a probe or workflow run, does not prove the requested business action. ";

    internal const string NoSuccessfulMutationConstraintText =
        ReceiptGroundingConstraintText +
        "In this turn, no successful mutating tool execution has been observed. " +
        "You may still call an appropriate tool to fulfill the request. " +
        "Do not claim that a definition, format, configuration, publication, schedule, registration, file, or external service was changed, updated, saved, applied, published, created, deleted, or otherwise mutated. " +
        "Read-only operations, status checks, searches, observation, trigger/rerun requests, failed tool calls, denied approvals, and pending approvals are not successful mutations. " +
        "If you answer without first observing a successful mutating tool receipt, state only what was observed and that the mutation has not been confirmed.";

    internal static IReadOnlyList<ChatMessage> BuildMutationClaimConstraints(
        IReadOnlyList<ToolOutcomeReplyFact>? toolOutcomes,
        IReadOnlyList<AgentToolReceipt>? toolReceipts)
    {
        if (HasSuccessfulMutatingToolOutcome(toolOutcomes) ||
            HasSuccessfulMutatingReceipt(toolReceipts))
        {
            return [ChatMessage.System(ReceiptGroundingConstraintText)];
        }

        return [ChatMessage.System(NoSuccessfulMutationConstraintText)];
    }

    internal static List<ChatMessage> ApplyConstraints(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatMessage> constraints,
        bool mergeIntoExistingSystem)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(constraints);
        if (constraints.Count == 0)
            return [.. messages];

        if (mergeIntoExistingSystem)
        {
            var constrained = messages.ToList();
            var systemIndex = constrained.FindIndex(static message =>
                string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase));
            if (systemIndex >= 0)
            {
                var system = constrained[systemIndex];
                constrained[systemIndex] = new ChatMessage
                {
                    Role = system.Role,
                    Content = $"{system.Content?.TrimEnd()}\n\n{constraints[0].Content}",
                    ReasoningContent = system.ReasoningContent,
                    ContentParts = system.ContentParts,
                    ToolCallId = system.ToolCallId,
                    ToolCalls = system.ToolCalls,
                    ToolResultView = system.ToolResultView,
                };
                return constrained;
            }
        }

        return [.. messages, .. constraints];
    }

    internal static bool IsMutatingTool(IAgentTool tool, string? argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var callSafety = tool.GetCallSafety(argumentsJson ?? string.Empty);
        return AgentToolReceiptEffectPolicy.FromCallSafety(callSafety, tool.SideEffectKind) ==
               AgentToolReceiptEffect.Mutating;
    }

    private static bool HasSuccessfulMutatingToolOutcome(IReadOnlyList<ToolOutcomeReplyFact>? toolOutcomes)
    {
        if (toolOutcomes is not { Count: > 0 })
            return false;

        return toolOutcomes.Any(static outcome =>
            outcome.Succeeded &&
            outcome.Receipt?.Status == AgentToolReceiptStatus.Success &&
            outcome.Receipt.Effect == AgentToolReceiptEffect.Mutating);
    }

    private static bool HasSuccessfulMutatingReceipt(IReadOnlyList<AgentToolReceipt>? toolReceipts)
    {
        if (toolReceipts is not { Count: > 0 })
            return false;

        return toolReceipts.Any(static receipt =>
            receipt.Status == AgentToolReceiptStatus.Success &&
            receipt.Effect == AgentToolReceiptEffect.Mutating);
    }
}

internal readonly record struct ToolOutcomeReplyFact(
    IAgentTool? Tool,
    string? ArgumentsJson,
    bool Succeeded,
    AgentToolReceipt? Receipt = null);
