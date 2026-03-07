using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.PrimitiveExecutors;

/// <summary>
/// Multi-way branching module.
/// Matches the <c>on</c> parameter value against <c>StepDefinition.Branches</c>;
/// falls back to <c>_default</c> when no key matches.
/// Publishes <c>StepCompletedEvent</c> with <c>metadata["branch"]</c> indicating the chosen key.
/// <see cref="WorkflowRunGAgent"/> uses the branch value to resolve the next step.
/// </summary>
public sealed class SwitchPrimitiveExecutor : IWorkflowPrimitiveExecutor
{
    public string Name => "switch";

    public async Task HandleAsync(StepRequestEvent request, WorkflowPrimitiveExecutionContext ctx, CancellationToken ct)
    {
        if (request.StepType != "switch") return;

        var value = request.Parameters.GetValueOrDefault("on", "").Trim();
        if (string.IsNullOrEmpty(value))
            value = (request.Input ?? "").Trim();

        var branchKey = ResolveMatchingBranch(value, request.Parameters);

        ctx.Logger.LogInformation("Switch {StepId}: on={Value} → branch={Branch}",
            request.StepId, value.Length > 80 ? value[..80] + "..." : value, branchKey);

        var completed = new StepCompletedEvent
        {
            StepId = request.StepId,
            RunId = request.RunId,
            Success = true,
            Output = request.Input ?? "",
        };
        completed.Metadata["branch"] = branchKey;
        await ctx.PublishAsync(completed, EventDirection.Self, ct);
    }

    /// <summary>
    /// Resolve which branch key matches. Priority: exact → case-insensitive contains → _default.
    /// Branch keys are passed as <c>parameters["branch.{key}"] = targetStepId</c>.
    /// </summary>
    private static string ResolveMatchingBranch(string value, IDictionary<string, string> parameters)
    {
        const string prefix = "branch.";
        var candidates = new List<string>();
        foreach (var (k, _) in parameters)
        {
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                candidates.Add(k[prefix.Length..]);
        }

        foreach (var key in candidates)
        {
            if (string.Equals(value, key, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        foreach (var key in candidates)
        {
            if (key != "_default" && value.Contains(key, StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return "_default";
    }
}
