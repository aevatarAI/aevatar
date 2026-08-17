using System.Text.Json;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Execution;

/// <summary>
/// Deterministic probe for a workflow's run-input contract, derived from the
/// compiled definition itself.
///
/// Field background (2026-08-13, aevatar#3432 family): chat agents starting
/// typed scope workflows repeatedly passed the user's natural-language
/// sentence — or an empty string — as the run input. Nothing validated the
/// input until the first bounded transform template threw
/// "transform template input must be valid bounded JSON" mid-run, which
/// surfaces as an opaque step failure long after admission. This probe lets
/// the run REJECT such inputs at start time with a corrective message the
/// calling agent can act on, without requiring workflow authors to declare
/// anything: the contract is read off the definition's own structure.
/// </summary>
public static class WorkflowRunInputContract
{
    private const int EntryChainProbeLimit = 4;

    /// <summary>
    /// True when the definition demonstrably consumes its run input as
    /// bounded JSON. Two independent, conservative signals:
    /// (1) an entry chain of assign steps that capture the raw input
    ///     (<c>value: "$input"</c>) feeding directly into a transform step;
    /// (2) any expression in the definition applying a <c>.json</c> accessor
    ///     to a step that captured the raw input.
    /// Workflows that feed their input to an LLM prompt or merely echo it
    /// match neither signal and keep their existing free-text semantics.
    /// </summary>
    public static bool RequiresJsonInput(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.Steps.Count == 0)
            return false;

        var captureStepIds = CollectRawInputCaptureStepIds(workflow);

        return HasEntryChainTransform(workflow, captureStepIds) ||
               HasJsonAccessorOnCapture(workflow.Steps, captureStepIds);
    }

    /// <summary>
    /// Mirrors the bounded renderer's acceptance: the input must be non-blank
    /// and parse as JSON. (The renderer parses the run input before any
    /// template evaluates; prose and empty strings both fail there.)
    /// </summary>
    public static bool IsBoundedJson(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        try
        {
            using var _ = JsonDocument.Parse(input);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Corrective, agent-actionable message. It intentionally describes how to
    /// rebuild the tool call rather than echoing the offending input.
    /// </summary>
    public static string BuildViolationMessage(string workflowName, string? input)
    {
        var received = string.IsNullOrWhiteSpace(input)
            ? "an empty input"
            : "input that is not valid JSON (for example natural-language prose)";
        return
            $"Workflow '{workflowName}' requires its run input to be a non-empty serialized JSON value " +
            $"(for example {{\"submit\": false}}), but received {received}. " +
            "Extract the business fields from the user's request, build the JSON object internally, " +
            "and call the workflow again with inputs.prompt set to that serialized JSON string. " +
            "Never pass an empty string, a raw user sentence, or an unserialized object.";
    }

    private static HashSet<string> CollectRawInputCaptureStepIds(WorkflowDefinition workflow)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        CollectRawInputCaptureStepIds(workflow.Steps, ids);
        return ids;
    }

    private static void CollectRawInputCaptureStepIds(IEnumerable<StepDefinition> steps, ISet<string> ids)
    {
        foreach (var step in steps)
        {
            if (string.Equals(step.Type, "assign", StringComparison.OrdinalIgnoreCase) &&
                step.Parameters.TryGetValue("value", out var value) &&
                string.Equals(value?.Trim(), "$input", StringComparison.Ordinal))
            {
                ids.Add(step.Id);
            }

            if (step.Children is { Count: > 0 })
                CollectRawInputCaptureStepIds(step.Children, ids);
        }
    }

    private static bool HasEntryChainTransform(WorkflowDefinition workflow, IReadOnlySet<string> captureStepIds)
    {
        var step = workflow.Steps[0];
        for (var hop = 0; hop < EntryChainProbeLimit && step is not null; hop++)
        {
            if (string.Equals(step.Type, "transform", StringComparison.OrdinalIgnoreCase))
            {
                // Only meaningful when the chain so far consisted purely of
                // raw-input captures. A bare entry transform makes no
                // demonstrable claim about the run input (it may render a
                // literal template), so it must not trigger the gate.
                return hop > 0;
            }

            if (!string.Equals(step.Type, "assign", StringComparison.OrdinalIgnoreCase) ||
                !captureStepIds.Contains(step.Id))
            {
                return false;
            }

            step = string.IsNullOrWhiteSpace(step.Next) ? null : workflow.GetStep(step.Next);
        }

        return false;
    }

    private static bool HasJsonAccessorOnCapture(
        IEnumerable<StepDefinition> steps,
        IReadOnlySet<string> captureStepIds)
    {
        if (captureStepIds.Count == 0)
            return false;

        foreach (var step in steps)
        {
            foreach (var value in step.Parameters.Values)
            {
                if (ContainsJsonAccessor(value, captureStepIds))
                    return true;
            }

            if (step.Branches is { Count: > 0 })
            {
                foreach (var branch in step.Branches)
                {
                    if (ContainsJsonAccessor(branch.Key, captureStepIds) ||
                        ContainsJsonAccessor(branch.Value, captureStepIds))
                    {
                        return true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(step.IdempotencyKey) &&
                ContainsJsonAccessor(step.IdempotencyKey, captureStepIds))
            {
                return true;
            }

            if (step.Children is { Count: > 0 } && HasJsonAccessorOnCapture(step.Children, captureStepIds))
                return true;
        }

        return false;
    }

    private static bool ContainsJsonAccessor(string? expression, IReadOnlySet<string> captureStepIds)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        foreach (var captureId in captureStepIds)
        {
            if (expression.Contains($"steps.{captureId}.json", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
