using System.Globalization;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    private async Task HandleDelayStepRequestAsync(StepRequestEvent request, CancellationToken ct)
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
                Error = "delay step requires non-empty run_id and step_id",
            }, EventDirection.Self, ct);
            return;
        }

        var durationMs = WorkflowParameterValueParser.GetBoundedInt(
            request.Parameters,
            1000,
            0,
            300_000,
            "duration_ms",
            "duration",
            "delay_ms");
        if (durationMs <= 0)
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = true,
                Output = request.Input ?? string.Empty,
            }, EventDirection.Self, ct);
            return;
        }

        var next = State.Clone();
        next.PendingDelays[stepId] = new WorkflowPendingDelayState
        {
            StepId = stepId,
            Input = request.Input ?? string.Empty,
            DurationMs = durationMs,
            SemanticGeneration = NextSemanticGeneration(
                State.PendingDelays.TryGetValue(stepId, out var existing) ? existing.SemanticGeneration : 0),
        };
        await PersistStateAsync(next, ct);

        await ScheduleWorkflowCallbackAsync(
            BuildDelayCallbackId(runId, stepId),
            TimeSpan.FromMilliseconds(durationMs),
            new DelayStepTimeoutFiredEvent
            {
                RunId = runId,
                StepId = stepId,
                DurationMs = durationMs,
            },
            next.PendingDelays[stepId].SemanticGeneration,
            stepId,
            sessionId: null,
            kind: "delay",
            ct);
    }

    private async Task HandleWaitSignalStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var stepId = request.StepId?.Trim() ?? string.Empty;
        var signalName = NormalizeSignalName(
            WorkflowParameterValueParser.GetString(request.Parameters, "default", "signal_name", "signal"));
        var prompt = WorkflowParameterValueParser.GetString(request.Parameters, string.Empty, "prompt", "message");

        var timeoutMs = WorkflowParameterValueParser.GetBoundedInt(
            request.Parameters,
            0,
            0,
            3_600_000,
            "timeout_ms");
        if (timeoutMs <= 0 &&
            WorkflowParameterValueParser.TryGetBoundedInt(
                request.Parameters,
                out var timeoutSeconds,
                0,
                3_600,
                "timeout_seconds",
                "timeout"))
        {
            timeoutMs = Math.Clamp(timeoutSeconds * 1000, 0, 3_600_000);
        }

        var next = State.Clone();
        next.Status = StatusSuspended;
        next.PendingSignalWaits[stepId] = new WorkflowPendingSignalWaitState
        {
            StepId = stepId,
            SignalName = signalName,
            Input = request.Input ?? string.Empty,
            Prompt = prompt,
            TimeoutMs = timeoutMs,
            TimeoutGeneration = timeoutMs > 0
                ? NextSemanticGeneration(
                    State.PendingSignalWaits.TryGetValue(stepId, out var existing) ? existing.TimeoutGeneration : 0)
                : 0,
            WaitToken = Guid.NewGuid().ToString("N"),
        };
        await PersistStateAsync(next, ct);

        await PublishAsync(new WaitingForSignalEvent
        {
            StepId = stepId,
            SignalName = signalName,
            Prompt = prompt,
            TimeoutMs = timeoutMs,
            RunId = runId,
            WaitToken = next.PendingSignalWaits[stepId].WaitToken,
        }, EventDirection.Both, ct);

        if (timeoutMs <= 0)
            return;

        await ScheduleWorkflowCallbackAsync(
            BuildWaitSignalCallbackId(runId, signalName, stepId),
            TimeSpan.FromMilliseconds(timeoutMs),
            new WaitSignalTimeoutFiredEvent
            {
                RunId = runId,
                StepId = stepId,
                SignalName = signalName,
                TimeoutMs = timeoutMs,
            },
            next.PendingSignalWaits[stepId].TimeoutGeneration,
            stepId,
            sessionId: null,
            kind: "wait_signal",
            ct);
    }

    private async Task HandleHumanGateStepRequestAsync(StepRequestEvent request, string gateType, CancellationToken ct)
    {
        var timeoutSeconds = WorkflowParameterValueParser.ResolveTimeoutSeconds(
            request.Parameters,
            gateType == "human_input" ? 1800 : 3600);
        var prompt = WorkflowParameterValueParser.GetString(
            request.Parameters,
            gateType == "human_input" ? "Please provide input:" : "Approve this step?",
            "prompt",
            "message");
        var variable = WorkflowParameterValueParser.GetString(request.Parameters, "user_input", "variable");
        var onTimeout = WorkflowParameterValueParser.GetString(request.Parameters, "fail", "on_timeout");
        var onReject = WorkflowParameterValueParser.GetString(request.Parameters, "fail", "on_reject");

        var next = State.Clone();
        next.Status = StatusSuspended;
        next.PendingHumanGates[request.StepId] = new WorkflowPendingHumanGateState
        {
            StepId = request.StepId,
            GateType = gateType,
            Input = request.Input ?? string.Empty,
            Prompt = prompt,
            Variable = variable,
            TimeoutSeconds = timeoutSeconds,
            OnTimeout = onTimeout,
            OnReject = onReject,
            ResumeToken = Guid.NewGuid().ToString("N"),
        };
        await PersistStateAsync(next, ct);

        var suspended = new WorkflowSuspendedEvent
        {
            RunId = State.RunId,
            StepId = request.StepId,
            SuspensionType = gateType,
            Prompt = prompt,
            TimeoutSeconds = timeoutSeconds,
            ResumeToken = next.PendingHumanGates[request.StepId].ResumeToken,
        };
        if (!string.IsNullOrWhiteSpace(variable))
            suspended.Metadata["variable"] = variable;
        if (!string.IsNullOrWhiteSpace(onTimeout))
            suspended.Metadata["on_timeout"] = onTimeout;
        if (!string.IsNullOrWhiteSpace(onReject))
            suspended.Metadata["on_reject"] = onReject;
        suspended.Metadata["resume_token"] = next.PendingHumanGates[request.StepId].ResumeToken;
        await PublishAsync(suspended, EventDirection.Both, ct);
    }

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

    private async Task HandleParallelStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        await EnsureAgentTreeAsync(ct);

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var workerRoles = new List<string>();
        if (request.Parameters.TryGetValue("workers", out var workers) && !string.IsNullOrWhiteSpace(workers))
            workerRoles.AddRange(WorkflowParameterValueParser.ParseStringList(workers));

        var count = workerRoles.Count > 0
            ? workerRoles.Count
            : WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 3, 1, 16, "parallel_count", "count");

        if (workerRoles.Count == 0 && string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = false,
                Error = "parallel requires parameters.workers (CSV/JSON list) or target_role",
            }, EventDirection.Self, ct);
            return;
        }

        var voteStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
            request.Parameters.TryGetValue("vote_step_type", out var voteType) ? voteType : string.Empty);
        var next = State.Clone();
        var parallelState = new WorkflowParallelState
        {
            ExpectedCount = count,
            VoteStepType = voteStepType,
            VoteStepId = string.Empty,
            WorkersSuccess = false,
        };
        foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("vote_param_", StringComparison.OrdinalIgnoreCase)))
            parallelState.VoteParameters[key["vote_param_".Length..]] = value;
        next.PendingParallelSteps[request.StepId] = parallelState;
        await PersistStateAsync(next, ct);

        for (var i = 0; i < count; i++)
        {
            var role = i < workerRoles.Count ? workerRoles[i] : request.TargetRole;
            await DispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_sub_{i}",
                "llm_call",
                request.Input ?? string.Empty,
                role ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
    }

    private async Task HandleForEachStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n---\n", "delimiter", "separator"),
            "\n---\n");
        var items = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(request.Input, delimiter);
        if (items.Length == 0 && request.Parameters.TryGetValue("items", out var rawItems))
            items = WorkflowParameterValueParser.ParseStringList(rawItems).ToArray();

        if (items.Length == 0)
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = true,
                Output = string.Empty,
            }, EventDirection.Self, ct);
            return;
        }

        var subStepType = WorkflowPrimitiveCatalog.ToCanonicalType(
            WorkflowParameterValueParser.GetString(request.Parameters, "parallel", "sub_step_type", "step"));
        var subTargetRole = WorkflowParameterValueParser.GetString(
            request.Parameters,
            request.TargetRole,
            "sub_target_role",
            "sub_role");

        var next = State.Clone();
        next.PendingForeachSteps[request.StepId] = new WorkflowForEachState
        {
            ExpectedCount = items.Length,
        };
        await PersistStateAsync(next, ct);

        for (var i = 0; i < items.Length; i++)
        {
            var subParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase)))
                subParameters[key["sub_param_".Length..]] = value;
            await DispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_item_{i}",
                subStepType,
                items[i].Trim(),
                subTargetRole ?? string.Empty,
                subParameters,
                ct);
        }
    }

    private async Task HandleMapReduceStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var delimiter = WorkflowParameterValueParser.NormalizeEscapedText(
            WorkflowParameterValueParser.GetString(request.Parameters, "\n---\n", "delimiter", "separator"),
            "\n---\n");
        var items = WorkflowParameterValueParser.SplitInputByDelimiterOrJsonArray(request.Input, delimiter);
        if (items.Length == 0 && request.Parameters.TryGetValue("items", out var rawItems))
            items = WorkflowParameterValueParser.ParseStringList(rawItems).ToArray();

        if (items.Length == 0)
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = true,
                Output = string.Empty,
            }, EventDirection.Self, ct);
            return;
        }

        var mapType = WorkflowPrimitiveCatalog.ToCanonicalType(
            WorkflowParameterValueParser.GetString(request.Parameters, "llm_call", "map_step_type", "map_type"));
        var mapRole = WorkflowParameterValueParser.GetString(
            request.Parameters,
            request.TargetRole,
            "map_target_role",
            "map_role");
        var reduceType = WorkflowPrimitiveCatalog.ToCanonicalType(
            request.Parameters.TryGetValue("reduce_step_type", out var reduceTypeRaw)
                ? reduceTypeRaw
                : request.Parameters.GetValueOrDefault("reduce_type", "llm_call"));
        var reduceRole = WorkflowParameterValueParser.GetString(
            request.Parameters,
            request.TargetRole,
            "reduce_target_role",
            "reduce_role");
        var reducePrefix = WorkflowParameterValueParser.GetString(
            request.Parameters,
            string.Empty,
            "reduce_prompt_prefix",
            "reduce_prefix");

        var next = State.Clone();
        next.PendingMapReduceSteps[request.StepId] = new WorkflowMapReduceState
        {
            MapCount = items.Length,
            ReduceType = reduceType,
            ReduceRole = reduceRole ?? string.Empty,
            ReducePromptPrefix = reducePrefix,
            ReduceStepId = string.Empty,
        };
        await PersistStateAsync(next, ct);

        for (var i = 0; i < items.Length; i++)
        {
            await DispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_map_{i}",
                mapType,
                items[i],
                mapRole ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
    }

    private async Task HandleRaceStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        await EnsureAgentTreeAsync(ct);

        var runId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var workers = WorkflowParameterValueParser.GetStringList(request.Parameters, "workers", "worker_roles");
        var count = workers.Count > 0
            ? workers.Count
            : WorkflowParameterValueParser.GetBoundedInt(request.Parameters, 2, 1, 10, "count", "race_count");

        if (workers.Count == 0 && string.IsNullOrWhiteSpace(request.TargetRole))
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = runId,
                Success = false,
                Error = "race requires parameters.workers (CSV/JSON list) or target_role",
            }, EventDirection.Self, ct);
            return;
        }

        var next = State.Clone();
        next.PendingRaceSteps[request.StepId] = new WorkflowRaceState
        {
            Total = count,
            Received = 0,
            Resolved = false,
        };
        await PersistStateAsync(next, ct);

        for (var i = 0; i < count; i++)
        {
            var role = i < workers.Count ? workers[i] : request.TargetRole;
            await DispatchInternalStepAsync(
                runId,
                request.StepId,
                $"{request.StepId}_race_{i}",
                "llm_call",
                request.Input ?? string.Empty,
                role ?? string.Empty,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
        }
    }

    private async Task HandleWhileStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var maxIterations = int.TryParse(request.Parameters.GetValueOrDefault("max_iterations", "10"), out var max)
            ? Math.Clamp(max, 1, 1_000_000)
            : 10;
        var subParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Parameters.Where(x => x.Key.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase)))
            subParameters[key["sub_param_".Length..]] = value;

        var next = State.Clone();
        next.PendingWhileSteps[request.StepId] = new WorkflowWhileState
        {
            StepId = request.StepId,
            SubStepType = WorkflowPrimitiveCatalog.ToCanonicalType(request.Parameters.GetValueOrDefault("step", "llm_call")),
            SubTargetRole = request.TargetRole ?? string.Empty,
            Iteration = 0,
            MaxIterations = maxIterations,
            ConditionExpression = string.IsNullOrWhiteSpace(request.Parameters.GetValueOrDefault("condition", "true"))
                ? "true"
                : request.Parameters.GetValueOrDefault("condition", "true"),
        };
        foreach (var (key, value) in subParameters)
            next.PendingWhileSteps[request.StepId].SubParameters[key] = value;
        await PersistStateAsync(next, ct);

        await DispatchWhileIterationAsync(next.PendingWhileSteps[request.StepId], request.Input ?? string.Empty, ct);
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

    private async Task HandleWorkflowCallStepRequestAsync(StepRequestEvent request, CancellationToken ct)
    {
        var parentRunId = WorkflowRunIdNormalizer.Normalize(request.RunId);
        var parentStepId = request.StepId?.Trim() ?? string.Empty;
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(request.Parameters.GetValueOrDefault("workflow", string.Empty));
        var lifecycle = WorkflowCallLifecycle.Normalize(request.Parameters.GetValueOrDefault("lifecycle", string.Empty));

        if (string.IsNullOrWhiteSpace(parentStepId))
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId ?? string.Empty,
                RunId = parentRunId,
                Success = false,
                Error = "workflow_call missing step_id",
            }, EventDirection.Self, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(workflowName))
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = parentRunId,
                Success = false,
                Error = "workflow_call missing workflow parameter",
            }, EventDirection.Self, ct);
            return;
        }

        if (!WorkflowCallLifecycle.IsSupported(lifecycle))
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = parentRunId,
                Success = false,
                Error = $"workflow_call lifecycle must be {WorkflowCallLifecycle.AllowedValuesText}, got '{lifecycle}'",
            }, EventDirection.Self, ct);
            return;
        }

        var invocationId = WorkflowCallInvocationIdFactory.Build(parentRunId, parentStepId);
        var childRunId = invocationId;
        var childActorId = BuildSubWorkflowRunActorId(workflowName, lifecycle, invocationId);
        var next = State.Clone();
        next.PendingSubWorkflows[childRunId] = new WorkflowPendingSubWorkflowState
        {
            InvocationId = invocationId,
            ParentStepId = parentStepId,
            WorkflowName = workflowName,
            Input = request.Input ?? string.Empty,
            Lifecycle = lifecycle,
            ChildActorId = childActorId,
            ChildRunId = childRunId,
        };
        await PersistStateAsync(next, ct);

        try
        {
            var childActor = await ResolveOrCreateSubWorkflowRunActorAsync(childActorId, ct);
            await _runtime.LinkAsync(Id, childActor.Id, ct);
            await childActor.HandleEventAsync(CreateWorkflowDefinitionBindEnvelope(
                await ResolveWorkflowYamlAsync(workflowName, ct),
                workflowName), ct);
            await SendToAsync(childActor.Id, new ChatRequestEvent
            {
                Prompt = request.Input ?? string.Empty,
                SessionId = childRunId,
            }, ct);
        }
        catch (Exception ex)
        {
            var rollback = State.Clone();
            rollback.PendingSubWorkflows.Remove(childRunId);
            await PersistStateAsync(rollback, ct);
            await PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = parentRunId,
                Success = false,
                Error = $"workflow_call invocation failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }
    }
}
