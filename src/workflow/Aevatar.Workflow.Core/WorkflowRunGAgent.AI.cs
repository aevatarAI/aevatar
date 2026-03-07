using System.Globalization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    private async Task HandleLlmCallStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var stepId = request.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(stepId))
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = false,
                Error = "llm_call step requires non-empty run_id and step_id",
            }, EventDirection.Self, ct);
            return;
        }

        await EnsureAgentTreeAsync(ct);

        var prompt = request.Input ?? string.Empty;
        if (request.Parameters.TryGetValue("prompt_prefix", out var prefix) && !string.IsNullOrWhiteSpace(prefix))
            prompt = prefix.TrimEnd() + "\n\n" + prompt;

        var attempt = State.StepExecutions.TryGetValue(stepId, out var execution) && execution.Attempt > 0
            ? execution.Attempt
            : 1;
        var timeoutMs = ResolveLlmTimeoutMs(request.Parameters);
        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(Id, runId, stepId, attempt);
        var next = State.Clone();
        next.PendingLlmCalls[sessionId] = new WorkflowPendingLlmCallState
        {
            SessionId = sessionId,
            StepId = stepId,
            OriginalInput = request.Input ?? string.Empty,
            TargetRole = request.TargetRole ?? string.Empty,
            TimeoutMs = timeoutMs,
            WatchdogGeneration = NextSemanticGeneration(
                State.PendingLlmCalls.TryGetValue(sessionId, out var existing) ? existing.WatchdogGeneration : 0),
            Attempt = attempt,
        };
        await PersistStateAsync(next, ct);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        chatRequest.Metadata["aevatar.llm_timeout_ms"] = timeoutMs.ToString(CultureInfo.InvariantCulture);

        try
        {
            if (!string.IsNullOrWhiteSpace(request.TargetRole))
            {
                await SendToAsync(
                    WorkflowRoleActorIdResolver.ResolveTargetActorId(Id, request.TargetRole),
                    chatRequest,
                    ct);
            }
            else
            {
                await PublishAsync(chatRequest, EventDirection.Self, ct);
            }
        }
        catch (Exception ex)
        {
            await RemovePendingLlmCallAndPublishFailureAsync(sessionId, stepId, runId, $"LLM dispatch failed: {ex.Message}", ct);
            return;
        }

        try
        {
            await ScheduleWorkflowCallbackAsync(
                BuildLlmWatchdogCallbackId(sessionId),
                TimeSpan.FromMilliseconds(timeoutMs),
                new LlmCallWatchdogTimeoutFiredEvent
                {
                    SessionId = sessionId,
                    TimeoutMs = timeoutMs,
                    RunId = runId,
                    StepId = stepId,
                },
                next.PendingLlmCalls[sessionId].WatchdogGeneration,
                stepId,
                sessionId,
                "llm_watchdog",
                ct);
        }
        catch (Exception ex)
        {
            await RemovePendingLlmCallAndPublishFailureAsync(sessionId, stepId, runId, $"LLM watchdog scheduling failed: {ex.Message}", ct);
        }
    }

    private async Task HandleEvaluateStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        await EnsureAgentTreeAsync(ct);

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var criteria = request.Parameters.GetValueOrDefault("criteria", "quality");
        var scale = request.Parameters.GetValueOrDefault("scale", "1-5");
        var threshold = double.TryParse(request.Parameters.GetValueOrDefault("threshold", "3"), out var parsedThreshold)
            ? parsedThreshold
            : 3.0;
        var onBelow = request.Parameters.GetValueOrDefault("on_below", string.Empty);

        var attempt = State.StepExecutions.TryGetValue(request.StepId, out var execution) && execution.Attempt > 0
            ? execution.Attempt
            : 1;
        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(Id, runId, request.StepId, attempt);
        var prompt = $"""
            Evaluate the following content on these criteria: {criteria}
            Use a numeric scale of {scale}. Respond with ONLY a single number (the score).

            Content to evaluate:
            {request.Input}
            """;

        var next = State.Clone();
        next.PendingEvaluations[sessionId] = new WorkflowPendingEvaluateState
        {
            SessionId = sessionId,
            StepId = request.StepId,
            OriginalInput = request.Input ?? string.Empty,
            Threshold = threshold,
            OnBelow = onBelow,
            TargetRole = request.TargetRole ?? string.Empty,
            Attempt = attempt,
        };
        await PersistStateAsync(next, ct);

        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        if (!string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await SendToAsync(
                WorkflowRoleActorIdResolver.ResolveTargetActorId(Id, request.TargetRole),
                chatRequest,
                ct);
            return;
        }

        await PublishAsync(chatRequest, EventDirection.Self, ct);
    }

    private async Task HandleReflectStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        await EnsureAgentTreeAsync(ct);

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var maxRounds = int.TryParse(request.Parameters.GetValueOrDefault("max_rounds", "3"), out var parsedMaxRounds)
            ? Math.Clamp(parsedMaxRounds, 1, 10)
            : 3;
        var criteria = request.Parameters.GetValueOrDefault("criteria", "quality and correctness");
        var initialState = new WorkflowPendingReflectState
        {
            SessionId = string.Empty,
            StepId = request.StepId,
            TargetRole = request.TargetRole ?? string.Empty,
            CurrentDraft = request.Input ?? string.Empty,
            Criteria = criteria,
            MaxRounds = maxRounds,
            Round = 0,
            Phase = "critique",
        };

        await DispatchReflectPhaseAsync(runId, initialState, request.Input ?? string.Empty, ct);
    }

    private async Task HandleCacheStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var cacheKey = request.Parameters.GetValueOrDefault("cache_key", request.Input ?? string.Empty);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (State.CacheEntries.TryGetValue(cacheKey, out var existing) &&
            existing.ExpiresAtUnixTimeMs > nowMs)
        {
            var hit = new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = true,
                Output = existing.Value,
            };
            hit.Metadata["cache.hit"] = "true";
            hit.Metadata["cache.key"] = ShortenKey(cacheKey);
            await PublishAsync(hit, EventDirection.Self, ct);
            return;
        }

        if (State.PendingCacheCalls.TryGetValue(cacheKey, out var pending))
        {
            var nextPending = pending.Clone();
            nextPending.Waiters.Add(new WorkflowCacheWaiter
            {
                ParentStepId = request.StepId,
            });
            var next = State.Clone();
            next.PendingCacheCalls[cacheKey] = nextPending;
            await PersistStateAsync(next, ct);
            return;
        }

        var ttlSeconds = int.TryParse(request.Parameters.GetValueOrDefault("ttl_seconds", "3600"), out var ttl)
            ? Math.Clamp(ttl, 1, 86_400)
            : 3600;
        var childType = WorkflowPrimitiveCatalog.ToCanonicalType(
            request.Parameters.GetValueOrDefault("child_step_type", "llm_call"));
        var childRole = request.Parameters.GetValueOrDefault("child_target_role", request.TargetRole);
        var childStepId = $"{request.StepId}_cached_{Guid.NewGuid():N}";

        var nextState = State.Clone();
        var pendingState = new WorkflowPendingCacheState
        {
            ChildStepId = childStepId,
            TtlSeconds = ttlSeconds,
        };
        pendingState.Waiters.Add(new WorkflowCacheWaiter
        {
            ParentStepId = request.StepId,
        });
        nextState.PendingCacheCalls[cacheKey] = pendingState;
        await PersistStateAsync(nextState, ct);

        await DispatchInternalStepAsync(
            runId,
            request.StepId,
            childStepId,
            childType,
            request.Input ?? string.Empty,
            childRole ?? string.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ct);
    }
}
