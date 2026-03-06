using System.Globalization;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowRunGAgent : GAgentBase<WorkflowRunState>
{
    private const string StatusIdle = "idle";
    private const string StatusActive = "active";
    private const string StatusSuspended = "suspended";
    private const string StatusCompleted = "completed";
    private const string StatusFailed = "failed";

    private const string CallbackSemanticGenerationMetadataKey = "workflow.semantic_generation";
    private const string CallbackRunIdMetadataKey = "workflow.run_id";
    private const string CallbackStepIdMetadataKey = "workflow.step_id";
    private const string CallbackSessionIdMetadataKey = "workflow.session_id";
    private const string CallbackKindMetadataKey = "workflow.callback_kind";

    private static readonly IReadOnlySet<string> StateOwnedModuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "workflow_loop",
        "wait_signal",
        "delay",
        "human_input",
        "human_approval",
        "llm_call",
        "evaluate",
        "reflect",
        "parallel_fanout",
        "foreach",
        "map_reduce",
        "race",
        "while",
        "cache",
        "workflow_call",
    };

    private readonly IActorRuntime _runtime;
    private readonly IRoleAgentTypeResolver _roleAgentTypeResolver;
    private readonly IWorkflowDefinitionResolver? _workflowDefinitionResolver;
    private readonly IReadOnlyList<IWorkflowModulePack> _modulePacks;
    private readonly WorkflowParser _parser = new();
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();
    private readonly ISet<string> _knownStepTypes;

    private WorkflowDefinition? _compiledWorkflow;

    public WorkflowRunGAgent(
        IActorRuntime runtime,
        IRoleAgentTypeResolver roleAgentTypeResolver,
        IEnumerable<IWorkflowModulePack> modulePacks,
        IWorkflowDefinitionResolver? workflowDefinitionResolver = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _roleAgentTypeResolver = roleAgentTypeResolver ?? throw new ArgumentNullException(nameof(roleAgentTypeResolver));
        _workflowDefinitionResolver = workflowDefinitionResolver;

        _modulePacks = (modulePacks ?? throw new ArgumentNullException(nameof(modulePacks))).ToList();
        if (_modulePacks.Count == 0)
            _modulePacks = [new WorkflowCoreModulePack()];

        _knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            _modulePacks
                .SelectMany(x => x.Modules)
                .SelectMany(x => x.Names));
        _knownStepTypes.UnionWith(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);
    }

    public override Task<string> GetDescriptionAsync()
    {
        var status = string.IsNullOrWhiteSpace(State.Status) ? StatusIdle : State.Status;
        var workflowName = string.IsNullOrWhiteSpace(State.WorkflowName) ? "(unbound)" : State.WorkflowName;
        return Task.FromResult($"WorkflowRunGAgent[{workflowName}] run={State.RunId} status={status}");
    }

    protected override Task OnStateChangedAsync(WorkflowRunState state, CancellationToken ct)
    {
        _ = ct;
        RebuildCompiledWorkflowCache();
        return Task.CompletedTask;
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        RebuildCompiledWorkflowCache();
        InstallStatelessPrimitiveModules();

        if (!string.IsNullOrWhiteSpace(State.Status) &&
            !string.Equals(State.Status, StatusIdle, StringComparison.OrdinalIgnoreCase) &&
            _compiledWorkflow != null)
        {
            await EnsureAgentTreeAsync(ct);
            await RepublishSuspendedFactsAsync(ct);
        }
    }

    protected override WorkflowRunState TransitionState(WorkflowRunState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowRunStateUpdatedEvent>((_, updated) => updated.State.Clone())
            .OrCurrent();

    public WorkflowRunBindingSnapshot GetBindingSnapshot() =>
        new(
            string.IsNullOrWhiteSpace(State.WorkflowName) ? string.Empty : State.WorkflowName.Trim(),
            State.WorkflowYaml ?? string.Empty,
            State.InlineWorkflowYamls.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase));

    public async Task BindWorkflowDefinitionAsync(
        string workflowYaml,
        string? workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        CancellationToken ct = default)
    {
        EnsureWorkflowNameCanBind(workflowName);

        var next = State.Clone();
        next.WorkflowYaml = workflowYaml ?? string.Empty;
        next.WorkflowName = string.IsNullOrWhiteSpace(workflowName)
            ? next.WorkflowName
            : workflowName.Trim();
        next.InlineWorkflowYamls.Clear();
        if (inlineWorkflowYamls != null)
        {
            foreach (var (name, yaml) in inlineWorkflowYamls)
            {
                var normalizedName = WorkflowRunIdNormalizer.NormalizeWorkflowName(name);
                if (string.IsNullOrWhiteSpace(normalizedName) || string.IsNullOrWhiteSpace(yaml))
                    continue;

                next.InlineWorkflowYamls[normalizedName] = yaml;
            }
        }

        ResetRuntimeState(next, clearChildActors: true);

        var compileResult = EvaluateWorkflowCompilation(next.WorkflowYaml);
        next.Compiled = compileResult.Compiled;
        next.CompilationError = compileResult.CompilationError;
        if (compileResult.Compiled && compileResult.Workflow != null && string.IsNullOrWhiteSpace(next.WorkflowName))
            next.WorkflowName = compileResult.Workflow.Name;

        await PersistStateAsync(next, ct);
        InstallStatelessPrimitiveModules();
    }

    [EventHandler]
    public Task HandleBindWorkflowDefinition(BindWorkflowDefinitionEvent request) =>
        BindWorkflowDefinitionAsync(request.WorkflowYaml, request.WorkflowName, request.InlineWorkflowYamls);

    [EventHandler]
    public async Task HandleChatRequest(ChatRequestEvent request)
    {
        if (_compiledWorkflow == null || !State.Compiled)
        {
            await PublishAsync(new ChatResponseEvent
            {
                SessionId = request.SessionId,
                Content = "Workflow is not compiled or definition-bound.",
            }, EventDirection.Up);
            return;
        }

        if (!string.IsNullOrWhiteSpace(State.RunId))
        {
            await PublishAsync(new ChatResponseEvent
            {
                SessionId = request.SessionId,
                Content = "WorkflowRunGAgent accepts only a single workflow run.",
            }, EventDirection.Up);
            return;
        }

        await EnsureAgentTreeAsync(CancellationToken.None);

        var runId = WorkflowRunIdNormalizer.Normalize(
            string.IsNullOrWhiteSpace(request.SessionId)
                ? Guid.NewGuid().ToString("N")
                : request.SessionId);
        var input = request.Prompt ?? string.Empty;

        var next = State.Clone();
        ResetRuntimeState(next, clearChildActors: false);
        next.RunId = runId;
        next.Status = StatusActive;
        next.Variables["input"] = input;
        await PersistStateAsync(next, CancellationToken.None);

        await PublishAsync(new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            Input = input,
            RunId = runId,
        }, EventDirection.Self, CancellationToken.None);

        var entry = _compiledWorkflow.Steps.FirstOrDefault();
        if (entry == null)
        {
            await FinalizeRunAsync(false, string.Empty, "workflow has no steps", CancellationToken.None);
            return;
        }

        await DispatchWorkflowStepAsync(entry, input, runId, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true, Priority = 1)]
    public async Task HandleStepRequest(StepRequestEvent request)
    {
        var stepType = WorkflowPrimitiveCatalog.ToCanonicalType(request.StepType);
        switch (stepType)
        {
            case "delay":
                await HandleDelayStepRequestAsync(request, CancellationToken.None);
                return;
            case "wait_signal":
                await HandleWaitSignalStepRequestAsync(request, CancellationToken.None);
                return;
            case "human_input":
            case "human_approval":
                await HandleHumanGateStepRequestAsync(request, stepType, CancellationToken.None);
                return;
            case "llm_call":
                await HandleLlmCallStepRequestAsync(request, CancellationToken.None);
                return;
            case "evaluate":
                await HandleEvaluateStepRequestAsync(request, CancellationToken.None);
                return;
            case "reflect":
                await HandleReflectStepRequestAsync(request, CancellationToken.None);
                return;
            case "parallel":
                await HandleParallelStepRequestAsync(request, CancellationToken.None);
                return;
            case "foreach":
                await HandleForEachStepRequestAsync(request, CancellationToken.None);
                return;
            case "map_reduce":
                await HandleMapReduceStepRequestAsync(request, CancellationToken.None);
                return;
            case "race":
                await HandleRaceStepRequestAsync(request, CancellationToken.None);
                return;
            case "while":
                await HandleWhileStepRequestAsync(request, CancellationToken.None);
                return;
            case "cache":
                await HandleCacheStepRequestAsync(request, CancellationToken.None);
                return;
            case "workflow_call":
                await HandleWorkflowCallStepRequestAsync(request, CancellationToken.None);
                return;
            default:
                return;
        }
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true, Priority = 2)]
    public async Task HandleStepCompleted(StepCompletedEvent evt)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
        if (!string.Equals(runId, State.RunId, StringComparison.Ordinal))
            return;

        if (await TryHandleStatefulChildCompletionAsync(evt, CancellationToken.None))
            return;

        if (!string.Equals(State.ActiveStepId, evt.StepId, StringComparison.Ordinal))
        {
            Logger.LogDebug(
                "WorkflowRunGAgent ignore stale completion run={RunId} step={StepId} active={ActiveStepId}",
                runId,
                evt.StepId,
                State.ActiveStepId ?? "(none)");
            return;
        }

        var currentStep = _compiledWorkflow?.GetStep(evt.StepId);
        if (currentStep == null)
        {
            Logger.LogWarning(
                "WorkflowRunGAgent active step definition not found run={RunId} step={StepId}",
                runId,
                evt.StepId);
            return;
        }

        var next = State.Clone();
        next.PendingTimeouts.Remove(evt.StepId);
        next.PendingRetryBackoffs.Remove(evt.StepId);

        if (!evt.Success && next.PendingRetryBackoffs.ContainsKey(evt.StepId))
            return;

        if (next.Variables.TryGetValue("input", out _))
            next.Variables["input"] = evt.Output ?? string.Empty;
        else
            next.Variables.Add("input", evt.Output ?? string.Empty);
        next.Variables[evt.StepId] = evt.Output ?? string.Empty;

        if (evt.Metadata.TryGetValue("assign.target", out var assignTarget) &&
            !string.IsNullOrWhiteSpace(assignTarget))
        {
            var assignValue = evt.Metadata.TryGetValue("assign.value", out var assignValueFromMetadata)
                ? assignValueFromMetadata
                : evt.Output ?? string.Empty;
            next.Variables[assignTarget] = assignValue;
        }

        if (!evt.Success)
        {
            if (await TryScheduleRetryAsync(currentStep, evt, next, CancellationToken.None))
                return;

            if (await TryHandleOnErrorAsync(currentStep, evt, next, CancellationToken.None))
                return;

            next.StepExecutions.Remove(evt.StepId);
            next.RetryAttemptsByStepId.Remove(evt.StepId);
            next.ActiveStepId = string.Empty;
            next.Status = StatusFailed;
            next.FinalError = evt.Error ?? "workflow step failed";
            await PersistStateAsync(next, CancellationToken.None);
            await PublishFinalWorkflowCompletedAsync(false, evt.Output ?? string.Empty, evt.Error ?? "workflow step failed", CancellationToken.None);
            return;
        }

        next.StepExecutions.Remove(evt.StepId);
        next.RetryAttemptsByStepId.Remove(evt.StepId);

        StepDefinition? nextStep;
        if (evt.Metadata.TryGetValue("next_step", out var directNextStepId) &&
            !string.IsNullOrWhiteSpace(directNextStepId))
        {
            nextStep = _compiledWorkflow!.GetStep(directNextStepId);
            if (nextStep == null)
            {
                next.ActiveStepId = string.Empty;
                next.Status = StatusFailed;
                next.FinalError = $"invalid next_step '{directNextStepId}' from step '{currentStep.Id}'";
                await PersistStateAsync(next, CancellationToken.None);
                await PublishFinalWorkflowCompletedAsync(false, string.Empty, next.FinalError, CancellationToken.None);
                return;
            }
        }
        else
        {
            var branchKey = evt.Metadata.TryGetValue("branch", out var branch) ? branch : null;
            nextStep = _compiledWorkflow!.GetNextStep(currentStep.Id, branchKey);
        }

        if (nextStep == null)
        {
            next.ActiveStepId = string.Empty;
            next.Status = StatusCompleted;
            next.FinalOutput = evt.Output ?? string.Empty;
            await PersistStateAsync(next, CancellationToken.None);
            await PublishFinalWorkflowCompletedAsync(true, evt.Output ?? string.Empty, string.Empty, CancellationToken.None);
            return;
        }

        await PersistStateAsync(next, CancellationToken.None);
        await DispatchWorkflowStepAsync(nextStep, evt.Output ?? string.Empty, runId, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleWorkflowResumed(WorkflowResumedEvent resumed)
    {
        if (!string.Equals(WorkflowRunIdNormalizer.Normalize(resumed.RunId), State.RunId, StringComparison.Ordinal))
            return;

        if (!TryResolvePendingHumanGate(resumed.ResumeToken, out var pending))
            return;

        var next = State.Clone();
        next.PendingHumanGates.Remove(pending.StepId);
        next.Status = StatusActive;
        await PersistStateAsync(next, CancellationToken.None);

        if (string.Equals(pending.GateType, "human_input", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(resumed.UserInput) && !resumed.Approved)
            {
                var onTimeout = string.IsNullOrWhiteSpace(pending.OnTimeout) ? "fail" : pending.OnTimeout;
                await PublishAsync(new StepCompletedEvent
                {
                    StepId = pending.StepId,
                    RunId = State.RunId,
                    Success = !string.Equals(onTimeout, "fail", StringComparison.OrdinalIgnoreCase),
                    Output = pending.Input,
                    Error = string.Equals(onTimeout, "fail", StringComparison.OrdinalIgnoreCase)
                        ? "Human input timed out"
                        : string.Empty,
                }, EventDirection.Self, CancellationToken.None);
                return;
            }

            await PublishAsync(new StepCompletedEvent
            {
                StepId = pending.StepId,
                RunId = State.RunId,
                Success = true,
                Output = resumed.UserInput ?? string.Empty,
            }, EventDirection.Self, CancellationToken.None);
            return;
        }

        var rejected = !resumed.Approved;
        var rejectionOutput = !string.IsNullOrEmpty(resumed.UserInput)
            ? $"[Previous content]\n{pending.Input}\n\n[User feedback]\n{resumed.UserInput}"
            : pending.Input;

        var completed = new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = State.RunId,
            Success = !rejected || !string.Equals(pending.OnReject, "fail", StringComparison.OrdinalIgnoreCase),
            Output = rejected ? rejectionOutput : string.IsNullOrEmpty(resumed.UserInput) ? pending.Input : resumed.UserInput,
            Error = rejected && string.Equals(pending.OnReject, "fail", StringComparison.OrdinalIgnoreCase)
                ? "Human approval rejected"
                : string.Empty,
        };
        completed.Metadata["branch"] = resumed.Approved ? "true" : "false";
        await PublishAsync(completed, EventDirection.Self, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleSignalReceived(SignalReceivedEvent signal)
    {
        if (!string.Equals(WorkflowRunIdNormalizer.Normalize(signal.RunId), State.RunId, StringComparison.Ordinal))
            return;

        if (!TryResolvePendingSignalWait(signal.WaitToken, out var pending))
            return;

        var next = State.Clone();
        next.PendingSignalWaits.Remove(pending.StepId);
        next.Status = StatusActive;
        await PersistStateAsync(next, CancellationToken.None);

        await PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = State.RunId,
            Success = true,
            Output = string.IsNullOrEmpty(signal.Payload) ? pending.Input : signal.Payload,
        }, EventDirection.Self, CancellationToken.None);
    }

    [AllEventHandler(Priority = 5, AllowSelfHandling = true)]
    public async Task HandleRuntimeCallbackEnvelope(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor))
        {
            await HandleWorkflowStepTimeoutFiredAsync(
                payload.Unpack<WorkflowStepTimeoutFiredEvent>(),
                envelope,
                CancellationToken.None);
            return;
        }

        if (payload.Is(WorkflowStepRetryBackoffFiredEvent.Descriptor))
        {
            await HandleWorkflowStepRetryBackoffFiredAsync(
                payload.Unpack<WorkflowStepRetryBackoffFiredEvent>(),
                envelope,
                CancellationToken.None);
            return;
        }

        if (payload.Is(DelayStepTimeoutFiredEvent.Descriptor))
        {
            await HandleDelayStepTimeoutFiredAsync(
                payload.Unpack<DelayStepTimeoutFiredEvent>(),
                envelope,
                CancellationToken.None);
            return;
        }

        if (payload.Is(WaitSignalTimeoutFiredEvent.Descriptor))
        {
            await HandleWaitSignalTimeoutFiredAsync(
                payload.Unpack<WaitSignalTimeoutFiredEvent>(),
                envelope,
                CancellationToken.None);
            return;
        }

        if (payload.Is(LlmCallWatchdogTimeoutFiredEvent.Descriptor))
        {
            await HandleLlmCallWatchdogTimeoutFiredAsync(
                payload.Unpack<LlmCallWatchdogTimeoutFiredEvent>(),
                envelope,
                CancellationToken.None);
        }
    }

    [AllEventHandler(Priority = 40, AllowSelfHandling = true)]
    public async Task HandleCompletionEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) != true)
            return;

        if (string.Equals(envelope.PublisherId, Id, StringComparison.Ordinal))
            return;

        var completed = envelope.Payload.Unpack<WorkflowCompletedEvent>();
        await TryHandleSubWorkflowCompletionAsync(completed, envelope.PublisherId, CancellationToken.None);
    }

    [AllEventHandler(Priority = 30, AllowSelfHandling = true)]
    public async Task HandleRoleAndPromptResponseEnvelope(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return;

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            await HandleLlmLikeResponseAsync(
                evt.SessionId,
                evt.Content ?? string.Empty,
                envelope.PublisherId,
                CancellationToken.None);
            return;
        }

        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            await HandleLlmLikeResponseAsync(
                evt.SessionId,
                evt.Content ?? string.Empty,
                string.IsNullOrWhiteSpace(envelope.PublisherId) ? Id : envelope.PublisherId,
                CancellationToken.None);
        }
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleReplaceWorkflowDefinitionAndExecute(ReplaceWorkflowDefinitionAndExecuteEvent request)
    {
        var yaml = request.WorkflowYaml ?? string.Empty;
        if (string.IsNullOrWhiteSpace(yaml))
        {
            await PublishAsync(new ChatResponseEvent
            {
                Content = "Dynamic workflow YAML is empty.",
            }, EventDirection.Up);
            return;
        }

        WorkflowDefinition parsed;
        try
        {
            parsed = _parser.Parse(yaml);
        }
        catch (Exception ex)
        {
            await PublishAsync(new ChatResponseEvent
            {
                Content = $"Dynamic workflow YAML compilation failed: {ex.Message}",
            }, EventDirection.Up);
            return;
        }

        var validationErrors = ValidateWorkflowDefinition(parsed);
        if (validationErrors.Count > 0)
        {
            await PublishAsync(new ChatResponseEvent
            {
                Content = $"Dynamic workflow YAML compilation failed: {string.Join("; ", validationErrors)}",
            }, EventDirection.Up);
            return;
        }

        var runId = string.IsNullOrWhiteSpace(State.RunId) ? Guid.NewGuid().ToString("N") : State.RunId;
        var input = request.Input ?? string.Empty;
        var next = State.Clone();
        next.WorkflowName = parsed.Name;
        next.WorkflowYaml = yaml;
        next.Compiled = true;
        next.CompilationError = string.Empty;
        next.InlineWorkflowYamls.Clear();
        ResetRuntimeState(next, clearChildActors: true);
        next.RunId = runId;
        next.Status = StatusActive;
        next.Variables["input"] = input;
        await PersistStateAsync(next, CancellationToken.None);
        InstallStatelessPrimitiveModules();
        await EnsureAgentTreeAsync(CancellationToken.None);
        await PublishAsync(new StartWorkflowEvent
        {
            WorkflowName = parsed.Name,
            Input = input,
            RunId = runId,
        }, EventDirection.Self, CancellationToken.None);
        var entry = parsed.Steps.FirstOrDefault();
        if (entry == null)
        {
            await FinalizeRunAsync(false, string.Empty, "workflow has no steps", CancellationToken.None);
            return;
        }

        await DispatchWorkflowStepAsync(entry, input, runId, CancellationToken.None);
    }

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

    private async Task<bool> TryHandleStatefulChildCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        if (await TryHandleParallelCompletionAsync(evt, ct))
            return true;
        if (await TryHandleForEachCompletionAsync(evt, ct))
            return true;
        if (await TryHandleMapReduceCompletionAsync(evt, ct))
            return true;
        if (await TryHandleRaceCompletionAsync(evt, ct))
            return true;
        if (await TryHandleWhileCompletionAsync(evt, ct))
            return true;
        if (await TryHandleCacheCompletionAsync(evt, ct))
            return true;
        return false;
    }

    private async Task<bool> TryHandleParallelCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        foreach (var (parentStepId, pending) in State.PendingParallelSteps)
        {
            if (!string.IsNullOrWhiteSpace(pending.VoteStepId) &&
                string.Equals(pending.VoteStepId, evt.StepId, StringComparison.Ordinal))
            {
                var voteNextState = State.Clone();
                voteNextState.PendingParallelSteps.Remove(parentStepId);
                voteNextState.StepExecutions.Remove(evt.StepId);
                await PersistStateAsync(voteNextState, ct);

                var voteCompleted = new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = State.RunId,
                    Success = pending.WorkersSuccess && evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                    WorkerId = evt.WorkerId,
                };
                foreach (var (key, value) in evt.Metadata)
                    voteCompleted.Metadata[key] = value;
                voteCompleted.Metadata["parallel.used_vote"] = "true";
                voteCompleted.Metadata["parallel.vote_step_id"] = evt.StepId;
                voteCompleted.Metadata["parallel.workers_success"] = pending.WorkersSuccess.ToString();
                await PublishAsync(voteCompleted, EventDirection.Self, ct);
                return true;
            }

            if (!TryGetParallelParent(evt.StepId, out var completionParent) ||
                !string.Equals(completionParent, parentStepId, StringComparison.Ordinal))
            {
                continue;
            }

            var next = State.Clone();
            next.StepExecutions.Remove(evt.StepId);
            next.PendingParallelSteps[parentStepId].ChildResults.Add(ToRecordedResult(evt));
            var collected = next.PendingParallelSteps[parentStepId].ChildResults.Count;
            if (collected < next.PendingParallelSteps[parentStepId].ExpectedCount)
            {
                await PersistStateAsync(next, ct);
                return true;
            }

            var results = next.PendingParallelSteps[parentStepId].ChildResults.ToList();
            var allSuccess = results.All(x => x.Success);
            var merged = string.Join("\n---\n", results.Select(x => x.Output));
            if (!string.IsNullOrWhiteSpace(next.PendingParallelSteps[parentStepId].VoteStepType))
            {
                var voteStepId = $"{parentStepId}_vote";
                next.PendingParallelSteps[parentStepId].VoteStepId = voteStepId;
                next.PendingParallelSteps[parentStepId].WorkersSuccess = allSuccess;
                await PersistStateAsync(next, ct);
                await DispatchInternalStepAsync(
                    State.RunId,
                    parentStepId,
                    voteStepId,
                    next.PendingParallelSteps[parentStepId].VoteStepType,
                    merged,
                    string.Empty,
                    next.PendingParallelSteps[parentStepId].VoteParameters.ToDictionary(x => x.Key, x => x.Value),
                    ct);
                return true;
            }

            next.PendingParallelSteps.Remove(parentStepId);
            await PersistStateAsync(next, ct);
            var completed = new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = State.RunId,
                Success = allSuccess,
                Output = merged,
            };
            completed.Metadata["parallel.used_vote"] = "false";
            await PublishAsync(completed, EventDirection.Self, ct);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleForEachCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var parentStepId = TryGetForEachParent(evt.StepId);
        if (parentStepId == null || !State.PendingForeachSteps.TryGetValue(parentStepId, out _))
            return false;

        var next = State.Clone();
        next.StepExecutions.Remove(evt.StepId);
        next.PendingForeachSteps[parentStepId].ChildResults.Add(ToRecordedResult(evt));
        if (next.PendingForeachSteps[parentStepId].ChildResults.Count < next.PendingForeachSteps[parentStepId].ExpectedCount)
        {
            await PersistStateAsync(next, ct);
            return true;
        }

        var results = next.PendingForeachSteps[parentStepId].ChildResults.ToList();
        next.PendingForeachSteps.Remove(parentStepId);
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = parentStepId,
            RunId = State.RunId,
            Success = results.All(x => x.Success),
            Output = string.Join("\n---\n", results.Select(x => x.Output)),
        }, EventDirection.Self, ct);
        return true;
    }

    private async Task<bool> TryHandleMapReduceCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        foreach (var (parentStepId, pending) in State.PendingMapReduceSteps)
        {
            if (!string.IsNullOrWhiteSpace(pending.ReduceStepId) &&
                string.Equals(pending.ReduceStepId, evt.StepId, StringComparison.Ordinal))
            {
                var reduceNextState = State.Clone();
                reduceNextState.PendingMapReduceSteps.Remove(parentStepId);
                reduceNextState.StepExecutions.Remove(evt.StepId);
                await PersistStateAsync(reduceNextState, ct);

                var reduceCompleted = new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = State.RunId,
                    Success = evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                };
                reduceCompleted.Metadata["map_reduce.phase"] = "reduce";
                await PublishAsync(reduceCompleted, EventDirection.Self, ct);
                return true;
            }

            var mapParent = TryGetMapReduceParent(evt.StepId);
            if (!string.Equals(mapParent, parentStepId, StringComparison.Ordinal))
                continue;

            var next = State.Clone();
            next.StepExecutions.Remove(evt.StepId);
            next.PendingMapReduceSteps[parentStepId].ChildResults.Add(ToRecordedResult(evt));
            if (next.PendingMapReduceSteps[parentStepId].ChildResults.Count < next.PendingMapReduceSteps[parentStepId].MapCount)
            {
                await PersistStateAsync(next, ct);
                return true;
            }

            var results = next.PendingMapReduceSteps[parentStepId].ChildResults.ToList();
            var allSuccess = results.All(x => x.Success);
            var merged = string.Join("\n---\n", results.Select(x => x.Output));
            if (!allSuccess || string.IsNullOrWhiteSpace(next.PendingMapReduceSteps[parentStepId].ReduceType))
            {
                next.PendingMapReduceSteps.Remove(parentStepId);
                await PersistStateAsync(next, ct);
                await PublishAsync(new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = State.RunId,
                    Success = allSuccess,
                    Output = merged,
                    Error = allSuccess ? string.Empty : "one or more map steps failed",
                }, EventDirection.Self, ct);
                return true;
            }

            var reduceInput = string.IsNullOrEmpty(next.PendingMapReduceSteps[parentStepId].ReducePromptPrefix)
                ? merged
                : next.PendingMapReduceSteps[parentStepId].ReducePromptPrefix.TrimEnd() + "\n\n" + merged;
            var reduceStepId = $"{parentStepId}_reduce";
            next.PendingMapReduceSteps[parentStepId].ReduceStepId = reduceStepId;
            await PersistStateAsync(next, ct);
            await DispatchInternalStepAsync(
                State.RunId,
                parentStepId,
                reduceStepId,
                next.PendingMapReduceSteps[parentStepId].ReduceType,
                reduceInput,
                next.PendingMapReduceSteps[parentStepId].ReduceRole,
                new Dictionary<string, string>(StringComparer.Ordinal),
                ct);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleRaceCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var parentStepId = TryGetRaceParent(evt.StepId);
        if (parentStepId == null || !State.PendingRaceSteps.TryGetValue(parentStepId, out var pending))
            return false;

        var next = State.Clone();
        next.StepExecutions.Remove(evt.StepId);
        next.PendingRaceSteps[parentStepId].Received = pending.Received + 1;
        if (evt.Success && !pending.Resolved)
        {
            next.PendingRaceSteps.Remove(parentStepId);
            await PersistStateAsync(next, ct);
            var completed = new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = State.RunId,
                Success = true,
                Output = evt.Output,
                WorkerId = evt.WorkerId,
            };
            completed.Metadata["race.winner"] = evt.StepId;
            await PublishAsync(completed, EventDirection.Self, ct);
            return true;
        }

        if (next.PendingRaceSteps[parentStepId].Received >= pending.Total)
        {
            next.PendingRaceSteps.Remove(parentStepId);
            await PersistStateAsync(next, ct);
            if (!pending.Resolved)
            {
                await PublishAsync(new StepCompletedEvent
                {
                    StepId = parentStepId,
                    RunId = State.RunId,
                    Success = false,
                    Error = "all race branches failed",
                }, EventDirection.Self, ct);
            }

            return true;
        }

        next.PendingRaceSteps[parentStepId].Resolved = pending.Resolved || evt.Success;
        await PersistStateAsync(next, ct);
        return true;
    }

    private async Task<bool> TryHandleWhileCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        var parentStepId = TryGetWhileParent(evt.StepId);
        if (parentStepId == null || !State.PendingWhileSteps.TryGetValue(parentStepId, out var pending))
            return false;

        var next = State.Clone();
        next.StepExecutions.Remove(evt.StepId);
        if (!evt.Success)
        {
            next.PendingWhileSteps.Remove(parentStepId);
            await PersistStateAsync(next, ct);
            await PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = State.RunId,
                Success = false,
                Output = evt.Output,
                Error = evt.Error,
            }, EventDirection.Self, ct);
            return true;
        }

        var nextIteration = pending.Iteration + 1;
        if (nextIteration < pending.MaxIterations &&
            EvaluateWhileCondition(pending, evt.Output ?? string.Empty, nextIteration))
        {
            next.PendingWhileSteps[parentStepId].Iteration = nextIteration;
            await PersistStateAsync(next, ct);
            await DispatchWhileIterationAsync(next.PendingWhileSteps[parentStepId], evt.Output ?? string.Empty, ct);
            return true;
        }

        next.PendingWhileSteps.Remove(parentStepId);
        await PersistStateAsync(next, ct);
        var completed = new StepCompletedEvent
        {
            StepId = parentStepId,
            RunId = State.RunId,
            Success = true,
            Output = evt.Output,
        };
        completed.Metadata["while.iterations"] = nextIteration.ToString(CultureInfo.InvariantCulture);
        completed.Metadata["while.max_iterations"] = pending.MaxIterations.ToString(CultureInfo.InvariantCulture);
        completed.Metadata["while.condition"] = pending.ConditionExpression;
        await PublishAsync(completed, EventDirection.Self, ct);
        return true;
    }

    private async Task<bool> TryHandleCacheCompletionAsync(StepCompletedEvent evt, CancellationToken ct)
    {
        foreach (var (cacheKey, pending) in State.PendingCacheCalls)
        {
            if (!string.Equals(pending.ChildStepId, evt.StepId, StringComparison.Ordinal))
                continue;

            var next = State.Clone();
            next.PendingCacheCalls.Remove(cacheKey);
            next.StepExecutions.Remove(evt.StepId);
            if (evt.Success)
            {
                next.CacheEntries[cacheKey] = new WorkflowCacheEntry
                {
                    Value = evt.Output ?? string.Empty,
                    ExpiresAtUnixTimeMs = DateTimeOffset.UtcNow.AddSeconds(pending.TtlSeconds).ToUnixTimeMilliseconds(),
                };
            }
            await PersistStateAsync(next, ct);

            foreach (var waiter in pending.Waiters)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = waiter.ParentStepId,
                    RunId = State.RunId,
                    Success = evt.Success,
                    Output = evt.Output,
                    Error = evt.Error,
                };
                completed.Metadata["cache.hit"] = "false";
                completed.Metadata["cache.key"] = ShortenKey(cacheKey);
                await PublishAsync(completed, EventDirection.Self, ct);
            }

            return true;
        }

        return false;
    }

    private async Task HandleWorkflowStepTimeoutFiredAsync(
        WorkflowStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!TryMatchRunAndStep(evt.RunId, evt.StepId))
            return;
        if (!State.PendingTimeouts.TryGetValue(evt.StepId, out var pending))
            return;
        if (!MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var next = State.Clone();
        next.PendingTimeouts.Remove(evt.StepId);
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = State.RunId,
            Success = false,
            Error = $"TIMEOUT after {evt.TimeoutMs}ms",
        }, EventDirection.Self, ct);
    }

    private async Task HandleWorkflowStepRetryBackoffFiredAsync(
        WorkflowStepRetryBackoffFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!TryMatchRunAndStep(evt.RunId, evt.StepId))
            return;
        if (!State.PendingRetryBackoffs.TryGetValue(evt.StepId, out var pending))
            return;
        if (!MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var step = _compiledWorkflow?.GetStep(evt.StepId);
        if (step == null || !State.StepExecutions.TryGetValue(evt.StepId, out var execution))
            return;

        var next = State.Clone();
        next.PendingRetryBackoffs.Remove(evt.StepId);
        await PersistStateAsync(next, ct);
        await DispatchWorkflowStepAsync(step, execution.Input ?? string.Empty, State.RunId, ct);
    }

    private async Task HandleDelayStepTimeoutFiredAsync(
        DelayStepTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!TryMatchRunAndStep(evt.RunId, evt.StepId))
            return;
        if (!State.PendingDelays.TryGetValue(evt.StepId, out var pending))
            return;
        if (!MatchesSemanticGeneration(envelope, pending.SemanticGeneration))
            return;

        var next = State.Clone();
        next.PendingDelays.Remove(evt.StepId);
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = State.RunId,
            Success = true,
            Output = pending.Input,
        }, EventDirection.Self, ct);
    }

    private async Task HandleWaitSignalTimeoutFiredAsync(
        WaitSignalTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!TryMatchRunAndStep(evt.RunId, evt.StepId))
            return;
        if (!State.PendingSignalWaits.TryGetValue(evt.StepId, out var pending))
            return;
        if (!MatchesSemanticGeneration(envelope, pending.TimeoutGeneration))
            return;

        var next = State.Clone();
        next.PendingSignalWaits.Remove(evt.StepId);
        next.Status = StatusActive;
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = evt.StepId,
            RunId = State.RunId,
            Success = false,
            Error = $"signal '{pending.SignalName}' timed out after {evt.TimeoutMs}ms",
        }, EventDirection.Self, ct);
    }

    private async Task HandleLlmCallWatchdogTimeoutFiredAsync(
        LlmCallWatchdogTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!string.Equals(WorkflowRunIdNormalizer.Normalize(evt.RunId), State.RunId, StringComparison.Ordinal))
            return;
        if (string.IsNullOrWhiteSpace(evt.SessionId) || !State.PendingLlmCalls.TryGetValue(evt.SessionId, out var pending))
            return;
        if (!MatchesSemanticGeneration(envelope, pending.WatchdogGeneration))
            return;

        var next = State.Clone();
        next.PendingLlmCalls.Remove(evt.SessionId);
        await PersistStateAsync(next, ct);
        await PublishAsync(new StepCompletedEvent
        {
            StepId = pending.StepId,
            RunId = State.RunId,
            Success = false,
            Error = $"LLM call timed out after {evt.TimeoutMs}ms",
            WorkerId = string.IsNullOrWhiteSpace(pending.TargetRole) ? Id : pending.TargetRole,
        }, EventDirection.Self, ct);
    }

    private async Task HandleLlmLikeResponseAsync(string? sessionId, string content, string publisherId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        if (State.PendingLlmCalls.TryGetValue(sessionId, out var llmPending))
        {
            var next = State.Clone();
            next.PendingLlmCalls.Remove(sessionId);
            await PersistStateAsync(next, ct);

            if (TryExtractLlmFailure(content, out var llmError))
            {
                await PublishAsync(new StepCompletedEvent
                {
                    StepId = llmPending.StepId,
                    RunId = State.RunId,
                    Success = false,
                    Error = llmError,
                    WorkerId = string.IsNullOrWhiteSpace(publisherId) ? Id : publisherId,
                }, EventDirection.Self, ct);
                return;
            }

            await PublishAsync(new StepCompletedEvent
            {
                StepId = llmPending.StepId,
                RunId = State.RunId,
                Success = true,
                Output = content,
                WorkerId = string.IsNullOrWhiteSpace(publisherId) ? Id : publisherId,
            }, EventDirection.Self, ct);
            return;
        }

        if (State.PendingEvaluations.TryGetValue(sessionId, out var evalPending))
        {
            var score = ParseScore(content);
            var passed = score >= evalPending.Threshold;
            var next = State.Clone();
            next.PendingEvaluations.Remove(sessionId);
            await PersistStateAsync(next, ct);

            var completed = new StepCompletedEvent
            {
                StepId = evalPending.StepId,
                RunId = State.RunId,
                Success = true,
                Output = evalPending.OriginalInput,
            };
            completed.Metadata["evaluate.score"] = score.ToString("F1", CultureInfo.InvariantCulture);
            completed.Metadata["evaluate.passed"] = passed.ToString();
            if (!passed && !string.IsNullOrWhiteSpace(evalPending.OnBelow))
                completed.Metadata["branch"] = evalPending.OnBelow;
            await PublishAsync(completed, EventDirection.Self, ct);
            return;
        }

        if (!State.PendingReflections.TryGetValue(sessionId, out var reflectPending))
            return;

        var reflectNext = State.Clone();
        reflectNext.PendingReflections.Remove(sessionId);
        await PersistStateAsync(reflectNext, ct);

        if (string.Equals(reflectPending.Phase, "critique", StringComparison.OrdinalIgnoreCase))
        {
            var passed = content.Contains("PASS", StringComparison.OrdinalIgnoreCase);
            var round = reflectPending.Round + 1;
            if (passed || round >= reflectPending.MaxRounds)
            {
                var completed = new StepCompletedEvent
                {
                    StepId = reflectPending.StepId,
                    RunId = State.RunId,
                    Success = true,
                    Output = reflectPending.CurrentDraft,
                };
                completed.Metadata["reflect.rounds"] = round.ToString(CultureInfo.InvariantCulture);
                completed.Metadata["reflect.passed"] = passed.ToString();
                await PublishAsync(completed, EventDirection.Self, ct);
                return;
            }

            var nextPending = reflectPending.Clone();
            nextPending.Round = round;
            nextPending.Phase = "improve";
            await DispatchReflectPhaseAsync(State.RunId, nextPending, content, ct);
            return;
        }

        var critiquePending = reflectPending.Clone();
        critiquePending.CurrentDraft = content;
        critiquePending.Phase = "critique";
        await DispatchReflectPhaseAsync(State.RunId, critiquePending, content, ct);
    }

    private async Task DispatchReflectPhaseAsync(
        string runId,
        WorkflowPendingReflectState pending,
        string content,
        CancellationToken ct)
    {
        await EnsureAgentTreeAsync(ct);

        var prompt = string.Equals(pending.Phase, "critique", StringComparison.OrdinalIgnoreCase)
            ? $"""
                Review the following content against these criteria: {pending.Criteria}
                If the content meets the criteria, respond with exactly "PASS".
                Otherwise, explain what needs improvement.

                Content:
                {content}
                """
            : $"""
                Improve the following content based on this feedback.

                Feedback:
                {content}

                Original content:
                {pending.CurrentDraft}
                """;

        var sessionId = ChatSessionKeys.CreateWorkflowStepSessionId(
            Id,
            runId,
            $"{pending.StepId}_r{pending.Round}_{pending.Phase}");
        var nextPending = pending.Clone();
        nextPending.SessionId = sessionId;

        var next = State.Clone();
        next.PendingReflections[sessionId] = nextPending;
        await PersistStateAsync(next, ct);

        var request = new ChatRequestEvent
        {
            Prompt = prompt,
            SessionId = sessionId,
        };
        if (!string.IsNullOrWhiteSpace(nextPending.TargetRole))
        {
            await SendToAsync(
                WorkflowRoleActorIdResolver.ResolveTargetActorId(Id, nextPending.TargetRole),
                request,
                ct);
            return;
        }

        await PublishAsync(request, EventDirection.Self, ct);
    }

    private async Task<bool> TryScheduleRetryAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        WorkflowRunState next,
        CancellationToken ct)
    {
        var policy = step.Retry;
        if (policy == null)
            return false;

        if (IsTimeoutError(evt.Error))
            return false;

        var scheduledRetryCount = State.RetryAttemptsByStepId.TryGetValue(step.Id, out var existingRetryCount)
            ? existingRetryCount
            : 0;
        var nextRetryCount = scheduledRetryCount + 1;
        var maxAttempts = Math.Clamp(policy.MaxAttempts, 1, 10);
        if (nextRetryCount >= maxAttempts)
            return false;

        if (!State.StepExecutions.TryGetValue(step.Id, out var execution))
            return false;

        next.RetryAttemptsByStepId[step.Id] = nextRetryCount;
        var delayMs = string.Equals(policy.Backoff, "exponential", StringComparison.OrdinalIgnoreCase)
            ? policy.DelayMs * (1 << (nextRetryCount - 1))
            : policy.DelayMs;
        delayMs = Math.Clamp(delayMs, 0, 60_000);

        if (delayMs <= 0)
        {
            await PersistStateAsync(next, ct);
            await DispatchWorkflowStepAsync(step, execution.Input ?? string.Empty, State.RunId, ct);
            return true;
        }

        next.PendingRetryBackoffs[step.Id] = new WorkflowPendingRetryBackoffState
        {
            StepId = step.Id,
            DelayMs = delayMs,
            NextAttempt = nextRetryCount + 1,
            SemanticGeneration = NextSemanticGeneration(
                State.PendingRetryBackoffs.TryGetValue(step.Id, out var existingBackoff)
                    ? existingBackoff.SemanticGeneration
                    : 0),
        };
        await PersistStateAsync(next, ct);
        await ScheduleWorkflowCallbackAsync(
            BuildRetryBackoffCallbackId(State.RunId, step.Id),
            TimeSpan.FromMilliseconds(delayMs),
            new WorkflowStepRetryBackoffFiredEvent
            {
                RunId = State.RunId,
                StepId = step.Id,
                DelayMs = delayMs,
                NextAttempt = nextRetryCount + 1,
            },
            next.PendingRetryBackoffs[step.Id].SemanticGeneration,
            step.Id,
            sessionId: null,
            kind: "retry_backoff",
            ct);
        return true;
    }

    private async Task<bool> TryHandleOnErrorAsync(
        StepDefinition step,
        StepCompletedEvent evt,
        WorkflowRunState next,
        CancellationToken ct)
    {
        var policy = step.OnError;
        if (policy == null)
            return false;

        switch ((policy.Strategy ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "skip":
            {
                var output = policy.DefaultOutput ?? evt.Output ?? string.Empty;
                next.StepExecutions.Remove(step.Id);
                next.RetryAttemptsByStepId.Remove(step.Id);
                var nextStep = _compiledWorkflow!.GetNextStep(step.Id);
                await PersistStateAsync(next, ct);
                if (nextStep == null)
                {
                    await FinalizeRunAsync(true, output, string.Empty, ct);
                    return true;
                }

                await DispatchWorkflowStepAsync(nextStep, output, State.RunId, ct);
                return true;
            }
            case "fallback" when !string.IsNullOrWhiteSpace(policy.FallbackStep):
            {
                var fallback = _compiledWorkflow!.GetStep(policy.FallbackStep);
                if (fallback == null)
                    return false;

                next.StepExecutions.Remove(step.Id);
                next.RetryAttemptsByStepId.Remove(step.Id);
                await PersistStateAsync(next, ct);
                await DispatchWorkflowStepAsync(fallback, evt.Output ?? string.Empty, State.RunId, ct);
                return true;
            }
            default:
                return false;
        }
    }

    private async Task DispatchWorkflowStepAsync(
        StepDefinition step,
        string input,
        string runId,
        CancellationToken ct)
    {
        var canonicalType = WorkflowPrimitiveCatalog.ToCanonicalType(step.Type);
        if (_compiledWorkflow?.Configuration.ClosedWorldMode == true &&
            WorkflowPrimitiveCatalog.IsClosedWorldBlocked(canonicalType))
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = step.Id,
                RunId = runId,
                Success = false,
                Error = $"step type '{canonicalType}' is blocked in closed_world_mode",
            }, EventDirection.Self, ct);
            return;
        }

        var request = BuildStepRequest(step, input, runId);
        var next = State.Clone();
        next.ActiveStepId = step.Id;
        next.Status = StatusActive;
        next.StepExecutions[step.Id] = BuildExecutionState(
            step.Id,
            canonicalType,
            input,
            request.TargetRole,
            attempt: next.RetryAttemptsByStepId.TryGetValue(step.Id, out var retryAttempt) ? retryAttempt + 1 : 1,
            parentStepId: string.Empty,
            request.Parameters);
        if (step.TimeoutMs is > 0)
        {
            next.PendingTimeouts[step.Id] = new WorkflowPendingTimeoutState
            {
                StepId = step.Id,
                TimeoutMs = Math.Clamp(step.TimeoutMs.Value, 100, 600_000),
                SemanticGeneration = NextSemanticGeneration(
                    State.PendingTimeouts.TryGetValue(step.Id, out var existingTimeout)
                        ? existingTimeout.SemanticGeneration
                        : 0),
            };
        }
        else
        {
            next.PendingTimeouts.Remove(step.Id);
        }

        await PersistStateAsync(next, ct);

        if (step.TimeoutMs is > 0)
        {
            await ScheduleWorkflowCallbackAsync(
                BuildStepTimeoutCallbackId(runId, step.Id),
                TimeSpan.FromMilliseconds(next.PendingTimeouts[step.Id].TimeoutMs),
                new WorkflowStepTimeoutFiredEvent
                {
                    RunId = runId,
                    StepId = step.Id,
                    TimeoutMs = next.PendingTimeouts[step.Id].TimeoutMs,
                },
                next.PendingTimeouts[step.Id].SemanticGeneration,
                step.Id,
                sessionId: null,
                kind: "step_timeout",
                ct);
        }

        try
        {
            await PublishAsync(request, EventDirection.Self, ct);
        }
        catch (Exception ex)
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = step.Id,
                RunId = runId,
                Success = false,
                Error = $"step dispatch failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }
    }

    private async Task DispatchInternalStepAsync(
        string runId,
        string parentStepId,
        string stepId,
        string stepType,
        string input,
        string targetRole,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        var request = new StepRequestEvent
        {
            StepId = stepId,
            StepType = WorkflowPrimitiveCatalog.ToCanonicalType(stepType),
            RunId = runId,
            Input = input,
            TargetRole = targetRole ?? string.Empty,
        };
        foreach (var (key, value) in parameters)
            request.Parameters[key] = value;

        var next = State.Clone();
        next.StepExecutions[stepId] = BuildExecutionState(
            stepId,
            request.StepType,
            input,
            request.TargetRole,
            attempt: 1,
            parentStepId,
            request.Parameters);
        await PersistStateAsync(next, ct);

        try
        {
            await PublishAsync(request, EventDirection.Self, ct);
        }
        catch (Exception ex)
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = stepId,
                RunId = runId,
                Success = false,
                Error = $"internal step dispatch failed: {ex.Message}",
            }, EventDirection.Self, ct);
        }
    }

    private async Task DispatchWhileIterationAsync(
        WorkflowWhileState state,
        string input,
        CancellationToken ct)
    {
        var vars = BuildIterationVariables(input, state.Iteration, state.MaxIterations);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in state.SubParameters)
            parameters[key] = _expressionEvaluator.Evaluate(value, vars);

        await DispatchInternalStepAsync(
            State.RunId,
            state.StepId,
            $"{state.StepId}_iter_{state.Iteration}",
            state.SubStepType,
            input,
            state.SubTargetRole,
            parameters,
            ct);
    }

    private async Task<bool> TryHandleSubWorkflowCompletionAsync(
        WorkflowCompletedEvent completed,
        string? publisherActorId,
        CancellationToken ct)
    {
        var childRunId = completed.RunId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(childRunId) || !State.PendingSubWorkflows.TryGetValue(childRunId, out var pending))
            return false;

        if (!string.IsNullOrWhiteSpace(pending.ChildActorId) &&
            !string.Equals(pending.ChildActorId, publisherActorId, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "Ignore workflow_call completion due to publisher mismatch childRun={ChildRunId} expected={Expected} actual={Actual}",
                childRunId,
                pending.ChildActorId,
                publisherActorId ?? "(none)");
            return true;
        }

        var next = State.Clone();
        next.PendingSubWorkflows.Remove(childRunId);
        await PersistStateAsync(next, ct);

        var parentCompleted = new StepCompletedEvent
        {
            StepId = pending.ParentStepId,
            RunId = State.RunId,
            Success = completed.Success,
            Output = completed.Output,
            Error = completed.Error,
        };
        parentCompleted.Metadata["workflow_call.invocation_id"] = pending.InvocationId;
        parentCompleted.Metadata["workflow_call.workflow_name"] = pending.WorkflowName;
        parentCompleted.Metadata["workflow_call.lifecycle"] = WorkflowCallLifecycle.Normalize(pending.Lifecycle);
        parentCompleted.Metadata["workflow_call.child_actor_id"] = pending.ChildActorId;
        parentCompleted.Metadata["workflow_call.child_run_id"] = childRunId;
        await PublishAsync(parentCompleted, EventDirection.Self, ct);

        if (!string.Equals(WorkflowCallLifecycle.Normalize(pending.Lifecycle), WorkflowCallLifecycle.Singleton, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pending.ChildActorId))
        {
            try
            {
                await _runtime.UnlinkAsync(pending.ChildActorId, ct);
                await _runtime.DestroyAsync(pending.ChildActorId, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to clean up child workflow actor {ChildActorId}", pending.ChildActorId);
            }
        }

        return true;
    }

    private async Task FinalizeRunAsync(bool success, string output, string error, CancellationToken ct)
    {
        var next = State.Clone();
        next.ActiveStepId = string.Empty;
        next.Status = success ? StatusCompleted : StatusFailed;
        next.FinalOutput = success ? output : string.Empty;
        next.FinalError = success ? string.Empty : error;
        await PersistStateAsync(next, ct);
        await PublishFinalWorkflowCompletedAsync(success, output, error, ct);
    }

    private async Task PublishFinalWorkflowCompletedAsync(bool success, string output, string error, CancellationToken ct)
    {
        await PublishAsync(new WorkflowCompletedEvent
        {
            WorkflowName = State.WorkflowName,
            RunId = State.RunId,
            Success = success,
            Output = output,
            Error = error,
        }, EventDirection.Both, ct);

        await PublishAsync(new TextMessageEndEvent
        {
            SessionId = State.RunId,
            Content = success ? output : $"Workflow execution failed: {error}",
        }, EventDirection.Up, ct);
    }

    private Task PersistStateAsync(WorkflowRunState next, CancellationToken ct) =>
        PersistDomainEventAsync(new WorkflowRunStateUpdatedEvent
        {
            State = next.Clone(),
        }, ct);

    private async Task RepublishSuspendedFactsAsync(CancellationToken ct)
    {
        foreach (var wait in State.PendingSignalWaits.Values)
        {
            await PublishAsync(new WaitingForSignalEvent
            {
                StepId = wait.StepId,
                SignalName = wait.SignalName,
                Prompt = wait.Prompt,
                TimeoutMs = wait.TimeoutMs,
                RunId = State.RunId,
                WaitToken = wait.WaitToken,
            }, EventDirection.Both, ct);
        }

        foreach (var gate in State.PendingHumanGates.Values)
        {
            var suspended = new WorkflowSuspendedEvent
            {
                RunId = State.RunId,
                StepId = gate.StepId,
                SuspensionType = gate.GateType,
                Prompt = gate.Prompt,
                TimeoutSeconds = gate.TimeoutSeconds,
                ResumeToken = gate.ResumeToken,
            };
            if (!string.IsNullOrWhiteSpace(gate.Variable))
                suspended.Metadata["variable"] = gate.Variable;
            if (!string.IsNullOrWhiteSpace(gate.OnTimeout))
                suspended.Metadata["on_timeout"] = gate.OnTimeout;
            if (!string.IsNullOrWhiteSpace(gate.OnReject))
                suspended.Metadata["on_reject"] = gate.OnReject;
            suspended.Metadata["resume_token"] = gate.ResumeToken;
            await PublishAsync(suspended, EventDirection.Both, ct);
        }
    }

    private void InstallStatelessPrimitiveModules()
    {
        if (Services == null)
            return;

        var installedTypes = new HashSet<System.Type>();
        var modules = new List<IEventModule>();
        foreach (var registration in _modulePacks.SelectMany(x => x.Modules))
        {
            if (!installedTypes.Add(registration.ModuleType))
                continue;

            var module = registration.Create(Services);
            if (StateOwnedModuleNames.Contains(module.Name))
                continue;

            modules.Add(module);
        }

        SetModules(modules);
    }

    private WorkflowCompilationResult EvaluateWorkflowCompilation(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return WorkflowCompilationResult.Invalid("workflow yaml is empty");

        try
        {
            var workflow = _parser.Parse(yaml);
            var errors = ValidateWorkflowDefinition(workflow);
            if (errors.Count > 0)
                return WorkflowCompilationResult.Invalid(string.Join("; ", errors));

            return WorkflowCompilationResult.Success(workflow);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "WorkflowRunGAgent compile failed.");
            return WorkflowCompilationResult.Invalid(ex.Message);
        }
    }

    private void RebuildCompiledWorkflowCache()
    {
        if (string.IsNullOrWhiteSpace(State.WorkflowYaml))
        {
            _compiledWorkflow = null;
            return;
        }

        var result = EvaluateWorkflowCompilation(State.WorkflowYaml);
        _compiledWorkflow = result.Workflow;
    }

    private List<string> ValidateWorkflowDefinition(WorkflowDefinition workflow) =>
        WorkflowValidator.Validate(
            workflow,
            new WorkflowValidator.WorkflowValidationOptions
            {
                RequireKnownStepTypes = true,
                KnownStepTypes = _knownStepTypes,
            },
            availableWorkflowNames: null);

    private void EnsureWorkflowNameCanBind(string? workflowName)
    {
        var incomingWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowName);
        var currentWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(State.WorkflowName);
        if (!string.IsNullOrWhiteSpace(currentWorkflowName) &&
            !string.IsNullOrWhiteSpace(incomingWorkflowName) &&
            !string.Equals(currentWorkflowName, incomingWorkflowName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"WorkflowRunGAgent '{Id}' is already bound to workflow '{State.WorkflowName}' and cannot switch to '{workflowName}'.");
        }
    }

    private async Task EnsureAgentTreeAsync(CancellationToken ct)
    {
        if (_compiledWorkflow == null)
            return;

        var roleAgentType = _roleAgentTypeResolver.ResolveRoleAgentType();
        if (!typeof(IRoleAgent).IsAssignableFrom(roleAgentType))
            throw new InvalidOperationException($"Role agent type '{roleAgentType.FullName}' does not implement IRoleAgent.");

        foreach (var role in _compiledWorkflow.Roles)
        {
            var childActorId = BuildChildActorId(role.Id);
            var actor = await _runtime.GetAsync(childActorId) ?? await _runtime.CreateAsync(roleAgentType, childActorId, ct);
            await _runtime.LinkAsync(Id, actor.Id, ct);
            await actor.HandleEventAsync(CreateRoleAgentInitializeEnvelope(role), ct);
        }
    }

    private string BuildChildActorId(string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            throw new InvalidOperationException("Role id is required to create child actor.");
        return $"{Id}:{roleId.Trim()}";
    }

    private EventEnvelope CreateRoleAgentInitializeEnvelope(RoleDefinition role)
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

    private async Task ScheduleWorkflowCallbackAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage evt,
        int semanticGeneration,
        string stepId,
        string? sessionId,
        string kind,
        CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CallbackSemanticGenerationMetadataKey] = semanticGeneration.ToString(CultureInfo.InvariantCulture),
            [CallbackRunIdMetadataKey] = State.RunId,
            [CallbackStepIdMetadataKey] = stepId,
            [CallbackKindMetadataKey] = kind,
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
            metadata[CallbackSessionIdMetadataKey] = sessionId;

        await ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, evt, metadata, ct);
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

    private async Task<IActor> ResolveOrCreateSubWorkflowRunActorAsync(string actorId, CancellationToken ct)
    {
        var existing = await _runtime.GetAsync(actorId);
        if (existing != null)
            return existing;

        return await _runtime.CreateAsync<WorkflowRunGAgent>(actorId, ct);
    }

    private async Task<string> ResolveWorkflowYamlAsync(string workflowName, CancellationToken ct)
    {
        foreach (var (registeredName, yaml) in State.InlineWorkflowYamls)
        {
            if (string.Equals(registeredName, workflowName, StringComparison.OrdinalIgnoreCase))
                return yaml;
        }

        var resolver = _workflowDefinitionResolver ?? Services.GetService<IWorkflowDefinitionResolver>();
        if (resolver == null)
            throw new InvalidOperationException("workflow_call requires IWorkflowDefinitionResolver service registration.");

        var yamlFromResolver = await resolver.GetWorkflowYamlAsync(workflowName, ct);
        if (string.IsNullOrWhiteSpace(yamlFromResolver))
            throw new InvalidOperationException($"workflow_call references unregistered workflow '{workflowName}'");
        return yamlFromResolver;
    }

    private EventEnvelope CreateWorkflowDefinitionBindEnvelope(string workflowYaml, string workflowName)
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

    private readonly record struct WorkflowCompilationResult(bool Compiled, string CompilationError, WorkflowDefinition? Workflow)
    {
        public static WorkflowCompilationResult Success(WorkflowDefinition workflow) =>
            new(true, string.Empty, workflow);

        public static WorkflowCompilationResult Invalid(string error) =>
            new(false, error ?? string.Empty, null);
    }
}

public sealed record WorkflowRunBindingSnapshot(
    string WorkflowName,
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls);
