using Aevatar.Workflow.Abstractions.Security;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Records the sanitized, size-bounded transcript facts for a turn's Model and
/// Tool operations, and snapshots them when the turn reaches its terminal.
/// </summary>
/// <remarks>
/// The live trajectory is assembled from the child executor's progress frames,
/// which are gone once the browser reloads. These facts are the durable copy the
/// chat transcript carries, so a reopened conversation renders the same ledger
/// without replaying events. Only Model and Tool steps are recorded: they are
/// exactly the operations the trajectory ledger models. Timing, usage and
/// content are copied only when the operation reported them.
///
/// Tool result bodies are deliberately excluded. They are untrusted external
/// text, and conversation actor state is re-read when rebuilding model input, so
/// copying a tool result here would let it re-enter the model as instructions.
/// Only model output and the arguments our own model authored are retained.
/// </remarks>
internal static class NyxIdChatOperationLedger
{
    /// <summary>Per-preview storage ceiling. Previews are fragments, not payloads.</summary>
    internal const int PreviewMaxUtf8Bytes = 2 * 1024;

    /// <summary>
    /// Stamps the committed result of one operation onto its owning step.
    /// </summary>
    /// <param name="state">Post-transition conversation state to annotate in place.</param>
    /// <param name="signal">The child-owned result being reconciled.</param>
    public static void RecordResult(
        NyxIdChatConversationGAgentState state,
        NyxIdChatOperationResultSignal signal)
    {
        var task = state.ActiveTask;
        if (task is null || signal.Key is null)
            return;

        var step = FindStep(task, signal.Key.StepId);
        if (step is null || !IsLedgerStep(step.Kind))
            return;

        var facts = step.OperationLedgerFacts ?? new NyxIdChatOperationLedgerFacts();
        var truncated = facts.PreviewsTruncated;

        switch (signal.ResultCase)
        {
            case NyxIdChatOperationResultSignal.ResultOneofCase.Llm:
                RecordLlmResult(facts, signal.Llm, step, ref truncated);
                RecordPlannedToolArguments(task, signal.Llm);
                break;
            case NyxIdChatOperationResultSignal.ResultOneofCase.Tool:
                // The receipt's own status is an actor-owned fact; the result body
                // is not retained. See the prompt-injection note above.
                return;
            case NyxIdChatOperationResultSignal.ResultOneofCase.Failure:
                // Failure codes and safe messages are actor-authored, not tool output.
                facts.OutputPreview = Preview(signal.Failure.SafeMessage, ref truncated);
                break;
            default:
                return;
        }

        facts.PreviewsTruncated = truncated;
        step.OperationLedgerFacts = facts;
    }

    /// <summary>
    /// Snapshots the operation ledger of the turn that owns <paramref name="turnId"/>.
    /// </summary>
    /// <param name="state">Conversation state holding the terminal turn's task.</param>
    /// <param name="turnId">Turn whose operations are being appended to history.</param>
    /// <returns>Ordered snapshots, empty when the turn owns no recorded operation.</returns>
    public static IReadOnlyList<NyxIdChatTurnOperationSnapshot> SnapshotTurn(
        NyxIdChatConversationGAgentState state,
        string turnId)
    {
        var task = state.ActiveTask;
        if (task is null ||
            string.IsNullOrWhiteSpace(turnId) ||
            !string.Equals(task.TurnId, turnId, StringComparison.Ordinal))
        {
            return [];
        }

        return task.Steps
            .Where(step => IsLedgerStep(step.Kind) && step.Operation?.RequestedAt is not null)
            .OrderBy(step => step.Order)
            .ThenBy(step => step.StepId, StringComparer.Ordinal)
            .Select(ToSnapshot)
            .ToList();
    }

