using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

internal static class WorkflowRunSupport
{
    public static void ResetRuntimeState(WorkflowRunState state, bool clearChildActors)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.RunId = string.Empty;
        state.Status = string.Empty;
        state.ActiveStepId = string.Empty;
        state.FinalOutput = string.Empty;
        state.FinalError = string.Empty;
        state.Variables.Clear();
        state.StepExecutions.Clear();
        state.RetryAttemptsByStepId.Clear();
        state.PendingTimeouts.Clear();
        state.PendingRetryBackoffs.Clear();
        state.PendingDelays.Clear();
        state.PendingSignalWaits.Clear();
        state.PendingHumanGates.Clear();
        state.PendingLlmCalls.Clear();
        state.PendingEvaluations.Clear();
        state.PendingReflections.Clear();
        state.PendingParallelSteps.Clear();
        state.PendingForeachSteps.Clear();
        state.PendingMapReduceSteps.Clear();
        state.PendingRaceSteps.Clear();
        state.PendingWhileSteps.Clear();
        state.PendingSubWorkflows.Clear();
        state.CacheEntries.Clear();
        state.PendingCacheCalls.Clear();
        if (clearChildActors)
            state.ChildActorIds.Clear();
    }

    public static int NextSemanticGeneration(int current) =>
        current >= int.MaxValue - 1 ? 1 : current + 1;

    public static bool TryMatchRunAndStep(string activeRunId, string runId, string stepId) =>
        string.Equals(WorkflowRunIdNormalizer.Normalize(runId), activeRunId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(stepId);

    public static bool MatchesSemanticGeneration(EventEnvelope envelope, int expectedGeneration)
    {
        if (expectedGeneration <= 0)
            return false;

        if (envelope.Metadata == null ||
            !envelope.Metadata.TryGetValue("workflow.semantic_generation", out var rawGeneration) ||
            !int.TryParse(rawGeneration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actualGeneration))
        {
            return false;
        }

        return actualGeneration == expectedGeneration;
    }

    public static string NormalizeSignalName(string signalName) =>
        string.IsNullOrWhiteSpace(signalName) ? "default" : signalName.Trim().ToLowerInvariant();

    public static bool TryResolvePendingSignalWait(
        WorkflowRunState state,
        string? waitToken,
        out WorkflowPendingSignalWaitState pending)
    {
        pending = default!;
        var normalizedToken = (waitToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
            return false;

        foreach (var candidate in state.PendingSignalWaits.Values)
        {
            if (!string.Equals(candidate.WaitToken, normalizedToken, StringComparison.Ordinal))
                continue;

            pending = candidate;
            return true;
        }

        return false;
    }

    public static bool TryResolvePendingHumanGate(
        WorkflowRunState state,
        string? resumeToken,
        out WorkflowPendingHumanGateState pending)
    {
        pending = default!;
        var normalizedToken = (resumeToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
            return false;

        foreach (var candidate in state.PendingHumanGates.Values)
        {
            if (!string.Equals(candidate.ResumeToken, normalizedToken, StringComparison.Ordinal))
                continue;

            pending = candidate;
            return true;
        }

        return false;
    }

    public static bool IsTimeoutError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase);

    public static int ResolveLlmTimeoutMs(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("llm_timeout_ms", out var llmTimeoutRaw) &&
            int.TryParse(llmTimeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var llmTimeoutMs) &&
            llmTimeoutMs > 0)
        {
            return llmTimeoutMs;
        }

        if (parameters.TryGetValue("timeout_ms", out var timeoutRaw) &&
            int.TryParse(timeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutMs) &&
            timeoutMs > 0)
        {
            return timeoutMs;
        }

        return 1_800_000;
    }

    public static bool TryExtractLlmFailure(string? content, out string error)
    {
        const string prefix = "[[AEVATAR_LLM_ERROR]]";
        if (string.IsNullOrEmpty(content) || !content.StartsWith(prefix, StringComparison.Ordinal))
        {
            error = string.Empty;
            return false;
        }

        var extracted = content[prefix.Length..].Trim();
        error = string.IsNullOrWhiteSpace(extracted) ? "LLM call failed." : extracted;
        return true;
    }

    public static double ParseScore(string text)
    {
        var trimmed = text.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            return numeric;

        foreach (var token in trimmed.Split([' ', '\n', '\r', ',', '/', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return 0;
    }

    public static WorkflowRecordedStepResult ToRecordedResult(StepCompletedEvent evt)
    {
        var recorded = new WorkflowRecordedStepResult
        {
            StepId = evt.StepId,
            Success = evt.Success,
            Output = evt.Output ?? string.Empty,
            Error = evt.Error ?? string.Empty,
            WorkerId = evt.WorkerId ?? string.Empty,
        };
        foreach (var (key, value) in evt.Metadata)
            recorded.Metadata[key] = value;
        return recorded;
    }

    public static bool TryGetParallelParent(string stepId, out string parent)
    {
        var index = stepId.LastIndexOf("_sub_", StringComparison.Ordinal);
        if (index <= 0)
        {
            parent = string.Empty;
            return false;
        }

        parent = stepId[..index];
        return true;
    }

    public static string? TryGetForEachParent(string stepId)
    {
        const string marker = "_item_";
        var index = stepId.LastIndexOf(marker, StringComparison.Ordinal);
        if (index <= 0)
            return null;

        var suffix = stepId[(index + marker.Length)..];
        return suffix.All(char.IsDigit) ? stepId[..index] : null;
    }

    public static string? TryGetMapReduceParent(string stepId)
    {
        var index = stepId.LastIndexOf("_map_", StringComparison.Ordinal);
        return index > 0 ? stepId[..index] : null;
    }

    public static string? TryGetRaceParent(string stepId)
    {
        var index = stepId.LastIndexOf("_race_", StringComparison.Ordinal);
        return index > 0 ? stepId[..index] : null;
    }

    public static string? TryGetWhileParent(string stepId)
    {
        var index = stepId.LastIndexOf("_iter_", StringComparison.Ordinal);
        if (index <= 0)
            return null;

        var suffix = stepId[(index + "_iter_".Length)..];
        return suffix.All(char.IsDigit) ? stepId[..index] : null;
    }

    public static string ShortenKey(string key) =>
        key.Length > 60 ? key[..60] + "..." : key;

    public static string BuildStepTimeoutCallbackId(string runId, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-step-timeout", runId, stepId);

    public static string BuildRetryBackoffCallbackId(string runId, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-step-retry-backoff", runId, stepId);

    public static string BuildDelayCallbackId(string runId, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("delay-step", runId, stepId);

    public static string BuildWaitSignalCallbackId(string runId, string signalName, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("wait-signal-timeout", runId, signalName, stepId);

    public static string BuildLlmWatchdogCallbackId(string sessionId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("llm-watchdog", sessionId);

    public static string BuildSubWorkflowRunActorId(
        string ownerActorId,
        string workflowName,
        string lifecycle,
        string invocationId)
    {
        var workflowSegment = SanitizeActorSegment(workflowName);
        var lifecycleSegment = SanitizeActorSegment(lifecycle);
        return $"{ownerActorId}:workflow:{workflowSegment}:{lifecycleSegment}:{invocationId}";
    }

    public static string BuildChildActorId(string ownerActorId, string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            throw new InvalidOperationException("Role id is required to create child actor.");
        return $"{ownerActorId}:{roleId.Trim()}";
    }

    private static string SanitizeActorSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "default";

        var cleaned = new string(value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "default" : cleaned;
    }
}
