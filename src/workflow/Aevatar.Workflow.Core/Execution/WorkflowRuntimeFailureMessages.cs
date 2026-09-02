using Aevatar.Workflow.Core.Primitives;
using System.Text.RegularExpressions;

namespace Aevatar.Workflow.Core.Execution;

internal static partial class WorkflowRuntimeFailureMessages
{
    private const int MaxMessageLength = 240;
    private const int MaxSummaryLength = 96;

    public static string StartDispatchFailed(Exception exception) =>
        Format("start_dispatch_failed", (FailureStep?)null, "start_dispatch", exception);

    public static string StepDispatchFailed(StepDefinition step, Exception exception) =>
        Format("step_dispatch_failed", step, "dispatch", exception);

    public static string StepExecutorFailed(string stepId, string stepType, Exception exception) =>
        Format(
            "step_executor_failed",
            new FailureStep(stepId, stepType),
            "executor",
            exception);

    public static string StepCompletionHandlingFailed(StepDefinition? step, StepCompletedEvent completion, Exception exception)
    {
        var failureStep = new FailureStep(
            string.IsNullOrWhiteSpace(completion.StepId) ? step?.Id ?? string.Empty : completion.StepId,
            step?.Type ?? string.Empty);
        return Format("step_completion_handling_failed", failureStep, "completion", exception);
    }

    private static string Format(string code, StepDefinition? step, string stage, Exception exception) =>
        Format(code, step == null ? null : new FailureStep(step.Id, step.Type), stage, exception);

    private static string Format(string code, FailureStep? step, string stage, Exception exception)
    {
        var summary = SafeSummary(exception);
        var stepPart = step == null || string.IsNullOrWhiteSpace(step.Value.StepId)
            ? $"failed during {stage}"
            : $"step '{CleanToken(step.Value.StepId)}' ({CleanToken(step.Value.StepType)}) failed during {stage}";
        var message = $"{code}: {stepPart}: {summary}";
        return Truncate(message, MaxMessageLength);
    }

    private static string SafeSummary(Exception exception)
    {
        var raw = exception.Message;
        if (string.IsNullOrWhiteSpace(raw))
            raw = exception.GetType().Name;

        var firstLine = raw
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        var sanitized = AuthorizationPattern().Replace(firstLine, "[redacted]");
        sanitized = BearerPattern().Replace(sanitized, "[redacted]");
        sanitized = TokenAssignmentPattern().Replace(sanitized, "$1=[redacted]");
        sanitized = JwtLikePattern().Replace(sanitized, "[redacted]");
        sanitized = StackFramePattern().Replace(sanitized, string.Empty);
        sanitized = PayloadPattern().Replace(sanitized, "payload=[redacted]");
        sanitized = WhitespacePattern().Replace(sanitized, " ").Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = exception.GetType().Name;
        return Truncate(sanitized, MaxSummaryLength);
    }

    private static string CleanToken(string? value)
    {
        var token = value?.Trim() ?? string.Empty;
        token = WhitespacePattern().Replace(token, "_");
        token = token.Replace("'", string.Empty);
        return Truncate(token, 64);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private readonly record struct FailureStep(string StepId, string StepType);

    [GeneratedRegex(@"authorization\s*:\s*[^\s]+(?:\s+[^\s]+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"\bbearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\b(token|access_token|api_key|apikey|secret)\s*[:=]\s*[""']?[^,""'\s}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenAssignmentPattern();

    [GeneratedRegex(@"\b[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtLikePattern();

    [GeneratedRegex(@"\bat\s+[\w.`<>]+\([^)]*\).*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StackFramePattern();

    [GeneratedRegex(@"payload\s*=\s*.+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PayloadPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
