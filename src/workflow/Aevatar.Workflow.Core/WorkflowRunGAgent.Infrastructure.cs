using System.Globalization;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent
{
    private Task EnsureAgentTreeAsync(CancellationToken ct) =>
        _effectDispatcher.EnsureAgentTreeAsync(ct);

    private Task ScheduleWorkflowCallbackAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        int semanticGeneration,
        string stepId,
        string? sessionId,
        string kind,
        CancellationToken ct) =>
        _effectDispatcher.ScheduleWorkflowCallbackAsync(
            callbackId,
            dueTime,
            evt,
            semanticGeneration,
            stepId,
            sessionId,
            kind,
            ct);

    private Task<IActor> ResolveOrCreateSubWorkflowRunActorAsync(string actorId, CancellationToken ct) =>
        _effectDispatcher.ResolveOrCreateSubWorkflowRunActorAsync(actorId, ct);

    private Task<string> ResolveWorkflowYamlAsync(string workflowName, CancellationToken ct) =>
        _effectDispatcher.ResolveWorkflowYamlAsync(workflowName, ct);

    private EventEnvelope CreateWorkflowDefinitionBindEnvelope(string workflowYaml, string workflowName) =>
        _effectDispatcher.CreateWorkflowDefinitionBindEnvelope(workflowYaml, workflowName);

    private EventEnvelope CreateRoleAgentInitializeEnvelope(RoleDefinition role) =>
        _effectDispatcher.CreateRoleAgentInitializeEnvelope(role);

    private async Task<string> ResolveWorkflowYamlCoreAsync(string workflowName, CancellationToken ct)
    {
        foreach (var (registeredName, yaml) in State.InlineWorkflowYamls)
        {
            if (string.Equals(registeredName, workflowName, StringComparison.OrdinalIgnoreCase))
                return yaml;
        }

        var resolver = _workflowDefinitionResolver ?? Services?.GetService<IWorkflowDefinitionResolver>();
        if (resolver == null)
            throw new InvalidOperationException("workflow_call requires IWorkflowDefinitionResolver service registration.");

        var yamlFromResolver = await resolver.GetWorkflowYamlAsync(workflowName, ct);
        if (string.IsNullOrWhiteSpace(yamlFromResolver))
            throw new InvalidOperationException($"workflow_call references unregistered workflow '{workflowName}'");
        return yamlFromResolver;
    }

    private EventEnvelope CreateWorkflowDefinitionBindEnvelopeCore(string workflowYaml, string workflowName)
    {
        var bind = new BindWorkflowDefinitionEvent
        {
            WorkflowYaml = workflowYaml ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
        };
        foreach (var (name, yaml) in State.InlineWorkflowYamls)
            bind.InlineWorkflowYamls[name] = yaml;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(bind),
            PublisherId = Id,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }

    private EventEnvelope CreateRoleAgentInitializeEnvelopeCore(RoleDefinition role)
    {
        var initialize = new InitializeRoleAgentEvent
        {
            RoleName = role.Name ?? string.Empty,
            ProviderName = string.IsNullOrWhiteSpace(role.Provider) ? string.Empty : role.Provider,
            Model = string.IsNullOrWhiteSpace(role.Model) ? string.Empty : role.Model,
            SystemPrompt = role.SystemPrompt ?? string.Empty,
            MaxTokens = role.MaxTokens ?? 0,
            MaxToolRounds = role.MaxToolRounds ?? 0,
            MaxHistoryMessages = role.MaxHistoryMessages ?? 0,
            StreamBufferCapacity = role.StreamBufferCapacity ?? 0,
            EventModules = role.EventModules ?? string.Empty,
            EventRoutes = role.EventRoutes ?? string.Empty,
        };
        if (role.Temperature.HasValue)
            initialize.Temperature = role.Temperature.Value;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(initialize),
            PublisherId = Id,
            Direction = EventDirection.Self,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }

    private StepRequestEvent BuildStepRequest(StepDefinition step, string input, string runId)
    {
        var request = new StepRequestEvent
        {
            StepId = step.Id,
            StepType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type),
            RunId = runId,
            Input = input,
            TargetRole = step.TargetRole ?? string.Empty,
        };

        var variables = ResolveVariables(input);
        foreach (var (key, value) in step.Parameters)
        {
            if (ShouldDeferWhileParameterEvaluation(request.StepType, key))
            {
                request.Parameters[key] = value;
                continue;
            }

            var evaluated = _expressionEvaluator.Evaluate(value, variables);
            request.Parameters[key] = WorkflowPrimitiveCatalog.IsStepTypeParameterKey(key)
                ? WorkflowPrimitiveCatalog.ToCanonicalType(evaluated)
                : evaluated;
        }

        if (step.Branches is { Count: > 0 })
        {
            foreach (var (branchKey, branchValue) in step.Branches)
                request.Parameters[$"branch.{branchKey}"] = branchValue;
        }

        if (!string.IsNullOrWhiteSpace(step.TargetRole) && _compiledWorkflow != null)
        {
            var role = _compiledWorkflow.Roles.FirstOrDefault(x => string.Equals(x.Id, step.TargetRole, StringComparison.OrdinalIgnoreCase));
            if (role is { Connectors.Count: > 0 })
                request.Parameters["allowed_connectors"] = string.Join(",", role.Connectors);
        }

        return request;
    }

    private Dictionary<string, string> ResolveVariables(string input)
    {
        var variables = State.Variables.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        variables["input"] = input;
        return variables;
    }

    private static WorkflowStepExecutionState BuildExecutionState(
        string stepId,
        string stepType,
        string input,
        string targetRole,
        int attempt,
        string parentStepId,
        IReadOnlyDictionary<string, string> parameters)
    {
        var state = new WorkflowStepExecutionState
        {
            StepId = stepId,
            StepType = stepType,
            Input = input ?? string.Empty,
            TargetRole = targetRole ?? string.Empty,
            Attempt = attempt,
            ParentStepId = parentStepId ?? string.Empty,
        };
        foreach (var (key, value) in parameters)
            state.Parameters[key] = value;
        return state;
    }

    private static bool MatchesSemanticGeneration(EventEnvelope envelope, int expectedGeneration)
    {
        if (expectedGeneration <= 0)
            return false;

        if (envelope.Metadata == null ||
            !envelope.Metadata.TryGetValue(CallbackSemanticGenerationMetadataKey, out var rawGeneration) ||
            !int.TryParse(rawGeneration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actualGeneration))
        {
            return false;
        }

        return actualGeneration == expectedGeneration;
    }

    private bool TryResolvePendingSignalWait(string? waitToken, out WorkflowPendingSignalWaitState pending)
    {
        pending = default!;
        var normalizedToken = (waitToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
            return false;

        foreach (var candidate in State.PendingSignalWaits.Values)
        {
            if (!string.Equals(candidate.WaitToken, normalizedToken, StringComparison.Ordinal))
                continue;

            pending = candidate;
            return true;
        }

        return false;
    }

    private bool TryResolvePendingHumanGate(string? resumeToken, out WorkflowPendingHumanGateState pending)
    {
        pending = default!;
        var normalizedToken = (resumeToken ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
            return false;

        foreach (var candidate in State.PendingHumanGates.Values)
        {
            if (!string.Equals(candidate.ResumeToken, normalizedToken, StringComparison.Ordinal))
                continue;

            pending = candidate;
            return true;
        }

        return false;
    }

    private async Task RemovePendingLlmCallAndPublishFailureAsync(
        string sessionId,
        string stepId,
        string runId,
        string error,
        CancellationToken ct)
    {
        var next = State.Clone();
        next.PendingLlmCalls.Remove(sessionId);
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = stepId,
            RunId = runId,
            Success = false,
            Error = error,
        }, EventDirection.Self, ct);
    }

    private static void ResetRuntimeState(WorkflowRunState state, bool clearChildActors)
    {
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

    private static int NextSemanticGeneration(int current) =>
        current >= int.MaxValue - 1 ? 1 : current + 1;

    private bool TryMatchRunAndStep(string runId, string stepId) =>
        string.Equals(WorkflowRunIdNormalizer.Normalize(runId), State.RunId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(stepId);

    private static string NormalizeSignalName(string signalName) =>
        string.IsNullOrWhiteSpace(signalName) ? "default" : signalName.Trim().ToLowerInvariant();

    private bool EvaluateWhileCondition(WorkflowWhileState state, string output, int nextIteration)
    {
        var variables = BuildIterationVariables(output, nextIteration, state.MaxIterations);
        var evaluation = state.ConditionExpression.Contains("${", StringComparison.Ordinal)
            ? _expressionEvaluator.Evaluate(state.ConditionExpression, variables)
            : _expressionEvaluator.EvaluateExpression(state.ConditionExpression, variables);
        return IsTruthy(evaluation);
    }

    private static Dictionary<string, string> BuildIterationVariables(string input, int iteration, int maxIterations) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = input,
            ["output"] = input,
            ["iteration"] = iteration.ToString(CultureInfo.InvariantCulture),
            ["max_iterations"] = maxIterations.ToString(CultureInfo.InvariantCulture),
        };

    private static bool ShouldDeferWhileParameterEvaluation(string canonicalStepType, string parameterKey) =>
        string.Equals(canonicalStepType, "while", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(parameterKey, "condition", StringComparison.OrdinalIgnoreCase) ||
         parameterKey.StartsWith("sub_param_", StringComparison.OrdinalIgnoreCase));

    private static bool IsTruthy(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (bool.TryParse(value, out var boolValue))
            return boolValue;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return Math.Abs(number) >= 1e-9;
        return true;
    }

    private static bool IsTimeoutError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase);

    private static int ResolveLlmTimeoutMs(IReadOnlyDictionary<string, string> parameters)
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

    private static bool TryExtractLlmFailure(string? content, out string error)
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

    private static double ParseScore(string text)
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

    private static WorkflowRecordedStepResult ToRecordedResult(StepCompletedEvent evt)
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

    private static bool TryGetParallelParent(string stepId, out string parent)
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

    private static string? TryGetForEachParent(string stepId)
    {
        var marker = "_item_";
        var index = stepId.LastIndexOf(marker, StringComparison.Ordinal);
        if (index <= 0)
            return null;

        var suffix = stepId[(index + marker.Length)..];
        return suffix.All(char.IsDigit) ? stepId[..index] : null;
    }

    private static string? TryGetMapReduceParent(string stepId)
    {
        var index = stepId.LastIndexOf("_map_", StringComparison.Ordinal);
        return index > 0 ? stepId[..index] : null;
    }

    private static string? TryGetRaceParent(string stepId)
    {
        var index = stepId.LastIndexOf("_race_", StringComparison.Ordinal);
        return index > 0 ? stepId[..index] : null;
    }

    private static string? TryGetWhileParent(string stepId)
    {
        var index = stepId.LastIndexOf("_iter_", StringComparison.Ordinal);
        if (index <= 0)
            return null;

        var suffix = stepId[(index + "_iter_".Length)..];
        return suffix.All(char.IsDigit) ? stepId[..index] : null;
    }

    private static string ShortenKey(string key) =>
        key.Length > 60 ? key[..60] + "..." : key;

    private static string BuildStepTimeoutCallbackId(string runId, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-step-timeout", runId, stepId);

    private static string BuildRetryBackoffCallbackId(string runId, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("workflow-step-retry-backoff", runId, stepId);

    private static string BuildDelayCallbackId(string runId, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("delay-step", runId, stepId);

    private static string BuildWaitSignalCallbackId(string runId, string signalName, string stepId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("wait-signal-timeout", runId, signalName, stepId);

    private static string BuildLlmWatchdogCallbackId(string sessionId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("llm-watchdog", sessionId);

    private async Task<bool> TryHandleRegisteredPrimitiveAsync(StepRequestEvent request, CancellationToken ct)
    {
        if (Services == null ||
            !_primitiveRegistry.TryCreate(request.StepType, Services, out var handler) ||
            handler == null)
        {
            return false;
        }

        try
        {
            await handler.HandleAsync(
                request,
                new WorkflowPrimitiveExecutionContext(
                    Id,
                    Services,
                    Logger,
                    new HashSet<string>(_knownStepTypes, StringComparer.OrdinalIgnoreCase),
                    new PrimitiveEventSink(this)),
                ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Workflow primitive {PrimitiveName} failed", handler.Name);
            await PublishAsync(new StepCompletedEvent
            {
                StepId = request.StepId,
                RunId = request.RunId,
                Success = false,
                Error = $"primitive '{handler.Name}' failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }

        return true;
    }

    private string BuildSubWorkflowRunActorId(string workflowName, string lifecycle, string invocationId)
    {
        var workflowSegment = SanitizeActorSegment(workflowName);
        var lifecycleSegment = SanitizeActorSegment(lifecycle);
        return $"{Id}:workflow:{workflowSegment}:{lifecycleSegment}:{invocationId}";
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

    private sealed class PrimitiveEventSink(WorkflowRunGAgent owner) : WorkflowPrimitiveExecutionContext.IWorkflowPrimitiveEventSink
    {
        public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction, CancellationToken ct)
            where TEvent : IMessage =>
            owner.PublishAsync(evt, direction, ct);
    }

    private string BuildChildActorId(string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            throw new InvalidOperationException("Role id is required to create child actor.");
        return $"{Id}:{roleId.Trim()}";
    }
}
