using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.Tools;

internal static class ToolOutcomeReplyConstraintBuilder
{
    internal const string ConstraintText =
        "System constraint: In this turn, no successful mutating tool execution has been observed. " +
        "Do not claim that a definition, format, configuration, publication, schedule, registration, file, or external service was changed, updated, saved, applied, published, created, deleted, or otherwise mutated. " +
        "Read-only operations, status checks, searches, observation, trigger/rerun requests, failed tool calls, denied approvals, and pending approvals are not successful mutations. " +
        "If the user asked for a mutation, state only what was observed and that the mutation has not been confirmed by a successful mutating tool receipt.";

    internal static IReadOnlyList<ChatMessage> BuildFinalNoToolsConstraints(
        IReadOnlyList<ToolOutcomeReplyFact>? toolOutcomes,
        IReadOnlyList<AgentToolReceipt>? toolReceipts)
    {
        if (HasSuccessfulMutatingToolOutcome(toolOutcomes) ||
            HasSuccessfulMutatingReceipt(toolReceipts))
        {
            return [];
        }

        return [ChatMessage.System(ConstraintText)];
    }

    internal static bool IsMutatingTool(IAgentTool tool, string? argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var callSafety = tool.GetCallSafety(argumentsJson ?? string.Empty);
        return !callSafety.IsReadOnly ||
               callSafety.IsDestructive ||
               !string.IsNullOrWhiteSpace(tool.SideEffectKind) ||
               callSafety.RequiresApproval == true;
    }

    private static bool HasSuccessfulMutatingToolOutcome(IReadOnlyList<ToolOutcomeReplyFact>? toolOutcomes)
    {
        if (toolOutcomes is not { Count: > 0 })
            return false;

        return toolOutcomes.Any(static outcome =>
            outcome.Succeeded &&
            outcome.Receipt?.Status == AgentToolReceiptStatus.Success &&
            outcome.Tool is not null &&
            IsMutatingTool(outcome.Tool, outcome.ArgumentsJson));
    }

    private static bool HasSuccessfulMutatingReceipt(IReadOnlyList<AgentToolReceipt>? toolReceipts)
    {
        if (toolReceipts is not { Count: > 0 })
            return false;

        return toolReceipts.Any(static receipt =>
            receipt.Status == AgentToolReceiptStatus.Success &&
            (receipt.IsDestructive || !string.IsNullOrWhiteSpace(receipt.SideEffectKind)));
    }
}

internal readonly record struct ToolOutcomeReplyFact(
    IAgentTool? Tool,
    string? ArgumentsJson,
    bool Succeeded,
    AgentToolReceipt? Receipt = null);
