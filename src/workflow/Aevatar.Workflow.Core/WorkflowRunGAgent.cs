using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Expressions;
using Aevatar.Workflow.Core.Primitives;
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

    private readonly IActorRuntime _runtime;
    private readonly IRoleAgentTypeResolver _roleAgentTypeResolver;
    private readonly IWorkflowDefinitionResolver? _workflowDefinitionResolver;
    private readonly WorkflowPrimitiveExecutorRegistry _primitiveRegistry;
    private readonly WorkflowExpressionEvaluator _expressionEvaluator = new();
    private readonly WorkflowRunStepRequestFactory _stepRequestFactory;
    private readonly ISet<string> _knownStepTypes;
    private readonly WorkflowCompilationService _workflowCompilationService;
    private readonly WorkflowRunEffectDispatcher _effectDispatcher;
    private readonly WorkflowRunDispatchRuntime _dispatchRuntime;
    private readonly WorkflowRunControlFlowRuntime _controlFlowRuntime;
    private readonly WorkflowRunHumanInteractionRuntime _humanInteractionRuntime;
    private readonly WorkflowRunAIRuntime _aiRuntime;
    private readonly WorkflowRunCompositionRuntime _compositionRuntime;
    private readonly WorkflowPrimitiveExecutionPlanner _primitiveExecutionPlanner;
    private readonly WorkflowRunCallbackRuntime _callbackRuntime;
    private readonly WorkflowRunStatefulCompletionRuntime _statefulCompletionRuntime;
    private readonly WorkflowRunAsyncPolicyRuntime _asyncPolicyRuntime;
    private readonly WorkflowAsyncOperationReconciler _asyncOperationReconciler;

    private WorkflowDefinition? _compiledWorkflow;

    public WorkflowRunGAgent(
        IActorRuntime runtime,
        IRoleAgentTypeResolver roleAgentTypeResolver,
        IEnumerable<IWorkflowPrimitivePack> primitivePacks,
        IWorkflowDefinitionResolver? workflowDefinitionResolver = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _roleAgentTypeResolver = roleAgentTypeResolver ?? throw new ArgumentNullException(nameof(roleAgentTypeResolver));
        _workflowDefinitionResolver = workflowDefinitionResolver;
        _stepRequestFactory = new WorkflowRunStepRequestFactory(_expressionEvaluator);

        var packs = (primitivePacks ?? throw new ArgumentNullException(nameof(primitivePacks))).ToList();
        if (packs.Count == 0)
            packs = [new WorkflowCorePrimitivePack()];

        _primitiveRegistry = new WorkflowPrimitiveExecutorRegistry(packs);
        _knownStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(_primitiveRegistry.RegisteredNames);
        _knownStepTypes.UnionWith(WorkflowPrimitiveCatalog.BuiltInCanonicalTypes);
        _workflowCompilationService = new WorkflowCompilationService(
            new HashSet<string>(_knownStepTypes, StringComparer.OrdinalIgnoreCase));
        _effectDispatcher = new WorkflowRunEffectDispatcher(
            actorIdAccessor: () => Id,
            runIdAccessor: () => State.RunId,
            compiledWorkflowAccessor: () => _compiledWorkflow,
            runtime: _runtime,
            resolveRoleAgentType: _roleAgentTypeResolver.ResolveRoleAgentType,
            buildChildActorId: roleId => WorkflowRunSupport.BuildChildActorId(Id, roleId),
            createRoleAgentInitializeEnvelope: CreateRoleAgentInitializeEnvelopeCore,
            scheduleSelfDurableTimeoutAsync: async (callbackId, dueTime, evt, metadata, ct) =>
            {
                await ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, evt, metadata, ct);
            },
            resolveWorkflowYamlAsync: ResolveWorkflowYamlCoreAsync,
            createWorkflowDefinitionBindEnvelope: CreateWorkflowDefinitionBindEnvelopeCore);
        _dispatchRuntime = new WorkflowRunDispatchRuntime(
            stateAccessor: () => State,
            compiledWorkflowAccessor: () => _compiledWorkflow,
            stepRequestFactory: _stepRequestFactory,
            expressionEvaluator: _expressionEvaluator,
            persistStateAsync: PersistStateAsync,
            publishAsync: (evt, direction, ct) => PublishAsync(evt, direction, ct),
            effectDispatcher: _effectDispatcher);
        _controlFlowRuntime = new WorkflowRunControlFlowRuntime(
            stateAccessor: () => State,
            persistStateAsync: PersistStateAsync,
            publishAsync: (evt, direction, ct) => PublishAsync(evt, direction, ct),
            effectDispatcher: _effectDispatcher,
            dispatchInternalStepAsync: _dispatchRuntime.DispatchInternalStepAsync,
            dispatchWhileIterationAsync: _dispatchRuntime.DispatchWhileIterationAsync);
        _humanInteractionRuntime = new WorkflowRunHumanInteractionRuntime(
            stateAccessor: () => State,
            persistStateAsync: PersistStateAsync,
            publishAsync: (evt, direction, ct) => PublishAsync(evt, direction, ct));
        _aiRuntime = new WorkflowRunAIRuntime(
            actorIdAccessor: () => Id,
            stateAccessor: () => State,
            persistStateAsync: PersistStateAsync,
            publishAsync: (evt, direction, ct) => PublishAsync(evt, direction, ct),
            sendToAsync: (targetActorId, evt, ct) => SendToAsync(targetActorId, evt, ct),
            effectDispatcher: _effectDispatcher,
            dispatchInternalStepAsync: _dispatchRuntime.DispatchInternalStepAsync);
        _compositionRuntime = new WorkflowRunCompositionRuntime(
            actorIdAccessor: () => Id,
            stateAccessor: () => State,
            persistStateAsync: PersistStateAsync,
            publishAsync: (evt, direction, ct) => PublishAsync(evt, direction, ct),
            sendToAsync: (targetActorId, evt, ct) => SendToAsync(targetActorId, evt, ct),
            logWarningAsync: (ex, message, args) =>
            {
                if (ex == null)
                    Logger.LogWarning(message, args);
                else
                    Logger.LogWarning(ex, message, args);
                return Task.CompletedTask;
            },
            effectDispatcher: _effectDispatcher,
            dispatchInternalStepAsync: _dispatchRuntime.DispatchInternalStepAsync);
        _callbackRuntime = new WorkflowRunCallbackRuntime(
            actorIdAccessor: () => Id,
            stateAccessor: () => State,
            compiledWorkflowAccessor: () => _compiledWorkflow,
            persistStateAsync: PersistStateAsync,
            publishAsync: (evt, direction, ct) => PublishAsync(evt, direction, ct),
            dispatchWorkflowStepAsync: _dispatchRuntime.DispatchWorkflowStepAsync,
            dispatchReflectPhaseAsync: _aiRuntime.DispatchReflectPhaseAsync);
        _statefulCompletionRuntime = new WorkflowRunStatefulCompletionRuntime(
            stateAccessor: () => State,
            persistStateAsync: PersistStateAsync,
            publishAsync: (evt, direction, ct) => PublishAsync(evt, direction, ct),
            dispatchInternalStepAsync: _dispatchRuntime.DispatchInternalStepAsync,
            dispatchWhileIterationAsync: _dispatchRuntime.DispatchWhileIterationAsync,
            evaluateWhileCondition: (state, output, nextIteration) => _stepRequestFactory.EvaluateWhileCondition(state, output, nextIteration));
        _asyncPolicyRuntime = new WorkflowRunAsyncPolicyRuntime(
            stateAccessor: () => State,
            compiledWorkflowAccessor: () => _compiledWorkflow,
            persistStateAsync: PersistStateAsync,
            dispatchWorkflowStepAsync: _dispatchRuntime.DispatchWorkflowStepAsync,
            finalizeRunAsync: FinalizeRunAsync,
            effectDispatcher: _effectDispatcher);
        _primitiveExecutionPlanner = new WorkflowPrimitiveExecutionPlanner(
            TryHandleRegisteredPrimitiveAsync,
            [
                new WorkflowControlFlowPlanner(
                    _controlFlowRuntime.HandleDelayStepRequestAsync,
                    _controlFlowRuntime.HandleWaitSignalStepRequestAsync,
                    _controlFlowRuntime.HandleRaceStepRequestAsync,
                    _controlFlowRuntime.HandleWhileStepRequestAsync),
                new WorkflowHumanInteractionPlanner(
                    (request, ct) => _humanInteractionRuntime.HandleHumanGateStepRequestAsync(request, "human_input", ct),
                    (request, ct) => _humanInteractionRuntime.HandleHumanGateStepRequestAsync(request, "human_approval", ct)),
                new WorkflowAIPlanner(
                    _aiRuntime.HandleLlmCallStepRequestAsync,
                    _aiRuntime.HandleEvaluateStepRequestAsync,
                    _aiRuntime.HandleReflectStepRequestAsync,
                    _aiRuntime.HandleCacheStepRequestAsync),
                new WorkflowCompositionPlanner(
                    _compositionRuntime.HandleParallelStepRequestAsync,
                    _compositionRuntime.HandleForEachStepRequestAsync,
                    _compositionRuntime.HandleMapReduceStepRequestAsync,
                    _compositionRuntime.HandleWorkflowCallStepRequestAsync),
            ]);
        _asyncOperationReconciler = new WorkflowAsyncOperationReconciler(
            [
                _statefulCompletionRuntime.TryHandleParallelCompletionAsync,
                _statefulCompletionRuntime.TryHandleForEachCompletionAsync,
                _statefulCompletionRuntime.TryHandleMapReduceCompletionAsync,
                _statefulCompletionRuntime.TryHandleRaceCompletionAsync,
                _statefulCompletionRuntime.TryHandleWhileCompletionAsync,
                _statefulCompletionRuntime.TryHandleCacheCompletionAsync,
            ],
            _callbackRuntime.HandleWorkflowStepTimeoutFiredAsync,
            _callbackRuntime.HandleWorkflowStepRetryBackoffFiredAsync,
            _callbackRuntime.HandleDelayStepTimeoutFiredAsync,
            _callbackRuntime.HandleWaitSignalTimeoutFiredAsync,
            _callbackRuntime.HandleLlmCallWatchdogTimeoutFiredAsync,
            _callbackRuntime.HandleLlmLikeResponseAsync);
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
        WorkflowRunSupport.ResetRuntimeState(next, clearChildActors: false);
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

        await _dispatchRuntime.DispatchWorkflowStepAsync(entry, input, runId, CancellationToken.None);
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
            if (await _asyncPolicyRuntime.TryScheduleRetryAsync(currentStep, evt, next, CancellationToken.None))
                return;

            if (await _asyncPolicyRuntime.TryHandleOnErrorAsync(currentStep, evt, next, CancellationToken.None))
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
        await _dispatchRuntime.DispatchWorkflowStepAsync(nextStep, evt.Output ?? string.Empty, runId, CancellationToken.None);
    }

}

public sealed record WorkflowRunBindingSnapshot(
    string WorkflowName,
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls);
