using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Modules;

/// <summary>
/// Multi-way branching module.
/// Matches the <c>on</c> parameter value against <c>StepDefinition.Branches</c>;
/// falls back to <c>_default</c> when no key matches.
/// Publishes <c>StepCompletedEvent.BranchKey</c> indicating the chosen branch.
/// The <c>WorkflowLoopModule</c> uses this typed field to resolve the next step.
/// </summary>
public sealed class SwitchModule : IEventModule<IWorkflowExecutionContext>
{
    public string Name => "switch";
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(StepRequestEvent.Descriptor) == true;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct)
    {
        var request = envelope.Payload!.Unpack<StepRequestEvent>();
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
            BranchKey = branchKey,
        };
        await ctx.PublishAsync(completed, BroadcastDirection.Self, ct);
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
