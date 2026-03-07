using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed partial class WorkflowRunGAgent : GAgentBase<WorkflowRunState>
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

    private readonly IActorRuntime _runtime;
    private readonly IRoleAgentTypeResolver _roleAgentTypeResolver;
    private readonly IWorkflowDefinitionResolver? _workflowDefinitionResolver;
    private readonly WorkflowPrimitiveRegistry _primitiveRegistry;
    private readonly WorkflowParser _parser = new();
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();
    private readonly ISet<string> _knownStepTypes;
    private readonly WorkflowRunEffectDispatcher _effectDispatcher;
    private readonly WorkflowPrimitiveExecutionPlanner _primitiveExecutionPlanner;
    private readonly WorkflowAsyncOperationReconciler _asyncOperationReconciler;

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

        var packs = (modulePacks ?? throw new ArgumentNullException(nameof(modulePacks))).ToList();
        if (packs.Count == 0)
            packs = [new WorkflowCoreModulePack()];

        _primitiveRegistry = new WorkflowPrimitiveRegistry(packs);
        _knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(_primitiveRegistry.RegisteredNames);
        _knownStepTypes.UnionWith(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);
        _effectDispatcher = new WorkflowRunEffectDispatcher(
            actorIdAccessor: () => Id,
            runIdAccessor: () => State.RunId,
            compiledWorkflowAccessor: () => _compiledWorkflow,
            runtime: _runtime,
            resolveRoleAgentType: _roleAgentTypeResolver.ResolveRoleAgentType,
            buildChildActorId: BuildChildActorId,
            createRoleAgentInitializeEnvelope: CreateRoleAgentInitializeEnvelopeCore,
            scheduleSelfDurableTimeoutAsync: async (callbackId, dueTime, evt, metadata, ct) =>
            {
                await ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, evt, metadata, ct);
            },
            resolveWorkflowYamlAsync: ResolveWorkflowYamlCoreAsync,
            createWorkflowDefinitionBindEnvelope: CreateWorkflowDefinitionBindEnvelopeCore);
        _primitiveExecutionPlanner = new WorkflowPrimitiveExecutionPlanner(
            TryHandleRegisteredPrimitiveAsync,
            new Dictionary<string, WorkflowStepRequestHandler>(StringComparer.Ordinal)
            {
                ["delay"] = HandleDelayStepRequestAsync,
                ["wait_signal"] = HandleWaitSignalStepRequestAsync,
                ["human_input"] = (request, ct) => HandleHumanGateStepRequestAsync(request, "human_input", ct),
                ["human_approval"] = (request, ct) => HandleHumanGateStepRequestAsync(request, "human_approval", ct),
                ["llm_call"] = HandleLlmCallStepRequestAsync,
                ["evaluate"] = HandleEvaluateStepRequestAsync,
                ["reflect"] = HandleReflectStepRequestAsync,
                ["parallel"] = HandleParallelStepRequestAsync,
                ["foreach"] = HandleForEachStepRequestAsync,
                ["map_reduce"] = HandleMapReduceStepRequestAsync,
                ["race"] = HandleRaceStepRequestAsync,
                ["while"] = HandleWhileStepRequestAsync,
                ["cache"] = HandleCacheStepRequestAsync,
                ["workflow_call"] = HandleWorkflowCallStepRequestAsync,
            });
        _asyncOperationReconciler = new WorkflowAsyncOperationReconciler(
            [
                TryHandleParallelCompletionAsync,
                TryHandleForEachCompletionAsync,
                TryHandleMapReduceCompletionAsync,
                TryHandleRaceCompletionAsync,
                TryHandleWhileCompletionAsync,
                TryHandleCacheCompletionAsync,
            ],
            HandleWorkflowStepTimeoutFiredAsync,
            HandleWorkflowStepRetryBackoffFiredAsync,
            HandleDelayStepTimeoutFiredAsync,
            HandleWaitSignalTimeoutFiredAsync,
            HandleLlmCallWatchdogTimeoutFiredAsync,
            HandleLlmLikeResponseAsync);
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
            .On<WorkflowRunStatePatchedEvent>(WorkflowRunReducer.ApplyPatchedEvent)
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
        await _primitiveExecutionPlanner.DispatchAsync(request, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true, Priority = 2)]
    public async Task HandleStepCompleted(StepCompletedEvent evt)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
        if (!string.Equals(runId, State.RunId, StringComparison.Ordinal))
            return;

        if (await _asyncOperationReconciler.TryHandleStatefulCompletionAsync(evt, CancellationToken.None))
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
        await _asyncOperationReconciler.HandleRuntimeCallbackEnvelopeAsync(envelope, CancellationToken.None);
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
        await _asyncOperationReconciler.HandleRoleAndPromptResponseEnvelopeAsync(
            envelope,
            defaultPublisherId: Id,
            CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleDynamicWorkflowInvokeRequested(DynamicWorkflowInvokeRequestedEvent request)
    {
        if (!string.Equals(WorkflowRunIdNormalizer.Normalize(request.ParentRunId), State.RunId, StringComparison.Ordinal))
            return;

        var parentStepId = request.ParentStepId?.Trim() ?? string.Empty;
        var workflowYaml = request.WorkflowYaml ?? string.Empty;
        var workflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(request.WorkflowName);
        if (string.IsNullOrWhiteSpace(parentStepId) ||
            string.IsNullOrWhiteSpace(workflowYaml) ||
            string.IsNullOrWhiteSpace(workflowName))
        {
            await PublishAsync(new StepCompletedEvent
            {
                StepId = string.IsNullOrWhiteSpace(parentStepId) ? request.ParentStepId ?? string.Empty : parentStepId,
                RunId = State.RunId,
                Success = false,
                Error = "dynamic_workflow requires parent step id, workflow name, and workflow yaml",
            }, EventDirection.Self);
            return;
        }

        var invocationId = string.IsNullOrWhiteSpace(request.InvocationId)
            ? $"{State.RunId}:dynamic:{parentStepId}:{Guid.NewGuid():N}"
            : request.InvocationId.Trim();
        var childRunId = invocationId;
        var childActorId = BuildSubWorkflowRunActorId(workflowName, WorkflowCallLifecycle.Transient, invocationId);
        var next = State.Clone();
        next.PendingSubWorkflows[childRunId] = new WorkflowPendingSubWorkflowState
        {
            InvocationId = invocationId,
            ParentStepId = parentStepId,
            WorkflowName = workflowName,
            Input = request.Input ?? string.Empty,
            Lifecycle = WorkflowCallLifecycle.Transient,
            ChildActorId = childActorId,
            ChildRunId = childRunId,
            ParentRunId = State.RunId,
        };
        await PersistStateAsync(next, CancellationToken.None);

        try
        {
            var childActor = await ResolveOrCreateSubWorkflowRunActorAsync(childActorId, CancellationToken.None);
            await _runtime.LinkAsync(Id, childActor.Id, CancellationToken.None);
            await childActor.HandleEventAsync(CreateWorkflowDefinitionBindEnvelope(
                workflowYaml,
                workflowName), CancellationToken.None);
            await SendToAsync(childActor.Id, new ChatRequestEvent
            {
                Prompt = request.Input ?? string.Empty,
                SessionId = childRunId,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var rollback = State.Clone();
            rollback.PendingSubWorkflows.Remove(childRunId);
            await PersistStateAsync(rollback, CancellationToken.None);
            await PublishAsync(new StepCompletedEvent
            {
                StepId = parentStepId,
                RunId = State.RunId,
                Success = false,
                Error = $"dynamic_workflow invocation failed: {ex.Message}",
            }, EventDirection.Self, CancellationToken.None);
        }
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

    private Task PersistStateAsync(WorkflowRunState next, CancellationToken ct)
    {
        var patch = WorkflowRunStatePatchSupport.BuildPatch(State, next);
        return patch == null
            ? Task.CompletedTask
            : PersistDomainEventAsync(patch, ct);
    }

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