    private static void RecordLlmResult(
        NyxIdChatOperationLedgerFacts facts,
        NyxIdChatLLMOperationResult llm,
        NyxIdChatTaskStepState step,
        ref bool truncated)
    {
        facts.OutputPreview = Preview(llm.Content, ref truncated);
        facts.FinishReason = WorkflowAuditTextSanitizer.SanitizeForDisplay(llm.FinishReason, 64);
        if (llm.Usage is not null)
            facts.Usage = llm.Usage.Clone();
        facts.ToolCatalogCaptured = llm.ToolCatalogCaptured;
        facts.AvailableToolNames.Clear();
        facts.AvailableToolNames.AddRange(llm.AvailableToolNames
            .Select(static name => name?.Trim() ?? string.Empty)
            .Where(static name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal));
        var model = step.Source?.Llm?.Model;
        if (!string.IsNullOrWhiteSpace(model))
            facts.Model = model;
    }

    /// <summary>
    /// Copies the arguments the model authored onto the tool step this reply planned.
    /// The reply and the tool call commit together, so the planned step is already
    /// present in the post-transition state.
    /// </summary>
    private static void RecordPlannedToolArguments(
        NyxIdChatTaskState task,
        NyxIdChatLLMOperationResult llm)
    {
        foreach (var toolCall in llm.ToolCalls)
        {
            var planned = task.Steps.LastOrDefault(step =>
                step.Kind == NyxIdChatStepKind.Tool &&
                step.OperationLedgerFacts is null &&
                string.Equals(step.Source?.Tool?.ToolName, toolCall.ToolName, StringComparison.Ordinal));
            if (planned is null)
                continue;

            var truncated = false;
            planned.OperationLedgerFacts = new NyxIdChatOperationLedgerFacts
            {
                ArgumentsPreview = Preview(toolCall.ArgumentsJson, ref truncated),
                PreviewsTruncated = truncated,
            };
        }
    }

    private static NyxIdChatTurnOperationSnapshot ToSnapshot(NyxIdChatTaskStepState step)
    {
        var operation = step.Operation!;
        var snapshot = new NyxIdChatTurnOperationSnapshot
        {
            OperationId = operation.Key?.OperationId ?? string.Empty,
            StepId = step.StepId,
            Order = step.Order,
            Kind = step.Kind,
            Status = step.Status,
            Title = ResolveTitle(step),
            Description = WorkflowAuditTextSanitizer.SanitizeForDisplay(step.Description, 200),
            TerminalCode = operation.TerminalCode,
            SafeMessage = WorkflowAuditTextSanitizer.SanitizeForDisplay(step.SafeMessage, 300),
        };
        if (operation.RequestedAt is not null)
            snapshot.StartedAt = operation.RequestedAt.Clone();
        if (operation.CompletedAt is not null)
            snapshot.CompletedAt = operation.CompletedAt.Clone();
        if (step.OperationLedgerFacts is not null)
            snapshot.LedgerFacts = step.OperationLedgerFacts.Clone();
        return snapshot;
    }

    private static string ResolveTitle(NyxIdChatTaskStepState step) =>
        step.Kind == NyxIdChatStepKind.Tool
            ? step.Source?.Tool?.ToolName ?? string.Empty
            : step.Source?.Llm?.Model ?? step.OperationLedgerFacts?.Model ?? string.Empty;

    private static NyxIdChatTaskStepState? FindStep(NyxIdChatTaskState task, string stepId) =>
        string.IsNullOrWhiteSpace(stepId)
            ? null
            : task.Steps.FirstOrDefault(step =>
                string.Equals(step.StepId, stepId, StringComparison.Ordinal));

    private static bool IsLedgerStep(NyxIdChatStepKind kind) =>
        kind is NyxIdChatStepKind.Llm or NyxIdChatStepKind.Tool;

    private static string Preview(string? value, ref bool truncated)
    {
        var preview = WorkflowAuditTextSanitizer.SanitizeForStorage(
            value,
            PreviewMaxUtf8Bytes,
            out var previewTruncated);
        truncated |= previewTruncated;
        return preview;
    }
}
