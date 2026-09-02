using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using ApplicationWorkflowFileArtifactOwnershipPort = Aevatar.Workflow.Application.Abstractions.Runs.IFileArtifactOwnershipPort;
using ApplicationFileArtifactRef = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactRef;
using ApplicationFileArtifactSourceKind = Aevatar.Workflow.Application.Abstractions.Runs.FileArtifactSourceKind;

namespace Aevatar.Workflow.Core;

[SuppressMessage(
    "Maintainability",
    "CA1506:Avoid excessive class coupling",
    Justification = "WorkflowRunGAgent is the run-scoped orchestration boundary and intentionally coordinates workflow execution dependencies.")]
// Refactor (iter115/cluster-3):
//   Old pattern: WorkflowRunGAgent kept durable control/security facts in
//                process-local runtime context.
//   New principle: durable control/security facts live in typed WorkflowRunState;
//                  runtime context carries only same-turn passthrough metadata.
// Refactor (iter78/cluster-078-workflow-subrun-lifecycle-handoff):
//   Old pattern: create/link/bind/start child before persisting invocation → orphan on crash
//   New principle (narrow): persist PendingSubWorkflowInvocation before child side-effects; 4 phases idempotent by invocation_id + child_actor_id
[GAgent("workflow.run")]
public sealed partial class WorkflowRunGAgent
    : GAgentBase<WorkflowRunState>,
      IWorkflowExecutionStateHost,
      IRuntimeSecretStoreAccessor,
      ISecretVaultAccessor
{
    private const string RunningStatus = "running";
    private const string CompletedStatus = "completed";
    private const string FailedStatus = "failed";
    private const string StoppedStatus = "stopped";
    private static readonly TimeSpan ScheduledCallerCredentialCleanupTimeout = TimeSpan.FromSeconds(5);
    private const string StartedNotificationDispatchOperationPrefix = "workflow-started-notification";
    private const string TerminalNotificationDispatchOperationPrefix = "workflow-terminal-notification";
    private const string TerminalNotificationRetryCallbackPrefix = "workflow-terminal-notification-retry";
    private const int TerminalNotificationInitialRetryDelayMs = 250;
    private const int TerminalNotificationMaxRetryDelayMs = 30_000;
    private const string WorkflowNotExecutableError = "Workflow run is not definition-bound or compiled.";
    private const string InputFileBindingError = "workflow_input_file_binding_failed";
    private WorkflowDefinition? _compiledWorkflow;
    private readonly WorkflowParser _parser = new();
    private readonly List<string> _childAgentIds = [];
    private readonly WorkflowExecutionRuntimeContext _runtimeContext = new();
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IEventModuleFactory<IWorkflowExecutionContext> _stepExecutorFactory;
    private readonly IReadOnlyList<IWorkflowModuleDependencyExpander> _moduleDependencyExpanders;
    private readonly IReadOnlyList<IWorkflowModuleConfigurator> _moduleConfigurators;
    private readonly ISet<string> _knownModuleStepTypes;
    private readonly SubWorkflowOrchestrator _subWorkflowOrchestrator;
    private readonly ApplicationWorkflowFileArtifactOwnershipPort? _fileArtifactOwnership;
    private readonly ISecretVault? _secretVault;
    private readonly TimeProvider _timeProvider;

    public WorkflowRunGAgent(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IEventModuleFactory<IWorkflowExecutionContext> stepExecutorFactory,
        IEnumerable<IWorkflowModulePack> modulePacks,
        IWorkflowDefinitionResolver? workflowDefinitionResolver = null,
        ISecretVault? secretVault = null,
        ApplicationWorkflowFileArtifactOwnershipPort? fileArtifactOwnership = null,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _stepExecutorFactory = stepExecutorFactory ?? throw new ArgumentNullException(nameof(stepExecutorFactory));
        _ = workflowDefinitionResolver;

        var packs = modulePacks?.ToList()
            ?? throw new ArgumentNullException(nameof(modulePacks));

        _moduleDependencyExpanders = packs
            .SelectMany(x => x.DependencyExpanders)
            .GroupBy(x => x.GetType())
            .Select(x => x.First())
            .OrderBy(x => x.Order)
            .ToList();

        _moduleConfigurators = packs
            .SelectMany(x => x.Configurators)
            .GroupBy(x => x.GetType())
            .Select(x => x.First())
            .OrderBy(x => x.Order)
            .ToList();

        _knownModuleStepTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(
            packs
                .SelectMany(x => x.Modules)
                .SelectMany(x => x.Names));
        _fileArtifactOwnership = fileArtifactOwnership;
        _secretVault = secretVault;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _subWorkflowOrchestrator = new SubWorkflowOrchestrator(
            _runtime,
            _dispatchPort,
            () => Id,
            () => Logger,
            (evt, token) => PersistDomainEventAsync(evt, token),
            (events, token) => PersistDomainEventsAsync(events, token),
            (evt, direction, token) => PublishAsync(evt, direction, token),
            (actorId, evt, token) => SendToAsync(actorId, evt, token),
            (callbackId, dueTime, evt, token) => ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, evt, ct: token),
            (lease, token) => CancelDurableCallbackAsync(lease, token));
    }

    public string RunId => string.IsNullOrWhiteSpace(State.RunId)
        ? Id
        : State.RunId;

    public string ScopeId => State.ScopeId ?? string.Empty;

    public string ScheduleId => State.ScheduleId ?? string.Empty;

    public WorkflowCallerNyxIdAuthority? CallerNyxIdAuthority
    {
        get
        {
            var source = State.ExecutionContext?.CallerCredential?.NyxIdAuthority;
            return WorkflowRunExecutionContextStateAccess.TryNormalizeCallerNyxIdAuthority(
                source,
                out var authority)
                ? authority
                : null;
        }
    }

    IRuntimeSecretStore? IRuntimeSecretStoreAccessor.RuntimeSecretStore =>
        (IRuntimeSecretStore?)Services.GetService(typeof(IRuntimeSecretStore));

    ISecretVault? ISecretVaultAccessor.SecretVault =>
        _secretVault;

    WorkflowExecutionRuntimeContext IWorkflowExecutionStateHost.RuntimeContext => _runtimeContext;

    // Refactor (iter115/cluster-3): Old pattern: callers received the mutable
    // State.ExecutionContext object and could bypass PersistDomainEventAsync.
    // New principle: callers only receive a snapshot; writes enter the event log
    // through WorkflowRunExecutionContextUpdatedEvent/ClearedEvent reducers.
    WorkflowRunExecutionContextState IWorkflowExecutionStateHost.ExecutionContextSnapshot =>
        State.ExecutionContext?.Clone() ?? new WorkflowRunExecutionContextState();

    WorkflowCapabilityAdmissionPlan IWorkflowExecutionStateHost.CapabilityAdmissionPlanSnapshot =>
        State.CapabilityAdmissionPlan?.Clone() ?? new WorkflowCapabilityAdmissionPlan();

    Task IWorkflowExecutionStateHost.UpdateExecutionContextAsync(
        WorkflowRunExecutionContextDelta delta,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delta);
        return PersistDomainEventAsync(
            new WorkflowRunExecutionContextUpdatedEvent
            {
                RunId = RunId,
                ExecutionContextDelta = delta,
            },
            ct);
    }

    Task IWorkflowExecutionStateHost.ClearExecutionContextAsync(CancellationToken ct) =>
        PersistDomainEventAsync(
            new WorkflowRunExecutionContextClearedEvent
            {
                RunId = RunId,
            },
            ct);

    public Any? GetExecutionState(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        return State.ExecutionStates.TryGetValue(scopeKey, out var state)
            ? state
            : null;
    }

    public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() =>
        State.ExecutionStates.ToList();

    public Task UpsertExecutionStateAsync(
        string scopeKey,
        Any state,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ArgumentNullException.ThrowIfNull(state);
        return PersistDomainEventAsync(
            new WorkflowExecutionStateUpsertedEvent
            {
                ScopeKey = scopeKey,
                State = state,
            },
            ct);
    }

    public Task ClearExecutionStateAsync(
        string scopeKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        return PersistDomainEventAsync(
            new WorkflowExecutionStateClearedEvent
            {
                ScopeKey = scopeKey,
            },
            ct);
    }

    Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
        CompensableStepDispatchedEvent evt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt, ct);
    }

    async Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
        WorkflowCompletedEvent terminalFailure,
        StepCompletedEvent? terminalStep,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terminalFailure);

        if (IsCompensating(State))
            return BuildCurrentCompensationResult(WorkflowCompensationTransitionStatus.AlreadyCompensating);

        var compensationState = BuildCompensationStartState(State, terminalStep);
        if (compensationState.CompensableLedger.Count == 0)
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.NoCompensableLedger);

        var originFailedStepId = ResolveCompensationOriginFailedStepId(compensationState, terminalStep);
        var cursor = compensationState.CompensableLedger.Count - 1;
        var entry = compensationState.CompensableLedger[cursor];
        var executionId = Guid.NewGuid().ToString("N");
        await PersistDomainEventAsync(new CompensationRequestEvent
        {
            RunId = string.IsNullOrWhiteSpace(terminalFailure.RunId) ? RunId : WorkflowRunIdNormalizer.Normalize(terminalFailure.RunId),
            FailedStepId = originFailedStepId,
            CompensationStepId = entry.CompensationStepId,
            IdempotencyKey = entry.IdempotencyKey,
            CapturedOutput = entry.CapturedOutput,
            ExecutionId = executionId,
        }, ct);

        return BuildCompensationResult(
            WorkflowCompensationTransitionStatus.Started,
            entry,
            originFailedStepId,
            executionId);
    }

    async Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.RecordCompensationStepCompletionAsync(
        CompensationStepCompletedEvent completion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completion);

        if (!IsCompensating(State))
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.NoCompensableLedger);

        var cursor = State.CompensationCursor;
        if (!TryGetLedgerEntry(cursor, out var currentEntry) ||
            !MatchesCurrentCompensation(completion, currentEntry))
        {
            await PersistDomainEventAsync(new StaleStepCompletionRejectedEvent
            {
                StepId = completion.CompensationStepId ?? string.Empty,
                RunId = completion.RunId ?? string.Empty,
                ExpectedExecutionId = State.CompensationExecutionId ?? string.Empty,
                ReceivedExecutionId = completion.ExecutionId ?? string.Empty,
            }, ct);
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.RejectedStaleOrDuplicate);
        }

        var remainingUncompensated = completion.Success
            ? 0
            : CalculateRemainingUncompensated(State.CompensableLedger.Count, cursor);

        await PersistDomainEventAsync(new CompensationStepCompletedEvent
        {
            RunId = WorkflowRunIdNormalizer.Normalize(completion.RunId),
            CompensationStepId = currentEntry.CompensationStepId,
            Success = completion.Success,
            Error = completion.Error ?? string.Empty,
            ExecutionId = completion.ExecutionId ?? string.Empty,
        }, ct);

        if (!completion.Success)
        {
            await PersistDomainEventAsync(new WorkflowCompensationFailedEvent
            {
                RunId = State.RunId ?? string.Empty,
                FailedCompensationStepId = currentEntry.CompensationStepId ?? string.Empty,
                RemainingUncompensated = remainingUncompensated,
                Error = completion.Error ?? string.Empty,
            }, ct);
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.CompensationDeadLettered);
        }

        var nextCursor = cursor - 1;
        if (nextCursor < 0)
        {
            await PersistDomainEventAsync(new WorkflowCompensationCompletedEvent
            {
                RunId = State.RunId ?? string.Empty,
                CompensatedSteps = State.CompensableLedger.Count,
            }, ct);
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.CompletedAll);
        }

        var nextEntry = State.CompensableLedger[nextCursor];
        var nextExecutionId = Guid.NewGuid().ToString("N");
        var originFailedStepId = ResolveLastFailedStepId(State);
        await PersistDomainEventAsync(new CompensationRequestEvent
        {
            RunId = State.RunId ?? string.Empty,
            FailedStepId = originFailedStepId,
            CompensationStepId = nextEntry.CompensationStepId,
            IdempotencyKey = nextEntry.IdempotencyKey,
            CapturedOutput = nextEntry.CapturedOutput,
            ExecutionId = nextExecutionId,
        }, ct);

        return BuildCompensationResult(
            WorkflowCompensationTransitionStatus.AdvancedAndRequestedNext,
            nextEntry,
            originFailedStepId,
            nextExecutionId);
    }

    async Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.RecordCompensationPhaseDeadlineExceededAsync(
        string runId,
        string error,
        CancellationToken ct)
    {
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        if (!IsCompensating(State) ||
            !string.Equals(State.RunId, normalizedRunId, StringComparison.Ordinal))
        {
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.NoCompensableLedger);
        }

        var failedCompensationStepId = TryGetLedgerEntry(State.CompensationCursor, out var entry)
            ? entry.CompensationStepId ?? string.Empty
            : string.Empty;
        await PersistDomainEventAsync(new WorkflowCompensationFailedEvent
        {
            RunId = State.RunId ?? string.Empty,
            FailedCompensationStepId = failedCompensationStepId,
            RemainingUncompensated = CalculateRemainingUncompensated(
                State.CompensableLedger.Count,
                State.CompensationCursor),
            Error = error ?? string.Empty,
        }, ct);

        return EmptyCompensationResult(WorkflowCompensationTransitionStatus.CompensationDeadLettered);
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        RebuildCompiledWorkflowCache();
        await base.OnActivateAsync(ct);
        InstallCognitiveModules();

        if (string.Equals(State.Status, RunningStatus, StringComparison.OrdinalIgnoreCase))
            await SendWorkflowRunStartedNotificationAsync(ct);

        // C4 (06-20-observatory-run-state-feed): a terminal run must never drive in-flight child handoffs.
        // ApplyWorkflowCompleted (unlike ApplyWorkflowStopped/RunStopped) does NOT clear
        // PendingSubWorkflowInvocations — those are cleared by HandleWorkflowCompleted's
        // CleanupPendingInvocationsForRunAsync, which the status-only completion-adopt path (R1) skips. So an
        // adopted-completed run can carry stale pending invocations into activation. Guard the forward
        // recovery on non-terminal; compensation recovery (ResumeCompensationAsync, below) is independent and
        // keyed off CompensationCursor/ledger, not pending invocations, so it still runs.
        if (!IsTerminalStatus(State.Status))
            await _subWorkflowOrchestrator.RecoverPendingSubWorkflowInvocationsAsync(State, ct);

        await ResumeCompensationAsync(ct);
        await RecoverTerminalNotificationAsync(ct);
    }

    public async Task BindWorkflowRunDefinitionAsync(
        string definitionActorId,
        string workflowYaml,
        string? workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        string? runId,
        string? scopeId,
        string? runOrigin,
        string? scheduleId,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan,
        ExternalCapabilityExecutionMode expectedExecutionMode,
        CancellationToken ct = default)
    {
        if (expectedExecutionMode == ExternalCapabilityExecutionMode.Unspecified ||
            !System.Enum.IsDefined(expectedExecutionMode))
        {
            throw new InvalidOperationException("Workflow Run expected execution mode is required.");
        }

        if (State.ExpectedExecutionMode != ExternalCapabilityExecutionMode.Unspecified &&
            State.ExpectedExecutionMode != expectedExecutionMode)
        {
            throw new InvalidOperationException(
                "Workflow Run is already bound to a different expected execution mode.");
        }

        EnsureWorkflowNameCanBind(workflowName);
        var childActorIdsToReset = CaptureDerivedChildActorIdsForReset();
        var stateBeforeBind = State.Clone();
        var bindDefinitionEvent = new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = definitionActorId ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
            WorkflowYaml = workflowYaml ?? string.Empty,
            RunId = string.IsNullOrWhiteSpace(runId) ? Id : WorkflowRunIdNormalizer.Normalize(runId),
            ScopeId = scopeId?.Trim() ?? string.Empty,
            RunOrigin = runOrigin?.Trim() ?? string.Empty,
            ScheduleId = scheduleId?.Trim() ?? string.Empty,
            CapabilityAdmissionPlan = capabilityAdmissionPlan?.Clone(),
            ExpectedExecutionMode = expectedExecutionMode,
        };
        if (inlineWorkflowYamls != null)
        {
            foreach (var (key, value) in inlineWorkflowYamls)
                bindDefinitionEvent.InlineWorkflowYamls[key] = value;
        }

        await PersistDomainEventAsync(bindDefinitionEvent, ct);
        await _subWorkflowOrchestrator.CancelPendingDefinitionResolutionTimeoutsAsync(stateBeforeBind, CancellationToken.None);
        RebuildCompiledWorkflowCache();
        await ResetDerivedRuntimeStateAsync(childActorIdsToReset, ct);
        InstallCognitiveModules();
    }

    [EventHandler]
    public Task HandleBindWorkflowRunDefinition(BindWorkflowRunDefinitionEvent request) =>
        BindWorkflowRunDefinitionAsync(
            request.DefinitionActorId,
            request.WorkflowYaml,
            request.WorkflowName,
            request.InlineWorkflowYamls,
            request.RunId,
            request.ScopeId,
            request.RunOrigin,
            request.ScheduleId,
            request.CapabilityAdmissionPlan,
            request.ExpectedExecutionMode);

    public override Task<string> GetDescriptionAsync()
    {
        var status = State.Compiled ? (State.Status?.Trim() ?? "bound") : "invalid";
        return Task.FromResult($"WorkflowRunGAgent[{State.WorkflowName}] run={RunId} ({status})");
    }

    [EventHandler]
    public async Task HandleChatRequest(WorkflowChatRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var commandId = ActiveInboundEnvelope?.Id?.Trim() ?? string.Empty;
        var correlationId = ActiveInboundEnvelope?.Propagation?.CorrelationId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(State.LastCommandId))
        {
            if (!string.Equals(State.LastCommandId, commandId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"workflow Run '{Id}' is already bound to command '{State.LastCommandId}' and cannot execute '{commandId}'.");
            }

            // A crash after observing a command but before starting it leaves
            // the Run bound and is recoverable by the same delivery. Once the
            // Run has advanced, the persistent command identity makes replay
            // an idempotent no-op across process restarts.
            if (!string.Equals(State.Status, "bound", StringComparison.OrdinalIgnoreCase))
            {
                await SendWorkflowRunStartedNotificationAsync(CancellationToken.None);
                return;
            }
        }
        var runId = string.IsNullOrWhiteSpace(State.RunId)
            ? WorkflowRunIdNormalizer.Normalize(Id)
            : WorkflowRunIdNormalizer.Normalize(State.RunId);
        var scopeId = ResolveScopeId(request.ScopeId, State.ScopeId);
        await AdoptCompletionNotificationTargetAsync(
            request.CompletionNotificationTarget,
            runId,
            scopeId,
            commandId,
            correlationId,
            CancellationToken.None);

        if (!string.IsNullOrWhiteSpace(commandId) &&
            !string.Equals(State.LastCommandId, commandId, StringComparison.Ordinal))
        {
            await PersistDomainEventAsync(
                new WorkflowCommandObservedEvent
                {
                    CommandId = commandId,
                },
                CancellationToken.None);
        }

        if (_compiledWorkflow == null)
        {
            await HandleWorkflowCompleted(new WorkflowCompletedEvent
            {
                RunId = runId,
                WorkflowName = State.WorkflowName ?? string.Empty,
                Success = false,
                Error = WorkflowNotExecutableError,
            }, request.SessionId);
            return;
        }

        // Refactor (iter163/cluster-002-first):
        //   Old pattern: actor read command id from request.Headers[workflow.command_id],
        //                making Headers a stable control flow channel.
        //   New principle: actor reads command id from ActiveInboundEnvelope.Id,
        //                  the typed envelope identity.
        var callerCredentialDelta = await WorkflowCallerCredentialRuntimeContextAccess.BuildCredentialDeltaAsync(
            this,
            request.CallerCredential,
            CancellationToken.None);
        _runtimeContext.ApplyRequestMetadata(request.Metadata);
        _runtimeContext.ApplySenderNyxIdAccessToken(request.LlmControl?.SenderNyxIdAccessToken);
        var llmControlDelta = WorkflowRunExecutionContextStateAccess.BuildLlmControlDelta(request.LlmControl);
        var inputFileRefs = ExtractInputFileRefs(request.InputParts);

        await EnsureAgentTreeAsync();

        inputFileRefs = StampInputFileRefs(inputFileRefs, runId, scopeId);
        if (!await BindInputFileArtifactsAsync(inputFileRefs, runId, scopeId))
        {
            await HandleWorkflowCompleted(new WorkflowCompletedEvent
            {
                RunId = runId,
                WorkflowName = _compiledWorkflow.Name,
                Success = false,
                Error = InputFileBindingError,
            }, request.SessionId);
            return;
        }

        var executionContextDelta = MergeExecutionContextDeltas(
            callerCredentialDelta,
            llmControlDelta,
            WorkflowRunExecutionContextStateAccess.ClearWorkflowRuntimeDelta());
        var executionInput = ResolveExecutionInput(request);
        var executionStarted = new WorkflowRunExecutionStartedEvent
        {
            RunId = runId,
            WorkflowName = _compiledWorkflow.Name,
            Input = executionInput,
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            ScopeId = scopeId,
            ExecutionContextDelta = executionContextDelta,
            Attempt = Math.Max(0, request.ForkSeed?.Attempt ?? 0),
            InputFileRefs = { inputFileRefs.Select(static fileRef => fileRef.Clone()) },
            // O2 (06-19-workflow-run-observatory): capture the run-start fact so the readmodel can sort by it.
            StartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            WorkflowCommandId = commandId,
            WorkflowCorrelationId = correlationId,
        };
        if (request.CompletionNotificationTarget != null)
            executionStarted.CompletionNotificationTarget = request.CompletionNotificationTarget.Clone();
        await PersistDomainEventAsync(executionStarted);

        var start = new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            Input = executionInput,
            RunId = runId,
            ForkSeed = request.ForkSeed,
        };
        start.InputFileRefs.Add(inputFileRefs.Select(static fileRef => fileRef.Clone()));
        await PublishStartWorkflowOrTerminalFailureAsync(start, request.SessionId, CancellationToken.None);
        await SendWorkflowRunStartedNotificationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleReplaceWorkflowDefinitionAndExecute(ReplaceWorkflowDefinitionAndExecuteEvent request)
    {
        var yaml = request.WorkflowYaml ?? string.Empty;
        if (string.IsNullOrWhiteSpace(yaml))
        {
            Logger.LogWarning("ReplaceWorkflowDefinitionAndExecute: empty workflow YAML, ignoring.");
            await PublishAsync(new WorkflowLlmInvocationCompletedEvent
            {
                RunId = RunId,
                Success = false,
                Error = "Dynamic workflow YAML is empty.",
                Content = "Dynamic workflow YAML is empty.",
            }, TopologyAudience.Parent);
            return;
        }

        var replaceResult = await ReplaceWorkflowDefinitionBypassingBindingAsync(yaml);
        if (!replaceResult.Compiled || _compiledWorkflow == null)
        {
            var reason = string.IsNullOrWhiteSpace(replaceResult.CompilationError)
                ? "Dynamic workflow YAML compilation failed."
                : $"Dynamic workflow YAML compilation failed: {replaceResult.CompilationError}";
            Logger.LogWarning("ReplaceWorkflowDefinitionAndExecute: YAML compilation failed. Error={Error}", replaceResult.CompilationError);
            await PublishAsync(new WorkflowLlmInvocationCompletedEvent
            {
                RunId = RunId,
                Success = false,
                Error = reason,
                Content = reason,
            }, TopologyAudience.Parent);
            return;
        }

        await EnsureAgentTreeAsync();

        var runId = string.IsNullOrWhiteSpace(State.RunId)
            ? WorkflowRunIdNormalizer.Normalize(Id)
            : WorkflowRunIdNormalizer.Normalize(State.RunId);
        await PersistDomainEventAsync(new WorkflowRunExecutionStartedEvent
        {
            RunId = runId,
            WorkflowName = _compiledWorkflow.Name,
            Input = request.Input ?? string.Empty,
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            ScopeId = State.ScopeId ?? string.Empty,
            ExecutionContextDelta = WorkflowRunExecutionContextStateAccess.ClearWorkflowRuntimeDelta(),
            Attempt = State.ForkAttempt,
            // O2 (06-19-workflow-run-observatory): capture the run-start fact so the readmodel can sort by it.
            StartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        await PublishStartWorkflowOrTerminalFailureAsync(
            new StartWorkflowEvent
            {
                WorkflowName = _compiledWorkflow.Name,
                Input = request.Input ?? string.Empty,
                RunId = runId,
            },
            sessionId: string.Empty,
            CancellationToken.None);
    }

    private async Task PublishStartWorkflowOrTerminalFailureAsync(
        StartWorkflowEvent start,
        string? sessionId,
        CancellationToken ct)
    {
        try
        {
            await PublishAsync(start, TopologyAudience.Self);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogError(
                ex,
                "Workflow start dispatch failed run={RunId} workflow={WorkflowName}.",
                start.RunId,
                start.WorkflowName);
            var terminal = new WorkflowCompletedEvent
            {
                WorkflowName = start.WorkflowName,
                RunId = start.RunId,
                Success = false,
                Error = WorkflowRuntimeFailureMessages.StartDispatchFailed(ex),
            };
            try
            {
                await HandleWorkflowCompleted(terminal);
            }
            catch (Exception terminalEx) when (!ct.IsCancellationRequested)
            {
                Logger.LogError(
                    terminalEx,
                    "Workflow start dispatch terminalization failed run={RunId}.",
                    start.RunId);
                await PublishAsync(new WorkflowLlmInvocationCompletedEvent
                {
                    RunId = start.RunId,
                    SessionId = sessionId ?? string.Empty,
                    Success = false,
                    Content = "Workflow execution failed: start_dispatch_failed",
                    Error = terminal.Error,
                }, TopologyAudience.Parent);
            }
        }
    }

    private Task AdoptCompletionNotificationTargetAsync(
        WorkflowCompletionNotificationTarget? target,
        string runId,
        string scopeId,
        string commandId,
        string correlationId,
        CancellationToken ct)
    {
        if (target == null)
            return Task.CompletedTask;

        return PersistDomainEventAsync(
            new WorkflowRunCompletionNotificationTargetAdoptedEvent
            {
                CompletionNotificationTarget = target.Clone(),
                WorkflowRunId = runId,
                ScopeId = scopeId,
                WorkflowCommandId = commandId,
                WorkflowCorrelationId = correlationId,
                AdoptedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            ct);
    }

    private static string ResolveExecutionInput(WorkflowChatRequestEvent request)
    {
        if (request.ForkSeed != null &&
            request.ForkSeed.Variables.TryGetValue("input", out var seedInput))
        {
            return seedInput ?? string.Empty;
        }

        if (request.ConversationContext != null)
        {
            return RenderConversationExecutionInput(request.ConversationContext, request.Prompt);
        }

        return request.Prompt ?? string.Empty;
    }

    private static string RenderConversationExecutionInput(
        WorkflowConversationContext conversationContext,
        string? currentPrompt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<conversation_context>");
        foreach (var message in conversationContext.Messages
                     .OrderBy(static message => message.Sequence))
        {
            var content = message.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content))
                continue;

            builder
                .Append('[')
                .Append(ToConversationRoleLabel(message.Role))
                .Append("] ")
                .AppendLine(content);
        }

        builder.AppendLine("</conversation_context>");
        builder.AppendLine("<current_user_message>");
        builder.AppendLine(currentPrompt?.Trim() ?? string.Empty);
        builder.Append("</current_user_message>");
        return builder.ToString();
    }

    private static string ToConversationRoleLabel(WorkflowConversationRole role) =>
        role switch
        {
            WorkflowConversationRole.User => "user",
            WorkflowConversationRole.Assistant => "assistant",
            WorkflowConversationRole.Tool => "tool",
            _ => "unknown",
        };

    private static IReadOnlyList<WorkflowFileRef> ExtractInputFileRefs(
        IEnumerable<WorkflowChatInputPartPayload> inputParts) =>
        inputParts
            .Where(static part => part.FileRef is not null && HasFileRefIdentity(part.FileRef))
            .Select(static part => part.FileRef.Clone())
            .ToArray();

    private static bool HasFileRefIdentity(WorkflowFileRef fileRef) =>
        !string.IsNullOrWhiteSpace(fileRef.FileId) ||
        !string.IsNullOrWhiteSpace(fileRef.ArtifactId);

    private static IReadOnlyList<WorkflowFileRef> StampInputFileRefs(
        IReadOnlyList<WorkflowFileRef> fileRefs,
        string runId,
        string scopeId) =>
        fileRefs
            .Select(fileRef =>
            {
                var clone = fileRef.Clone();
                clone.OwnerRunId = runId;
                clone.OwnerScopeId = scopeId ?? string.Empty;
                return clone;
            })
            .ToArray();

    private async Task<bool> BindInputFileArtifactsAsync(
        IReadOnlyList<WorkflowFileRef> fileRefs,
        string runId,
        string scopeId)
    {
        if (fileRefs.Count == 0 || _fileArtifactOwnership == null)
            return true;

        foreach (var fileRef in fileRefs)
        {
            try
            {
                await _fileArtifactOwnership.BindOwnerAsync(
                    ToApplicationFileArtifactRef(fileRef),
                    runId,
                    scopeId,
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Workflow input file artifact owner binding failed: {RunId}", runId);
                return false;
            }
        }

        return true;
    }

    private static ApplicationFileArtifactRef ToApplicationFileArtifactRef(WorkflowFileRef source) =>
        new()
        {
            FileId = source.FileId,
            ArtifactId = source.ArtifactId,
            SourceKind = ToApplicationFileArtifactSourceKind(source.SourceKind),
            SourceMessageId = source.SourceMessageId,
            SourceResourceKey = source.SourceResourceKey,
            FileName = source.FileName,
            MediaType = source.MediaType,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256,
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = source.OwnerRunId,
            OwnerScopeId = source.OwnerScopeId,
        };

    private static ApplicationFileArtifactSourceKind ToApplicationFileArtifactSourceKind(
        WorkflowFileSourceKind source) =>
        source switch
        {
            WorkflowFileSourceKind.ChatInput => ApplicationFileArtifactSourceKind.ChatInput,
            WorkflowFileSourceKind.FormUpload => ApplicationFileArtifactSourceKind.FormUpload,
            WorkflowFileSourceKind.ConnectedServiceResource => ApplicationFileArtifactSourceKind.ConnectedServiceResource,
            WorkflowFileSourceKind.ExternalResource => ApplicationFileArtifactSourceKind.ExternalResource,
            WorkflowFileSourceKind.Generated => ApplicationFileArtifactSourceKind.Generated,
            _ => ApplicationFileArtifactSourceKind.Unspecified,
        };

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleSubWorkflowInvokeRequested(SubWorkflowInvokeRequestedEvent request)
    {
        await _subWorkflowOrchestrator.HandleInvokeRequestedAsync(request, State, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleSubWorkflowDefinitionResolved(SubWorkflowDefinitionResolvedEvent resolved)
    {
        await _subWorkflowOrchestrator.HandleDefinitionResolvedAsync(resolved, State, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleSubWorkflowDefinitionResolveFailed(SubWorkflowDefinitionResolveFailedEvent failed)
    {
        await _subWorkflowOrchestrator.HandleDefinitionResolveFailedAsync(failed, State, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleSubWorkflowDefinitionResolutionTimeoutFired(SubWorkflowDefinitionResolutionTimeoutFiredEvent timeout)
    {
        await _subWorkflowOrchestrator.HandleDefinitionResolutionTimeoutFiredAsync(
            timeout,
            ActiveInboundEnvelope,
            State,
            CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleWorkflowRunTerminalNotificationRetryFired(
        WorkflowRunTerminalNotificationRetryFiredEvent retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        var pending = State.PendingTerminalNotification;
        var matchesIdentity = pending != null &&
            string.Equals(retry.WorkflowActorId, Id, StringComparison.Ordinal) &&
            string.Equals(retry.DeliveryId, pending.DeliveryId, StringComparison.Ordinal) &&
            string.Equals(retry.WorkflowCommandId, pending.WorkflowCommandId, StringComparison.Ordinal);
        var matchesScheduledRetry =
            State.TerminalNotificationDeliveryStatus == WorkflowRunTerminalNotificationDeliveryStatus.RetryScheduled &&
            retry.Attempt == State.TerminalNotificationAttempt;
        var recoversUncommittedSchedule =
            State.TerminalNotificationDeliveryStatus == WorkflowRunTerminalNotificationDeliveryStatus.Prepared &&
            retry.Attempt == State.TerminalNotificationAttempt + 1;
        if (!matchesIdentity || (!matchesScheduledRetry && !recoversUncommittedSchedule))
        {
            Logger.LogDebug(
                "Ignore stale workflow terminal notification retry. actor={ActorId} delivery={DeliveryId} command={CommandId} attempt={Attempt}",
                Id,
                retry.DeliveryId,
                retry.WorkflowCommandId,
                retry.Attempt);
            return;
        }

        if (recoversUncommittedSchedule)
        {
            await PersistDomainEventAsync(
                new WorkflowRunTerminalNotificationPreparedEvent
                {
                    Notification = pending!.Clone(),
                    Attempt = retry.Attempt,
                    PreparedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
                CancellationToken.None);
        }

        await AttemptPendingTerminalNotificationAsync(CancellationToken.None);
    }

    [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
    public async Task HandleWorkflowCompletionEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowCompletedEvent.Descriptor) != true)
            return;

        var completed = envelope.Payload.Unpack<WorkflowCompletedEvent>();
        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        if (await _subWorkflowOrchestrator.TryHandleCompletionAsync(
                completed,
                publisherActorId,
                State,
                CancellationToken.None))
        {
            return;
        }

        if (!string.Equals(publisherActorId, Id, StringComparison.Ordinal))
        {
            if (TryAdoptOwnRunRelayedTerminal(completed.RunId))
            {
                await AdoptRelayedWorkflowCompletedAsync(completed);
                return;
            }

            Logger.LogDebug(
                "Ignore external WorkflowCompletedEvent from publisher={PublisherId} run={RunId}.",
                publisherActorId,
                completed.RunId);
            return;
        }

        await HandleWorkflowCompleted(completed);
    }

    [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
    public async Task HandleWorkflowStoppedEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowStoppedEvent.Descriptor) != true)
            return;

        var stopped = envelope.Payload.Unpack<WorkflowStoppedEvent>();
        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        if (await _subWorkflowOrchestrator.TryHandleStoppedAsync(
                stopped,
                publisherActorId,
                State,
                CancellationToken.None))
        {
            return;
        }

        if (!string.Equals(publisherActorId, Id, StringComparison.Ordinal))
        {
            Logger.LogDebug(
                "Ignore external WorkflowStoppedEvent from publisher={PublisherId} run={RunId}.",
                publisherActorId,
                stopped.RunId);
            return;
        }

        await HandleWorkflowStopped(stopped);
    }

    [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
    public async Task HandleWorkflowRunStoppedEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowRunStoppedEvent.Descriptor) != true)
            return;

        var stopped = envelope.Payload.Unpack<WorkflowRunStoppedEvent>();
        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        if (await _subWorkflowOrchestrator.TryHandleRunStoppedAsync(
                stopped,
                publisherActorId,
                State,
                CancellationToken.None))
        {
            return;
        }

        if (!string.Equals(publisherActorId, Id, StringComparison.Ordinal))
        {
            Logger.LogDebug(
                "Ignore external WorkflowRunStoppedEvent from publisher={PublisherId} run={RunId}.",
                publisherActorId,
                stopped.RunId);
            return;
        }

        await HandleWorkflowRunStoppedAsync(stopped);
    }

    public async Task HandleWorkflowCompleted(WorkflowCompletedEvent evt, string? sessionId = null)
    {
        if (ShouldIgnoreWorkflowCompleted(State))
        {
            Logger.LogDebug(
                "Ignore duplicate WorkflowCompletedEvent for terminal run={RunId} status={Status}.",
                string.IsNullOrWhiteSpace(evt.RunId) ? RunId : evt.RunId,
                State.Status);
            await EnsureTerminalNotificationAsync(CancellationToken.None);
            return;
        }

        var stateBeforeCompletion = State.Clone();
        await PersistDomainEventAsync(evt);
        await TryRevokeScheduledCallerCredentialAsync(
            stateBeforeCompletion,
            "workflow-run-completed",
            CancellationToken.None);
        if (!ShouldSuppressGenericParentCompletion(stateBeforeCompletion))
            await PublishAsync(evt.Clone(), TopologyAudience.Parent);
        await PersistForkRequestOnTerminalFailureAsync(evt, stateBeforeCompletion, CancellationToken.None);
        await _subWorkflowOrchestrator.CancelPendingDefinitionResolutionTimeoutsAsync(stateBeforeCompletion, CancellationToken.None);
        await _subWorkflowOrchestrator.CleanupPendingInvocationsForRunAsync(evt.RunId, stateBeforeCompletion, CancellationToken.None);
        await CleanupRoleAgentTreeAsync(CancellationToken.None);
        _runtimeContext.Clear();
        DisableExecutionModules();
        if (evt.Success)
        {
            Logger.LogInformation(
                "Workflow run {Name} completed: success={Success} run={RunId} outputLen={OutputLen}",
                evt.WorkflowName,
                evt.Success,
                evt.RunId,
                (evt.Output ?? string.Empty).Length);
        }
        else
        {
            Logger.LogError(
                "Workflow run {Name} failed: run={RunId} error={Error} outputLen={OutputLen}",
                evt.WorkflowName,
                evt.RunId,
                string.IsNullOrWhiteSpace(evt.Error) ? "(none)" : evt.Error,
                (evt.Output ?? string.Empty).Length);
        }

        await PublishAsync(new WorkflowLlmInvocationCompletedEvent
        {
            RunId = evt.RunId,
            SessionId = sessionId?.Trim() ?? string.Empty,
            Success = evt.Success,
            Content = evt.Success ? evt.Output : $"Workflow execution failed: {evt.Error}",
            Error = evt.Success ? string.Empty : evt.Error,
        }, TopologyAudience.Parent);

        await PublishManagedParentInvocationCompletionAsync(evt, stateBeforeCompletion, CancellationToken.None);
        await EnsureTerminalNotificationAsync(CancellationToken.None);
    }

    // R1 (06-20-observatory-run-state-feed): a provisioned run delegates execution to an inner child
    // WorkflowRunGAgent that self-commits the COMPLETION; the relayed WorkflowCompletedEvent carries
    // publisher = inner, so the current-state projector gate skips it and the outer projection-root never
    // advances its own committed Status (stuck "running"). HandleWorkflowCompleted is not an [EventHandler],
    // so HandleWorkflowCompletionEnvelope ignores the non-self relay — hence the outer must adopt it.
    // (STOP/RUN-STOP are NOT affected: their typed [EventHandler]s fire for non-self publishers and run the
    // full CompleteStopAsync path, so they are not adopted here.) R1a enforces that a child sub-workflow run
    // id never equals the parent run id (SubWorkflowOrchestrator), so a relayed terminal whose RunId == this
    // run's RunId is necessarily this run's own.
    private bool TryAdoptOwnRunRelayedTerminal(string? relayedRunId)
    {
        // R1c started precondition: only adopt once the projection-root has applied bind/start, so the
        // adopted terminal does not set Status while RunId/ScopeId/StartedAtUtc are still blank.
        if (string.IsNullOrWhiteSpace(State.RunId) ||
            string.IsNullOrWhiteSpace(State.ScopeId) ||
            State.StartedAtUtc == null)
        {
            return false;
        }

        return string.Equals(
            WorkflowRunIdNormalizer.Normalize(relayedRunId),
            RunId,
            StringComparison.Ordinal);
    }

    // R1b status-only adopt: advance only the terminal WorkflowRunState (via the ApplyWorkflowCompleted
    // reducer) so the current-state projector gate passes (publisher becomes the root). It MUST NOT run
    // any HandleWorkflowCompleted cross-actor side effects (parent completion publish, fork handling,
    // sub-workflow/role cleanup, runtime clear, module disable, LLM-completion publish, managed-parent
    // completion) — the inner executor already emitted those for the actual execution.
    private async Task AdoptRelayedWorkflowCompletedAsync(WorkflowCompletedEvent evt)
    {
        if (ShouldIgnoreWorkflowCompleted(State))
        {
            Logger.LogDebug(
                "Skip adopting relayed WorkflowCompletedEvent for terminal run={RunId} status={Status}.",
                RunId,
                State.Status);
            await EnsureTerminalNotificationAsync(CancellationToken.None);
            return;
        }

        Logger.LogInformation(
            "Adopt relayed WorkflowCompletedEvent for own run={RunId} success={Success}.",
            RunId,
            evt.Success);
        await PersistDomainEventAsync(NormalizeAdoptedCompleted(evt));
        await EnsureTerminalNotificationAsync(CancellationToken.None);
    }

    private WorkflowCompletedEvent NormalizeAdoptedCompleted(WorkflowCompletedEvent evt)
    {
        var normalized = evt.Clone();
        normalized.RunId = RunId;
        if (string.IsNullOrWhiteSpace(normalized.WorkflowName))
            normalized.WorkflowName = State.WorkflowName;
        return normalized;
    }

    private static bool ShouldSuppressGenericParentCompletion(WorkflowRunState stateBeforeCompletion) =>
        IsManagedChildWorkflowCall(stateBeforeCompletion);

    private static bool IsManagedChildWorkflowCall(WorkflowRunState stateBeforeCompletion)
    {
        var runtime = stateBeforeCompletion.ExecutionContext?.WorkflowRuntime;
        return runtime != null &&
               !string.IsNullOrWhiteSpace(runtime.ParentActorId) &&
               !string.IsNullOrWhiteSpace(runtime.ParentRunId) &&
               !string.IsNullOrWhiteSpace(runtime.ParentStepId) &&
               !string.IsNullOrWhiteSpace(TryResolveWorkflowCallInvocationId(stateBeforeCompletion));
    }

    [EventHandler]
    public async Task HandleSubWorkflowInvocationCompleted(SubWorkflowInvocationCompletedEvent completed)
    {
        await _subWorkflowOrchestrator.HandleInvocationCompletedAsync(completed, State, CancellationToken.None);
    }

    private async Task PersistForkRequestOnTerminalFailureAsync(
        WorkflowCompletedEvent evt,
        WorkflowRunState stateBeforeCompletion,
        CancellationToken ct)
    {
        if (evt.Success || _compiledWorkflow?.OnFailure == null)
            return;

        var policy = _compiledWorkflow.OnFailure;
        if (!string.Equals(
                policy.Action,
                WorkflowRunFailureActions.ForkFromFailedStep,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var maxAttempts = Math.Max(0, policy.MaxAttempts);
        var currentAttempt = Math.Max(0, State.ForkAttempt);
        if (currentAttempt >= maxAttempts)
            return;

        var failedStepId = ResolveLastFailedStepId(State);
        if (string.IsNullOrWhiteSpace(failedStepId))
            failedStepId = ResolveLastFailedStepId(stateBeforeCompletion);
        if (string.IsNullOrWhiteSpace(failedStepId))
        {
            Logger.LogWarning(
                "Workflow run {RunId} failed with on_failure fork policy but no failed step id was available.",
                evt.RunId);
            return;
        }

        await PersistDomainEventAsync(
            new WorkflowRunForkRequestedEvent
            {
                SourceRunId = string.IsNullOrWhiteSpace(evt.RunId) ? RunId : WorkflowRunIdNormalizer.Normalize(evt.RunId),
                StartAtStepId = failedStepId,
                Attempt = currentAttempt + 1,
                ScopeId = State.ScopeId ?? string.Empty,
            },
            ct);
    }

    [EventHandler]
    public async Task HandleWorkflowStopped(WorkflowStoppedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!TryPrepareStop(evt.RunId, nameof(WorkflowStoppedEvent), out var runId))
        {
            if (IsTerminalStatus(State.Status))
                await EnsureTerminalNotificationAsync(CancellationToken.None);
            return;
        }

        var persistedEvent = new WorkflowStoppedEvent
        {
            WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? State.WorkflowName : evt.WorkflowName,
            RunId = runId,
            Reason = evt.Reason ?? string.Empty,
        };

        await CompleteStopAsync(
            runId,
            persistedEvent.WorkflowName,
            persistedEvent.Reason,
            ct => PersistDomainEventAsync(persistedEvent, ct),
            CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleWorkflowRunStoppedAsync(WorkflowRunStoppedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!TryPrepareStop(evt.RunId, nameof(WorkflowRunStoppedEvent), out var runId))
        {
            if (IsTerminalStatus(State.Status))
                await EnsureTerminalNotificationAsync(CancellationToken.None);
            return;
        }

        var persistedEvent = new WorkflowRunStoppedEvent
        {
            RunId = runId,
            Reason = evt.Reason ?? string.Empty,
        };

        await CompleteStopAsync(
            runId,
            State.WorkflowName,
            persistedEvent.Reason,
            ct => PersistDomainEventAsync(persistedEvent, ct),
            CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleWorkflowCompensationRetryRequestedAsync(WorkflowCompensationRetryRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var runId = WorkflowRunIdNormalizer.Normalize(evt.RunId);
        var failedStepId = evt.FailedCompensationStepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(failedStepId))
        {
            Logger.LogWarning(
                "Reject retry compensation request with missing run or failed step: run={RunId} step={StepId}.",
                evt.RunId,
                evt.FailedCompensationStepId);
            return;
        }

        if (!string.Equals(State.RunId, runId, StringComparison.Ordinal) ||
            State.SagaStatus != WorkflowSagaStatus.CompensationDeadLetter)
        {
            Logger.LogWarning(
                "Reject retry compensation request outside dead-letter state: run={RunId} status={SagaStatus}.",
                runId,
                State.SagaStatus);
            return;
        }

        if (!string.Equals(State.DeadLetterFailedCompensationStepId ?? string.Empty, failedStepId, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "Reject retry compensation request for mismatched failed step: run={RunId} expected={ExpectedStepId} requested={RequestedStepId}.",
                runId,
                State.DeadLetterFailedCompensationStepId,
                failedStepId);
            return;
        }

        if (!TryGetLedgerEntry(State.CompensationCursor, out var entry) ||
            !string.Equals(entry.CompensationStepId ?? string.Empty, failedStepId, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "Reject retry compensation request because failed compensation step does not match the dead-letter cursor: run={RunId} step={StepId}.",
                runId,
                failedStepId);
            return;
        }

        var executionId = Guid.NewGuid().ToString("N");
        var retryRequested = new WorkflowCompensationRetryRequestedEvent
        {
            RunId = runId,
            FailedCompensationStepId = failedStepId,
            Reason = evt.Reason ?? string.Empty,
            CommandId = evt.CommandId ?? ActiveInboundEnvelope?.Id ?? string.Empty,
            CorrelationId = evt.CorrelationId ?? ActiveInboundEnvelope?.Propagation?.CorrelationId ?? string.Empty,
        };
        var compensationRequest = new CompensationRequestEvent
        {
            RunId = runId,
            FailedStepId = ResolveLastFailedStepId(State),
            CompensationStepId = entry.CompensationStepId ?? string.Empty,
            IdempotencyKey = entry.IdempotencyKey ?? string.Empty,
            CapturedOutput = entry.CapturedOutput ?? string.Empty,
            ExecutionId = executionId,
        };

        await PersistDomainEventAsync(retryRequested);
        await PersistDomainEventAsync(compensationRequest);
        await PublishAsync(compensationRequest, TopologyAudience.Self);
    }

    [AllEventHandler(Priority = 40, AllowSelfHandling = true)]
    public async Task HandleWorkflowArtifactObservationEnvelope(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (WorkflowArtifactFactBuilder.TryBuild(envelope, Id, State.RunId, out var artifactFact))
            await PersistDomainEventAsync(artifactFact, CancellationToken.None);
    }

    private async Task CleanupRoleAgentTreeAsync(CancellationToken ct)
    {
        var roleActorIds = CollectRoleActorIds();
        if (roleActorIds.Count == 0)
            return;

        var remainingActorIds = new List<string>();
        foreach (var childActorId in roleActorIds)
        {
            try
            {
                await _runtime.UnlinkAsync(childActorId, ct);
                await _runtime.DestroyAsync(childActorId, ct);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "Failed to cleanup workflow role actor {ChildActorId} for run actor {ActorId}.",
                    childActorId,
                    Id);
                remainingActorIds.Add(childActorId);
            }
        }

        _childAgentIds.Clear();
        _childAgentIds.AddRange(remainingActorIds);
    }

    private IReadOnlyList<string> CollectRoleActorIds()
    {
        var roleActorIds = new HashSet<string>(_childAgentIds, StringComparer.Ordinal);
        if (_compiledWorkflow == null)
            return roleActorIds.ToList();

        foreach (var role in WorkflowImplicitLlmRolePolicy.GetEffectiveRoles(_compiledWorkflow))
        {
            roleActorIds.Add(BuildChildActorId(role.Id));
        }

        return roleActorIds.ToList();
    }

    private async Task EnsureAgentTreeAsync()
    {
        if (_childAgentIds.Count > 0 || _compiledWorkflow == null)
            return;

        foreach (var role in WorkflowImplicitLlmRolePolicy.GetEffectiveRoles(_compiledWorkflow))
        {
            var roleId = role.Id;
            var childActorId = BuildChildActorId(roleId);
            var actor = await _runtime.GetAsync(childActorId)
                        ?? await CreateRoleActorAsync(role, childActorId);
            await _runtime.LinkAsync(Id, actor.Id);

            await DispatchRoleInitializationAsync(actor.Id, WorkflowRoleAgentEnvelopeFactory.CreateInitializeEnvelope(role, Id, actor.Id));
            _childAgentIds.Add(actor.Id);
            await PersistDomainEventAsync(new WorkflowRoleActorLinkedEvent
            {
                RunId = string.IsNullOrWhiteSpace(State.RunId)
                    ? WorkflowRunIdNormalizer.Normalize(Id)
                    : WorkflowRunIdNormalizer.Normalize(State.RunId),
                RoleId = roleId,
                ChildActorId = actor.Id,
            });
        }

        Logger.LogInformation("Workflow run actor tree created: {Count} role agents", _childAgentIds.Count);
    }

    private Task<DispatchAdmission> DispatchRoleInitializationAsync(string actorId, EventEnvelope envelope) =>
        _dispatchPort.DispatchAsync(actorId, envelope);
    private async Task<IActor> CreateRoleActorAsync(RoleDefinition role, string childActorId)
    {
        var agentKind = string.IsNullOrWhiteSpace(role.AgentKind)
            ? WorkflowRoleConventions.DefaultAgentKind
            : role.AgentKind.Trim();
        return await _runtime.CreateByKindAsync(agentKind, childActorId);
    }

    private string BuildChildActorId(string roleId)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            throw new InvalidOperationException("Role id is required to create child actor.");

        return $"{Id}:{roleId.Trim()}";
    }

    private void InstallCognitiveModules()
    {
        if (_compiledWorkflow == null)
        {
            Logger.LogDebug("Workflow run definition is not bound yet; skipping module installation for actor {ActorId}.", Id);
            SetModules([]);
            return;
        }

        if (IsTerminalStatus(State.Status) && !IsCompensating(State))
        {
            Logger.LogDebug(
                "Workflow run is terminal; skipping module installation for actor {ActorId} status={Status}.",
                Id,
                State.Status);
            SetModules([]);
            return;
        }

        if (_moduleDependencyExpanders.Count == 0)
        {
            SetModules(
            [
                new WorkflowExecutionKernel(_compiledWorkflow, this),
                new WorkflowExecutionBridgeModule([], this),
            ]);
            return;
        }

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expander in _moduleDependencyExpanders)
            expander.Expand(_compiledWorkflow, needed);

        Logger.LogInformation("Installing workflow run modules: {Modules}", string.Join(", ", needed));

        var executors = new List<IEventModule<IWorkflowExecutionContext>>();
        foreach (var name in needed)
        {
            if (_stepExecutorFactory.TryCreate(name, out var module) && module != null)
            {
                ConfigureModule(module);
                executors.Add(module);
                continue;
            }

            var workflowName = _compiledWorkflow?.Name ?? State.WorkflowName;
            throw new InvalidOperationException(
                $"Workflow '{workflowName}' requires module '{name}', but no module registration was found.");
        }

        var workflowModules = new List<IEventModule<IEventHandlerContext>>
        {
            new WorkflowExecutionKernel(_compiledWorkflow, this),
            new WorkflowExecutionBridgeModule(
                executors,
                this),
        };
        SetModules(workflowModules);
    }

    private void DisableExecutionModules() => SetModules([]);

    private void ConfigureModule(IEventModule<IWorkflowExecutionContext> module)
    {
        if (_compiledWorkflow == null)
            return;

        foreach (var configurator in _moduleConfigurators)
            configurator.Configure(module, _compiledWorkflow);
    }

    protected override WorkflowRunState TransitionState(WorkflowRunState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<BindWorkflowRunDefinitionEvent>(ApplyBindWorkflowRunDefinition)
            .On<WorkflowRunCompletionNotificationTargetAdoptedEvent>(ApplyWorkflowRunCompletionNotificationTargetAdopted)
            .On<WorkflowCommandObservedEvent>(ApplyWorkflowCommandObserved)
            .On<WorkflowRunExecutionStartedEvent>(ApplyWorkflowRunExecutionStarted)
            .On<WorkflowRunExecutionContextUpdatedEvent>(ApplyWorkflowRunExecutionContextUpdated)
            .On<WorkflowRunExecutionContextClearedEvent>(ApplyWorkflowRunExecutionContextCleared)
            .On<WorkflowExecutionStateUpsertedEvent>(ApplyWorkflowExecutionStateUpserted)
            .On<WorkflowExecutionStateClearedEvent>(ApplyWorkflowExecutionStateCleared)
            .On<CompensableStepDispatchedEvent>(ApplyCompensableStepDispatched)
            .On<StepCompletedEvent>(ApplyStepCompleted)
            .On<CompensationRequestEvent>(ApplyCompensationRequest)
            .On<CompensationStepCompletedEvent>(ApplyCompensationStepCompleted)
            .On<WorkflowCompensationCompletedEvent>(ApplyWorkflowCompensationCompleted)
            .On<WorkflowCompensationFailedEvent>(ApplyWorkflowCompensationFailed)
            .On<WorkflowCompensationRetryRequestedEvent>(ApplyWorkflowCompensationRetryRequested)
            .On<WorkflowStoppedEvent>(ApplyWorkflowStopped)
            .On<WorkflowCompletedEvent>(ApplyWorkflowCompleted)
            .On<WorkflowRunStoppedEvent>(ApplyWorkflowRunStopped)
            .On<WorkflowRunTerminalNotificationPreparedEvent>(ApplyWorkflowRunTerminalNotificationPrepared)
            .On<WorkflowRunTerminalNotificationRetryScheduledEvent>(ApplyWorkflowRunTerminalNotificationRetryScheduled)
            .On<WorkflowRunTerminalNotificationDispatchedEvent>(ApplyWorkflowRunTerminalNotificationDispatched)
            .On<WorkflowRunTerminalNotificationExpiredEvent>(ApplyWorkflowRunTerminalNotificationExpired)
            .On<WorkflowRunTerminalNotificationRetryFiredEvent>(KeepCurrentState)
            .On<SubWorkflowDefinitionResolutionRegisteredEvent>(SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionRegistered)
            .On<SubWorkflowDefinitionResolvedEvent>(KeepCurrentState)
            .On<SubWorkflowDefinitionResolveFailedEvent>(KeepCurrentState)
            .On<SubWorkflowDefinitionResolutionTimeoutFiredEvent>(KeepCurrentState)
            .On<SubWorkflowDefinitionResolutionClearedEvent>(SubWorkflowOrchestrator.ApplySubWorkflowDefinitionResolutionCleared)
            .On<SubWorkflowBindingUpsertedEvent>(SubWorkflowOrchestrator.ApplySubWorkflowBindingUpserted)
            .On<SubWorkflowInvocationRegisteredEvent>(SubWorkflowOrchestrator.ApplySubWorkflowInvocationRegistered)
            .On<SubWorkflowInvocationHandoffAdvancedEvent>(SubWorkflowOrchestrator.ApplySubWorkflowInvocationHandoffAdvanced)
            .On<SubWorkflowInvocationCompletedEvent>(SubWorkflowOrchestrator.ApplySubWorkflowInvocationCompleted)
            .OrCurrent();

    private WorkflowRunState ApplyBindWorkflowRunDefinition(WorkflowRunState current, BindWorkflowRunDefinitionEvent evt)
    {
        var next = current.Clone();
        next.DefinitionActorId = evt.DefinitionActorId?.Trim() ?? string.Empty;
        next.WorkflowYaml = evt.WorkflowYaml ?? string.Empty;
        next.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName)
            ? current.WorkflowName
            : evt.WorkflowName.Trim();
        next.RunId = string.IsNullOrWhiteSpace(evt.RunId)
            ? (string.IsNullOrWhiteSpace(current.RunId) ? Id : current.RunId)
            : WorkflowRunIdNormalizer.Normalize(evt.RunId);
        next.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId)
            ? current.ScopeId
            : evt.ScopeId.Trim();
        next.RunOrigin = string.IsNullOrWhiteSpace(evt.RunOrigin)
            ? current.RunOrigin
            : evt.RunOrigin.Trim();
        next.ScheduleId = string.IsNullOrWhiteSpace(evt.ScheduleId)
            ? current.ScheduleId
            : evt.ScheduleId.Trim();
        next.CapabilityAdmissionPlan = evt.CapabilityAdmissionPlan?.Clone();
        next.ExpectedExecutionMode = evt.ExpectedExecutionMode;
        next.Status = "bound";
        next.Input = string.Empty;
        next.FinalOutput = string.Empty;
        next.FinalError = string.Empty;
        next.ForkAttempt = 0;
        next.CompensableLedger.Clear();
        next.CompensationCursor = 0;
        next.SagaStatus = WorkflowSagaStatus.Unspecified;
        next.CompensationExecutionId = string.Empty;
        next.DeadLetterFailedCompensationStepId = string.Empty;
        next.DeadLetterRemainingUncompensated = 0;
        next.DeadLetterError = string.Empty;
        next.CompensationOriginFailedStepId = string.Empty;
        next.TerminalWorkflowCompletionRecorded = false;
        next.ExecutionStates.Clear();
        next.ExecutionContext = new WorkflowRunExecutionContextState();
        next.SubWorkflowBindings.Clear();
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        next.PendingSubWorkflowInvocations.Clear();
        next.PendingSubWorkflowInvocationIndexByChildRunId.Clear();
        next.PendingChildRunIdsByParentRunId.Clear();
        next.LastCommandId = string.Empty;
        next.CompletionNotificationTarget = null;
        next.WorkflowCorrelationId = string.Empty;
        next.PendingTerminalNotification = null;
        next.TerminalNotificationAttempt = 0;
        next.TerminalNotificationDeliveryStatus = WorkflowRunTerminalNotificationDeliveryStatus.Unspecified;
        next.TerminalNotificationRetryCallbackId = string.Empty;
        next.InlineWorkflowYamls.Clear();
        foreach (var (workflowNameKey, workflowYamlValue) in evt.InlineWorkflowYamls)
        {
            var normalizedWorkflowName = WorkflowRunIdNormalizer.NormalizeWorkflowName(workflowNameKey);
            if (string.IsNullOrWhiteSpace(normalizedWorkflowName) ||
                string.IsNullOrWhiteSpace(workflowYamlValue))
            {
                continue;
            }

            next.InlineWorkflowYamls[normalizedWorkflowName] = workflowYamlValue;
        }

        var compileResult = EvaluateWorkflowCompilation(next.WorkflowYaml);
        next.Compiled = compileResult.Compiled;
        next.CompilationError = compileResult.CompilationError;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunCompletionNotificationTargetAdopted(
        WorkflowRunState current,
        WorkflowRunCompletionNotificationTargetAdoptedEvent evt)
    {
        if (evt.CompletionNotificationTarget == null)
            return current;

        var next = current.Clone();
        var sameDelivery =
            string.Equals(
                current.CompletionNotificationTarget?.DeliveryId,
                evt.CompletionNotificationTarget.DeliveryId,
                StringComparison.Ordinal) &&
            string.Equals(current.LastCommandId, evt.WorkflowCommandId, StringComparison.Ordinal);
        next.CompletionNotificationTarget = evt.CompletionNotificationTarget.Clone();
        next.RunId = string.IsNullOrWhiteSpace(evt.WorkflowRunId)
            ? current.RunId
            : WorkflowRunIdNormalizer.Normalize(evt.WorkflowRunId);
        next.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId)
            ? current.ScopeId
            : evt.ScopeId.Trim();
        next.LastCommandId = evt.WorkflowCommandId?.Trim() ?? string.Empty;
        next.WorkflowCorrelationId = evt.WorkflowCorrelationId?.Trim() ?? string.Empty;
        if (!sameDelivery)
        {
            next.PendingTerminalNotification = null;
            next.TerminalNotificationAttempt = 0;
            next.TerminalNotificationDeliveryStatus = WorkflowRunTerminalNotificationDeliveryStatus.Unspecified;
            next.TerminalNotificationRetryCallbackId = string.Empty;
        }

        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunExecutionStarted(WorkflowRunState current, WorkflowRunExecutionStartedEvent evt)
    {
        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? current.RunId : WorkflowRunIdNormalizer.Normalize(evt.RunId);
        next.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? current.WorkflowName : evt.WorkflowName.Trim();
        next.Input = evt.Input ?? string.Empty;
        next.Status = RunningStatus;
        next.FinalOutput = string.Empty;
        next.FinalError = string.Empty;
        next.ForkAttempt = Math.Max(0, evt.Attempt);
        next.CompensableLedger.Clear();
        next.CompensationCursor = 0;
        next.SagaStatus = WorkflowSagaStatus.Unspecified;
        next.CompensationExecutionId = string.Empty;
        next.DeadLetterFailedCompensationStepId = string.Empty;
        next.DeadLetterRemainingUncompensated = 0;
        next.DeadLetterError = string.Empty;
        next.CompensationOriginFailedStepId = string.Empty;
        next.TerminalWorkflowCompletionRecorded = false;
        next.CompletionNotificationTarget = evt.CompletionNotificationTarget?.Clone();
        next.LastCommandId = string.IsNullOrWhiteSpace(evt.WorkflowCommandId)
            ? current.LastCommandId
            : evt.WorkflowCommandId.Trim();
        next.WorkflowCorrelationId = evt.WorkflowCorrelationId?.Trim() ?? string.Empty;
        next.PendingTerminalNotification = null;
        next.TerminalNotificationAttempt = 0;
        next.TerminalNotificationDeliveryStatus = WorkflowRunTerminalNotificationDeliveryStatus.Unspecified;
        next.TerminalNotificationRetryCallbackId = string.Empty;
        next.ExecutionContext ??= new WorkflowRunExecutionContextState();
        ApplyExecutionContextDelta(next.ExecutionContext, evt.ExecutionContextDelta);
        if (string.IsNullOrWhiteSpace(next.DefinitionActorId) && !string.IsNullOrWhiteSpace(evt.DefinitionActorId))
            next.DefinitionActorId = evt.DefinitionActorId.Trim();
        if (string.IsNullOrWhiteSpace(next.ScopeId) && !string.IsNullOrWhiteSpace(evt.ScopeId))
            next.ScopeId = evt.ScopeId.Trim();
        // O2 (06-19-workflow-run-observatory): record the run-start fact once; fork re-runs keep the original.
        if (next.StartedAtUtc == null && evt.StartedAtUtc != null)
            next.StartedAtUtc = evt.StartedAtUtc;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunExecutionContextUpdated(
        WorkflowRunState current,
        WorkflowRunExecutionContextUpdatedEvent evt)
    {
        var next = current.Clone();
        next.ExecutionContext ??= new WorkflowRunExecutionContextState();
        ApplyExecutionContextDelta(next.ExecutionContext, evt.ExecutionContextDelta);
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunExecutionContextCleared(
        WorkflowRunState current,
        WorkflowRunExecutionContextClearedEvent _)
    {
        var next = current.Clone();
        next.ExecutionContext = new WorkflowRunExecutionContextState();
        return next;
    }

    private static WorkflowRunExecutionContextDelta MergeExecutionContextDeltas(
        params WorkflowRunExecutionContextDelta[] deltas)
    {
        var merged = new WorkflowRunExecutionContextDelta();
        foreach (var delta in deltas)
        {
            if (delta.ClearLlm)
                merged.ClearLlm = true;
            if (delta.ClearCallerCredential)
                merged.ClearCallerCredential = true;
            if (delta.Llm != null)
                merged.Llm = delta.Llm.Clone();
            if (delta.CallerCredential != null)
                merged.CallerCredential = delta.CallerCredential.Clone();
            if (delta.ClearWorkflowRuntime)
                merged.ClearWorkflowRuntime = true;
            if (delta.WorkflowRuntime != null)
                merged.WorkflowRuntime = delta.WorkflowRuntime.Clone();
        }

        return merged;
    }

    private static void ApplyExecutionContextDelta(
        WorkflowRunExecutionContextState state,
        WorkflowRunExecutionContextDelta? delta)
    {
        if (delta == null)
            return;

        if (delta.ClearLlm)
            state.Llm = null;
        if (delta.ClearCallerCredential)
            state.CallerCredential = null;
        if (delta.ClearWorkflowRuntime)
            state.WorkflowRuntime = null;

        if (delta.Llm != null)
        {
            state.Llm = new WorkflowLlmExecutionContextState
            {
                ModelOverride = delta.Llm.ModelOverride?.Trim() ?? string.Empty,
                UserMemoryPrompt = delta.Llm.UserMemoryPrompt?.Trim() ?? string.Empty,
                RoutePreference = delta.Llm.RoutePreference?.Trim() ?? string.Empty,
            };
            if (delta.Llm.HasMaxToolRoundsOverride)
                state.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
        }

        if (delta.CallerCredential != null)
        {
            var parsed = WorkflowCallerCredentialTokens.ParseOptional(delta.CallerCredential.BearerToken);
            var sourceReadable = WorkflowCallerCredentialTokens.ParseOptional(
                delta.CallerCredential.SourceReadableUserBearerToken);
            state.CallerCredential = new WorkflowCallerCredentialState
            {
                BearerToken = parsed.IsValid ? parsed.NormalizedBearerToken ?? string.Empty : string.Empty,
                SourceReadableUserBearerToken = sourceReadable.IsValid
                    ? sourceReadable.NormalizedBearerToken ?? string.Empty
                    : string.Empty,
                RuntimeSecretReference = delta.CallerCredential.RuntimeSecretReference?.Clone(),
                SourceReadableUserBearerRuntimeSecretReference =
                    delta.CallerCredential.SourceReadableUserBearerRuntimeSecretReference?.Clone(),
                DurableCallerCredential = delta.CallerCredential.DurableCallerCredential?.Clone(),
                NyxIdAuthority = delta.CallerCredential.NyxIdAuthority?.Clone(),
                Kind = delta.CallerCredential.Kind,
            };
        }

        if (delta.WorkflowRuntime != null)
        {
            state.WorkflowRuntime = new WorkflowToolRuntimeContextState
            {
                ParentActorId = delta.WorkflowRuntime.ParentActorId?.Trim() ?? string.Empty,
                ParentRunId = WorkflowRunIdNormalizer.Normalize(delta.WorkflowRuntime.ParentRunId),
                ParentStepId = delta.WorkflowRuntime.ParentStepId?.Trim() ?? string.Empty,
                RootRunId = WorkflowRunIdNormalizer.Normalize(delta.WorkflowRuntime.RootRunId),
                Depth = Math.Max(0, delta.WorkflowRuntime.Depth),
            };
        }
    }

    private static WorkflowRunState ApplyWorkflowCommandObserved(WorkflowRunState current, WorkflowCommandObservedEvent evt)
    {
        var next = current.Clone();
        next.LastCommandId = evt.CommandId?.Trim() ?? string.Empty;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowExecutionStateUpserted(WorkflowRunState current, WorkflowExecutionStateUpsertedEvent evt)
    {
        var next = current.Clone();
        var scopeKey = evt.ScopeKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(scopeKey) || evt.State == null)
            return next;

        next.ExecutionStates[scopeKey] = evt.State;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowExecutionStateCleared(WorkflowRunState current, WorkflowExecutionStateClearedEvent evt)
    {
        var next = current.Clone();
        var scopeKey = evt.ScopeKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(scopeKey))
            return next;

        next.ExecutionStates.Remove(scopeKey);
        return next;
    }

    private WorkflowRunState ApplyStepCompleted(WorkflowRunState current, StepCompletedEvent evt)
    {
        var next = current.Clone();
        var stepId = evt.StepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stepId))
            return next;

        var workflow = ResolveWorkflowForTransition(current);
        var step = string.IsNullOrWhiteSpace(stepId) ? null : workflow?.GetStep(stepId);
        var compensationStepId = step?.Compensation?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(compensationStepId))
            return next;

        var idempotencyKey = ResolveStepCompletionIdempotency(evt, current, stepId);
        if (!evt.Success)
        {
            var failureOutcome = NormalizeFailureOutcome(evt.FailureOutcome);
            if (failureOutcome == WorkflowStepFailureOutcome.CalleeConfirmed)
                RemoveMatchingProvisionalLedgerEntries(next, stepId, compensationStepId, idempotencyKey);

            return next;
        }

        var matchingProvisional = next.CompensableLedger.FirstOrDefault(entry =>
            IsProvisional(entry) &&
            string.Equals(entry.StepId, stepId, StringComparison.Ordinal) &&
            string.Equals(entry.CompensationStepId, compensationStepId, StringComparison.Ordinal) &&
            string.Equals(entry.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
        if (matchingProvisional != null)
        {
            matchingProvisional.CapturedOutput = evt.Output ?? string.Empty;
            matchingProvisional.LedgerStatus = CompensableLedgerEntryStatus.Confirmed;
            return next;
        }

        if (next.CompensableLedger.Any(entry =>
                string.Equals(entry.StepId, stepId, StringComparison.Ordinal) &&
                string.Equals(entry.CompensationStepId, compensationStepId, StringComparison.Ordinal) &&
                string.Equals(entry.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)))
        {
            return next;
        }

        next.CompensableLedger.Add(new CompletedStepLedgerEntry
        {
            StepId = stepId,
            CompensationStepId = compensationStepId,
            IdempotencyKey = idempotencyKey,
            CapturedOutput = evt.Output ?? string.Empty,
            LedgerStatus = CompensableLedgerEntryStatus.Confirmed,
        });
        return next;
    }

    private WorkflowRunState BuildCompensationStartState(WorkflowRunState current, StepCompletedEvent? terminalStep)
    {
        if (terminalStep == null || terminalStep.Success)
            return current;

        if (NormalizeFailureOutcome(terminalStep.FailureOutcome) == WorkflowStepFailureOutcome.OutcomeUncertain)
            return current;

        return ApplyStepCompleted(current, terminalStep);
    }

    private static WorkflowRunState ApplyCompensableStepDispatched(
        WorkflowRunState current,
        CompensableStepDispatchedEvent evt)
    {
        var next = current.Clone();
        var stepId = evt.StepId?.Trim() ?? string.Empty;
        var compensationStepId = evt.CompensationStepId?.Trim() ?? string.Empty;
        var idempotencyKey = evt.IdempotencyKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stepId) || string.IsNullOrWhiteSpace(compensationStepId))
            return next;

        if (next.CompensableLedger.Any(entry =>
                string.Equals(entry.StepId, stepId, StringComparison.Ordinal) &&
                string.Equals(entry.CompensationStepId, compensationStepId, StringComparison.Ordinal) &&
                string.Equals(entry.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)))
        {
            return next;
        }

        next.CompensableLedger.Add(new CompletedStepLedgerEntry
        {
            StepId = stepId,
            CompensationStepId = compensationStepId,
            IdempotencyKey = idempotencyKey,
            CapturedOutput = string.Empty,
            LedgerStatus = CompensableLedgerEntryStatus.Provisional,
        });
        return next;
    }

    private static WorkflowRunState ApplyCompensationRequest(WorkflowRunState current, CompensationRequestEvent evt)
    {
        var next = current.Clone();
        var compensationStepId = evt.CompensationStepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(compensationStepId))
            return next;

        next.SagaStatus = WorkflowSagaStatus.Compensating;
        next.CompensationExecutionId = evt.ExecutionId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.FailedStepId))
            next.CompensationOriginFailedStepId = evt.FailedStepId.Trim();
        for (var i = next.CompensableLedger.Count - 1; i >= 0; i--)
        {
            var ledgerEntry = next.CompensableLedger[i];
            if (string.Equals(ledgerEntry.CompensationStepId, compensationStepId, StringComparison.Ordinal) &&
                string.Equals(ledgerEntry.IdempotencyKey ?? string.Empty, evt.IdempotencyKey ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(ledgerEntry.CapturedOutput ?? string.Empty, evt.CapturedOutput ?? string.Empty, StringComparison.Ordinal))
            {
                next.CompensationCursor = i;
                break;
            }
        }

        return next;
    }

    private static WorkflowRunState ApplyCompensationStepCompleted(
        WorkflowRunState current,
        CompensationStepCompletedEvent evt)
    {
        var next = current.Clone();
        if (!IsCompensating(next))
            return next;

        if (!evt.Success)
        {
            next.CompensationExecutionId = string.Empty;
            return next;
        }

        next.CompensationCursor -= 1;
        next.CompensationExecutionId = string.Empty;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowCompensationCompleted(
        WorkflowRunState current,
        WorkflowCompensationCompletedEvent evt)
    {
        var next = current.Clone();
        next.SagaStatus = WorkflowSagaStatus.CompensatedFailed;
        next.CompensationCursor = -1;
        next.CompensationExecutionId = string.Empty;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowCompensationFailed(
        WorkflowRunState current,
        WorkflowCompensationFailedEvent evt)
    {
        var next = current.Clone();
        next.Status = FailedStatus;
        next.FinalOutput = string.Empty;
        next.FinalError = evt.Error ?? string.Empty;
        next.SagaStatus = WorkflowSagaStatus.CompensationDeadLetter;
        next.CompensationExecutionId = string.Empty;
        next.DeadLetterFailedCompensationStepId = evt.FailedCompensationStepId ?? string.Empty;
        next.DeadLetterRemainingUncompensated = Math.Max(0, evt.RemainingUncompensated);
        next.DeadLetterError = evt.Error ?? string.Empty;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowCompensationRetryRequested(
        WorkflowRunState current,
        WorkflowCompensationRetryRequestedEvent evt)
    {
        var next = current.Clone();
        if (next.SagaStatus != WorkflowSagaStatus.CompensationDeadLetter)
            return next;

        var failedStepId = evt.FailedCompensationStepId?.Trim() ?? string.Empty;
        if (!string.Equals(next.DeadLetterFailedCompensationStepId ?? string.Empty, failedStepId, StringComparison.Ordinal))
            return next;

        next.SagaStatus = WorkflowSagaStatus.Compensating;
        next.DeadLetterFailedCompensationStepId = string.Empty;
        next.DeadLetterRemainingUncompensated = 0;
        next.DeadLetterError = string.Empty;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowStopped(WorkflowRunState current, WorkflowStoppedEvent evt)
    {
        var next = current.Clone();
        next.Status = StoppedStatus;
        next.FinalOutput = string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Reason))
            next.FinalError = evt.Reason;
        next.ExecutionStates.Clear();
        next.ExecutionContext = new WorkflowRunExecutionContextState();
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        next.PendingSubWorkflowInvocations.Clear();
        next.PendingSubWorkflowInvocationIndexByChildRunId.Clear();
        next.PendingChildRunIdsByParentRunId.Clear();
        return next;
    }

    private static WorkflowRunState ApplyWorkflowCompleted(WorkflowRunState current, WorkflowCompletedEvent evt)
    {
        var next = current.Clone();
        next.Status = evt.Success ? CompletedStatus : FailedStatus;
        next.FinalOutput = evt.Output ?? string.Empty;
        next.FinalError = evt.Error ?? string.Empty;
        next.TerminalWorkflowCompletionRecorded = true;
        next.ExecutionContext = new WorkflowRunExecutionContextState();
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunStopped(WorkflowRunState current, WorkflowRunStoppedEvent evt)
    {
        var next = current.Clone();
        next.Status = StoppedStatus;
        next.FinalOutput = string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Reason))
            next.FinalError = evt.Reason;
        next.ExecutionStates.Clear();
        next.ExecutionContext = new WorkflowRunExecutionContextState();
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        next.PendingSubWorkflowInvocations.Clear();
        next.PendingSubWorkflowInvocationIndexByChildRunId.Clear();
        next.PendingChildRunIdsByParentRunId.Clear();
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunTerminalNotificationPrepared(
        WorkflowRunState current,
        WorkflowRunTerminalNotificationPreparedEvent evt)
    {
        if (evt.Notification == null)
            return current;

        var next = current.Clone();
        next.PendingTerminalNotification = evt.Notification.Clone();
        next.TerminalNotificationAttempt = Math.Max(0, evt.Attempt);
        next.TerminalNotificationDeliveryStatus = WorkflowRunTerminalNotificationDeliveryStatus.Prepared;
        next.TerminalNotificationRetryCallbackId = string.Empty;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunTerminalNotificationRetryScheduled(
        WorkflowRunState current,
        WorkflowRunTerminalNotificationRetryScheduledEvent evt)
    {
        var next = current.Clone();
        if (!MatchesPendingTerminalNotification(next, evt.DeliveryId, evt.WorkflowCommandId) ||
            evt.Attempt <= next.TerminalNotificationAttempt)
        {
            return next;
        }

        next.TerminalNotificationAttempt = evt.Attempt;
        next.TerminalNotificationDeliveryStatus = WorkflowRunTerminalNotificationDeliveryStatus.RetryScheduled;
        next.TerminalNotificationRetryCallbackId = evt.CallbackId ?? string.Empty;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunTerminalNotificationDispatched(
        WorkflowRunState current,
        WorkflowRunTerminalNotificationDispatchedEvent evt)
    {
        var next = current.Clone();
        if (!MatchesPendingTerminalNotification(next, evt.DeliveryId, evt.WorkflowCommandId) ||
            evt.Attempt != next.TerminalNotificationAttempt)
        {
            return next;
        }

        next.PendingTerminalNotification = null;
        next.TerminalNotificationDeliveryStatus = WorkflowRunTerminalNotificationDeliveryStatus.Dispatched;
        next.TerminalNotificationRetryCallbackId = string.Empty;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunTerminalNotificationExpired(
        WorkflowRunState current,
        WorkflowRunTerminalNotificationExpiredEvent evt)
    {
        var next = current.Clone();
        if (!MatchesPendingTerminalNotification(next, evt.DeliveryId, evt.WorkflowCommandId) ||
            evt.Attempt != next.TerminalNotificationAttempt)
        {
            return next;
        }

        next.PendingTerminalNotification = null;
        next.TerminalNotificationDeliveryStatus = WorkflowRunTerminalNotificationDeliveryStatus.Expired;
        next.TerminalNotificationRetryCallbackId = string.Empty;
        return next;
    }

    private static bool MatchesPendingTerminalNotification(
        WorkflowRunState state,
        string? deliveryId,
        string? workflowCommandId) =>
        state.PendingTerminalNotification != null &&
        string.Equals(state.PendingTerminalNotification.DeliveryId, deliveryId, StringComparison.Ordinal) &&
        string.Equals(state.PendingTerminalNotification.WorkflowCommandId, workflowCommandId, StringComparison.Ordinal);

    private static WorkflowRunState KeepCurrentState(WorkflowRunState current, SubWorkflowDefinitionResolvedEvent _) => current;

    private static WorkflowRunState KeepCurrentState(WorkflowRunState current, SubWorkflowDefinitionResolveFailedEvent _) => current;

    private static WorkflowRunState KeepCurrentState(WorkflowRunState current, SubWorkflowDefinitionResolutionTimeoutFiredEvent _) => current;

    private static WorkflowRunState KeepCurrentState(WorkflowRunState current, WorkflowRunTerminalNotificationRetryFiredEvent _) => current;

    private static bool IsTerminalStatus(string? status) =>
        string.Equals(status, CompletedStatus, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, FailedStatus, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, StoppedStatus, StringComparison.OrdinalIgnoreCase);

    private static bool ShouldIgnoreWorkflowCompleted(WorkflowRunState state) =>
        state.TerminalWorkflowCompletionRecorded ||
        (IsTerminalStatus(state.Status) &&
         state.SagaStatus != WorkflowSagaStatus.CompensationDeadLetter);

    private async Task RecoverTerminalNotificationAsync(CancellationToken ct)
    {
        if (!IsTerminalStatus(State.Status) || !HasCompletionNotificationTarget(State.CompletionNotificationTarget))
            return;

        if (State.TerminalNotificationDeliveryStatus is
            WorkflowRunTerminalNotificationDeliveryStatus.Dispatched or
            WorkflowRunTerminalNotificationDeliveryStatus.Expired)
        {
            return;
        }

        await EnsureTerminalNotificationAsync(ct);
    }

    private async Task EnsureTerminalNotificationAsync(CancellationToken ct)
    {
        var target = State.CompletionNotificationTarget;
        if (!IsTerminalStatus(State.Status) || !HasCompletionNotificationTarget(target))
            return;

        if (State.TerminalNotificationDeliveryStatus is
            WorkflowRunTerminalNotificationDeliveryStatus.Dispatched or
            WorkflowRunTerminalNotificationDeliveryStatus.Expired)
        {
            return;
        }

        if (State.PendingTerminalNotification == null)
        {
            var terminalStatus = ResolveTerminalNotificationStatus(State.Status);
            if (terminalStatus == WorkflowRunTerminalStatus.Unspecified)
                return;

            await PersistDomainEventAsync(
                new WorkflowRunTerminalNotificationPreparedEvent
                {
                    Notification = new WorkflowRunTerminalNotification
                    {
                        DeliveryId = target!.DeliveryId.Trim(),
                        WorkflowActorId = Id,
                        WorkflowRunId = RunId,
                        WorkflowCommandId = State.LastCommandId?.Trim() ?? string.Empty,
                        WorkflowCorrelationId = State.WorkflowCorrelationId?.Trim() ?? string.Empty,
                        Status = terminalStatus,
                        Output = State.FinalOutput ?? string.Empty,
                        Error = State.FinalError ?? string.Empty,
                        TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    },
                    Attempt = 0,
                    PreparedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
                ct);
        }

        await AttemptPendingTerminalNotificationAsync(ct);
    }

    private async Task AttemptPendingTerminalNotificationAsync(CancellationToken ct)
    {
        var target = State.CompletionNotificationTarget?.Clone();
        var pending = State.PendingTerminalNotification?.Clone();
        if (!HasCompletionNotificationTarget(target) || pending == null)
            return;

        if (!string.Equals(target!.DeliveryId, pending.DeliveryId, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "Workflow terminal notification target does not match the pending outbox. actor={ActorId} targetDelivery={TargetDeliveryId} pendingDelivery={PendingDeliveryId}",
                Id,
                target.DeliveryId,
                pending.DeliveryId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (target.ExpiresAtUnixMs <= now.ToUnixTimeMilliseconds())
        {
            await PersistDomainEventAsync(
                new WorkflowRunTerminalNotificationExpiredEvent
                {
                    DeliveryId = pending.DeliveryId,
                    WorkflowCommandId = pending.WorkflowCommandId,
                    Attempt = State.TerminalNotificationAttempt,
                    ExpiredAt = Timestamp.FromDateTimeOffset(now),
                },
                ct);
            return;
        }

        var attempt = State.TerminalNotificationAttempt;
        try
        {
            await SendToAsync(
                target.ActorId.Trim(),
                pending,
                ct,
                BuildTerminalNotificationDispatchOptions(pending));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Workflow terminal notification dispatch failed; scheduling actor-owned retry. actor={ActorId} target={TargetActorId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                target.ActorId,
                pending.DeliveryId,
                attempt);
            await ScheduleTerminalNotificationRetryAsync(target, pending, attempt + 1, now, ct);
            return;
        }

        await PersistDomainEventAsync(
            new WorkflowRunTerminalNotificationDispatchedEvent
            {
                DeliveryId = pending.DeliveryId,
                WorkflowCommandId = pending.WorkflowCommandId,
                Attempt = attempt,
                DispatchedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            ct);
    }

    private async Task ScheduleTerminalNotificationRetryAsync(
        WorkflowCompletionNotificationTarget target,
        WorkflowRunTerminalNotification pending,
        int attempt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var remainingMs = target.ExpiresAtUnixMs - now.ToUnixTimeMilliseconds();
        if (remainingMs <= 0)
        {
            await PersistDomainEventAsync(
                new WorkflowRunTerminalNotificationExpiredEvent
                {
                    DeliveryId = pending.DeliveryId,
                    WorkflowCommandId = pending.WorkflowCommandId,
                    Attempt = State.TerminalNotificationAttempt,
                    ExpiredAt = Timestamp.FromDateTimeOffset(now),
                },
                ct);
            return;
        }

        var delay = ResolveTerminalNotificationRetryDelay(attempt, remainingMs);
        var callbackId = BuildTerminalNotificationRetryCallbackId(pending, attempt);
        var retryFired = new WorkflowRunTerminalNotificationRetryFiredEvent
        {
            DeliveryId = pending.DeliveryId,
            WorkflowActorId = Id,
            WorkflowCommandId = pending.WorkflowCommandId,
            Attempt = attempt,
        };
        var retryOptions = BuildTerminalNotificationRetryOptions(callbackId);
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                callbackId,
                delay,
                retryFired,
                retryOptions,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var canPublishImmediateRecovery =
                State.TerminalNotificationDeliveryStatus == WorkflowRunTerminalNotificationDeliveryStatus.Prepared &&
                State.TerminalNotificationAttempt == 0 &&
                attempt == 1;
            Logger.LogWarning(
                ex,
                canPublishImmediateRecovery
                    ? "Workflow terminal notification durable retry scheduling failed; publishing one immediate recovery continuation. actor={ActorId} delivery={DeliveryId} attempt={Attempt}"
                    : "Workflow terminal notification durable retry scheduling failed; preserving the outbox for activation recovery. actor={ActorId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                pending.DeliveryId,
                attempt);
            if (canPublishImmediateRecovery)
                await SendToAsync(Id, retryFired, ct, retryOptions);
            return;
        }

        await PersistDomainEventAsync(
            new WorkflowRunTerminalNotificationRetryScheduledEvent
            {
                DeliveryId = pending.DeliveryId,
                WorkflowCommandId = pending.WorkflowCommandId,
                Attempt = attempt,
                CallbackId = callbackId,
                RetryAt = Timestamp.FromDateTimeOffset(now.Add(delay)),
            },
            ct);
    }

    private static WorkflowRunTerminalStatus ResolveTerminalNotificationStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            CompletedStatus => WorkflowRunTerminalStatus.Completed,
            FailedStatus => WorkflowRunTerminalStatus.Failed,
            StoppedStatus => WorkflowRunTerminalStatus.Stopped,
            _ => WorkflowRunTerminalStatus.Unspecified,
        };

    private static bool HasCompletionNotificationTarget(WorkflowCompletionNotificationTarget? target) =>
        target != null &&
        !string.IsNullOrWhiteSpace(target.ActorId) &&
        !string.IsNullOrWhiteSpace(target.DeliveryId);

    private async Task SendWorkflowRunStartedNotificationAsync(CancellationToken ct)
    {
        var target = State.CompletionNotificationTarget;
        if (!HasCompletionNotificationTarget(target) ||
            State.StartedAtUtc == null ||
            string.IsNullOrWhiteSpace(State.LastCommandId) ||
            target!.ExpiresAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            return;
        }

        var notification = new WorkflowRunStartedNotification
        {
            DeliveryId = target.DeliveryId.Trim(),
            WorkflowActorId = Id,
            WorkflowRunId = RunId,
            WorkflowCommandId = State.LastCommandId.Trim(),
            WorkflowCorrelationId = State.WorkflowCorrelationId?.Trim() ?? string.Empty,
            StartedAt = State.StartedAtUtc.Clone(),
        };
        await SendToAsync(
            target.ActorId.Trim(),
            notification,
            ct,
            BuildStartedNotificationDispatchOptions(notification));
    }

    private static TimeSpan ResolveTerminalNotificationRetryDelay(int attempt, long remainingMs)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 16);
        var exponentialDelayMs = Math.Min(
            TerminalNotificationMaxRetryDelayMs,
            TerminalNotificationInitialRetryDelayMs * (1L << exponent));
        var delayMs = Math.Max(1L, Math.Min(exponentialDelayMs, remainingMs));
        return TimeSpan.FromMilliseconds(delayMs);
    }

    private static EventEnvelopePublishOptions BuildTerminalNotificationDispatchOptions(
        WorkflowRunTerminalNotification notification) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId(
                    TerminalNotificationDispatchOperationPrefix,
                    notification.DeliveryId,
                    notification.WorkflowCommandId),
            },
        };

    private static EventEnvelopePublishOptions BuildStartedNotificationDispatchOptions(
        WorkflowRunStartedNotification notification) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId(
                    StartedNotificationDispatchOperationPrefix,
                    notification.DeliveryId,
                    notification.WorkflowCommandId),
            },
        };

    private static EventEnvelopePublishOptions BuildTerminalNotificationRetryOptions(string callbackId) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = callbackId,
            },
        };

    private static string BuildTerminalNotificationRetryCallbackId(
        WorkflowRunTerminalNotification notification,
        int attempt) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            TerminalNotificationRetryCallbackPrefix,
            notification.DeliveryId,
            notification.WorkflowCommandId,
            attempt.ToString(CultureInfo.InvariantCulture));

    private async Task ResumeCompensationAsync(CancellationToken ct)
    {
        if (!IsCompensating(State) ||
            string.IsNullOrWhiteSpace(State.CompensationExecutionId) ||
            !TryGetLedgerEntry(State.CompensationCursor, out var entry))
        {
            return;
        }

        await PublishAsync(new CompensationRequestEvent
        {
            RunId = State.RunId ?? string.Empty,
            FailedStepId = ResolveLastFailedStepId(State),
            CompensationStepId = entry.CompensationStepId,
            IdempotencyKey = entry.IdempotencyKey,
            CapturedOutput = entry.CapturedOutput,
            ExecutionId = State.CompensationExecutionId ?? string.Empty,
        }, TopologyAudience.Self, ct);
    }

    private WorkflowCompensationTransitionResult BuildCurrentCompensationResult(
        WorkflowCompensationTransitionStatus status)
    {
        if (!TryGetLedgerEntry(State.CompensationCursor, out var entry))
            return EmptyCompensationResult(status);

        return BuildCompensationResult(
            status,
            entry,
            ResolveLastFailedStepId(State),
            State.CompensationExecutionId ?? string.Empty);
    }

    private static WorkflowCompensationTransitionResult BuildCompensationResult(
        WorkflowCompensationTransitionStatus status,
        CompletedStepLedgerEntry entry,
        string failedStepId,
        string executionId) =>
        new(
            status,
            entry.CompensationStepId ?? string.Empty,
            failedStepId ?? string.Empty,
            entry.IdempotencyKey ?? string.Empty,
            entry.CapturedOutput ?? string.Empty,
            executionId ?? string.Empty);

    private static WorkflowCompensationTransitionResult EmptyCompensationResult(
        WorkflowCompensationTransitionStatus status) =>
        new(status, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    private static int CalculateRemainingUncompensated(int ledgerCount, int cursor)
    {
        if (ledgerCount <= 0)
            return 0;

        return Math.Clamp(cursor + 1, 0, ledgerCount);
    }

    private static WorkflowStepFailureOutcome NormalizeFailureOutcome(WorkflowStepFailureOutcome failureOutcome) =>
        failureOutcome == WorkflowStepFailureOutcome.OutcomeUncertain
            ? WorkflowStepFailureOutcome.OutcomeUncertain
            : WorkflowStepFailureOutcome.CalleeConfirmed;

    private static bool IsProvisional(CompletedStepLedgerEntry entry) =>
        entry.LedgerStatus == CompensableLedgerEntryStatus.Provisional;

    private static void RemoveMatchingProvisionalLedgerEntries(
        WorkflowRunState state,
        string stepId,
        string compensationStepId,
        string idempotencyKey)
    {
        for (var i = state.CompensableLedger.Count - 1; i >= 0; i--)
        {
            var entry = state.CompensableLedger[i];
            if (!IsProvisional(entry))
                continue;

            if (string.Equals(entry.StepId, stepId, StringComparison.Ordinal) &&
                string.Equals(entry.CompensationStepId, compensationStepId, StringComparison.Ordinal) &&
                string.Equals(entry.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
            {
                state.CompensableLedger.RemoveAt(i);
            }
        }
    }

    private bool TryGetLedgerEntry(int cursor, [NotNullWhen(true)] out CompletedStepLedgerEntry? entry)
    {
        if (cursor >= 0 && cursor < State.CompensableLedger.Count)
        {
            entry = State.CompensableLedger[cursor];
            return true;
        }

        entry = null;
        return false;
    }

    private bool MatchesCurrentCompensation(
        CompensationStepCompletedEvent completion,
        CompletedStepLedgerEntry currentEntry)
    {
        var runId = WorkflowRunIdNormalizer.Normalize(completion.RunId);
        return string.Equals(State.RunId, runId, StringComparison.Ordinal) &&
               string.Equals(currentEntry.CompensationStepId, completion.CompensationStepId?.Trim(), StringComparison.Ordinal) &&
               string.Equals(State.CompensationExecutionId ?? string.Empty, completion.ExecutionId ?? string.Empty, StringComparison.Ordinal);
    }

    private static bool IsCompensating(WorkflowRunState state) =>
        state.SagaStatus == WorkflowSagaStatus.Compensating;

    private static string ResolveStepCompletionIdempotency(
        StepCompletedEvent evt,
        WorkflowRunState state,
        string stepId) =>
        evt.Annotations.TryGetValue("idempotency_key", out var annotatedKey)
            ? annotatedKey ?? string.Empty
            : ResolveCompletedStepIdempotency(state, stepId);

    private static string ResolveCompletedStepIdempotency(WorkflowRunState state, string stepId)
    {
        foreach (var packedState in state.ExecutionStates.Values)
        {
            if (packedState?.Is(WorkflowExecutionKernelState.Descriptor) != true)
                continue;

            var kernelState = packedState.Unpack<WorkflowExecutionKernelState>();
            if (kernelState.IdempotencyByStepId.TryGetValue(stepId, out var idempotency))
                return idempotency.IdempotencyKey ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ResolveLastFailedStepId(WorkflowRunState state)
    {
        if (!string.IsNullOrWhiteSpace(state.CompensationOriginFailedStepId))
            return state.CompensationOriginFailedStepId.Trim();

        foreach (var packedState in state.ExecutionStates.Values)
        {
            if (packedState?.Is(WorkflowExecutionKernelState.Descriptor) == true)
                return packedState.Unpack<WorkflowExecutionKernelState>().CurrentStepId?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ResolveCompensationOriginFailedStepId(
        WorkflowRunState state,
        StepCompletedEvent? terminalStep)
    {
        if (terminalStep != null && !terminalStep.Success && !string.IsNullOrWhiteSpace(terminalStep.StepId))
            return terminalStep.StepId.Trim();

        return ResolveLastFailedStepId(state);
    }

    private async Task PublishManagedParentInvocationCompletionAsync(
        WorkflowCompletedEvent evt,
        WorkflowRunState stateBeforeCompletion,
        CancellationToken ct)
    {
        var runtime = stateBeforeCompletion.ExecutionContext?.WorkflowRuntime;
        if (runtime == null ||
            string.IsNullOrWhiteSpace(runtime.ParentActorId) ||
            string.IsNullOrWhiteSpace(runtime.ParentRunId) ||
            string.IsNullOrWhiteSpace(runtime.ParentStepId))
        {
            return;
        }

        var invocationId = TryResolveWorkflowCallInvocationId(stateBeforeCompletion);
        if (string.IsNullOrWhiteSpace(invocationId))
            return;

        await SendToAsync(
            runtime.ParentActorId,
            new SubWorkflowInvocationCompletedEvent
            {
                InvocationId = invocationId,
                ChildRunId = string.IsNullOrWhiteSpace(evt.RunId) ? RunId : WorkflowRunIdNormalizer.Normalize(evt.RunId),
                Success = evt.Success,
                Output = evt.Output ?? string.Empty,
                Error = evt.Error ?? string.Empty,
                Compensated = !evt.Success && State.SagaStatus == WorkflowSagaStatus.CompensatedFailed,
            },
            ct);
    }

    private static string TryResolveWorkflowCallInvocationId(WorkflowRunState state)
    {
        foreach (var packedState in state.ExecutionStates.Values)
        {
            if (packedState?.Is(WorkflowExecutionKernelState.Descriptor) != true)
                continue;

            var kernelState = packedState.Unpack<WorkflowExecutionKernelState>();
            return kernelState.Variables.TryGetValue("workflow_call.invocation_id", out var invocationId)
                ? invocationId?.Trim() ?? string.Empty
                : string.Empty;
        }

        return string.Empty;
    }

    private static string BuildStoppedMessage(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "Workflow execution stopped."
            : $"Workflow execution stopped: {reason}";

    private bool TryPrepareStop(
        string? requestedRunId,
        string eventName,
        out string runId)
    {
        runId = string.IsNullOrWhiteSpace(requestedRunId)
            ? RunId
            : WorkflowRunIdNormalizer.Normalize(requestedRunId);
        if (!string.IsNullOrWhiteSpace(State.RunId) &&
            !string.Equals(State.RunId, runId, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "Ignore {EventName} with mismatched run id. actor={ActorId} stateRun={StateRunId} eventRun={EventRunId}",
                eventName,
                Id,
                State.RunId,
                runId);
            return false;
        }

        if (!IsTerminalStatus(State.Status))
            return true;

        Logger.LogInformation(
            "Ignore {EventName} for terminal run. actor={ActorId} run={RunId} status={Status}",
            eventName,
            Id,
            runId,
            State.Status);
        return false;
    }

    private async Task CompleteStopAsync(
        string runId,
        string? workflowName,
        string? reason,
        Func<CancellationToken, Task> persistAsync,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(persistAsync);

        var stateBeforeStop = State.Clone();
        await persistAsync(ct);
        await TryRevokeScheduledCallerCredentialAsync(
            stateBeforeStop,
            "workflow-run-stopped",
            CancellationToken.None);
        await _subWorkflowOrchestrator.CancelPendingDefinitionResolutionTimeoutsAsync(stateBeforeStop, CancellationToken.None);
        await _subWorkflowOrchestrator.CleanupPendingInvocationsForRunAsync(runId, stateBeforeStop, CancellationToken.None);
        await CleanupRoleAgentTreeAsync(CancellationToken.None);
        _runtimeContext.Clear();
        DisableExecutionModules();

        Logger.LogInformation(
            "Workflow run {Name} stopped: run={RunId} reason={Reason}",
            workflowName,
            runId,
            string.IsNullOrWhiteSpace(reason) ? "(none)" : reason);

        await PublishAsync(new WorkflowLlmInvocationCompletedEvent
        {
            RunId = runId,
            Success = false,
            Content = BuildStoppedMessage(reason),
            Error = BuildStoppedMessage(reason),
        }, TopologyAudience.Parent);
        await EnsureTerminalNotificationAsync(ct);
    }

    private async Task TryRevokeScheduledCallerCredentialAsync(
        WorkflowRunState stateBeforeTerminal,
        string auditReason,
        CancellationToken ct)
    {
        var reference = stateBeforeTerminal.ExecutionContext?
            .CallerCredential?
            .DurableCallerCredential;
        if (reference == null ||
            reference.SourceKind != DurableCallerCredentialSourceKind.ScheduledDispatch ||
            !string.Equals(
                reference.Purpose,
                CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reference.Ref) ||
            string.IsNullOrWhiteSpace(reference.OwnerScopeKey) ||
            string.IsNullOrWhiteSpace(reference.SubjectId))
        {
            return;
        }

        if (_secretVault == null)
        {
            Logger.LogWarning(
                "Scheduled workflow caller credential cleanup skipped because the secret vault is unavailable. run={RunId}",
                stateBeforeTerminal.RunId);
            return;
        }

        using var cleanupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var revokeTask = RevokeScheduledCallerCredentialAsync(
            reference,
            auditReason,
            stateBeforeTerminal.RunId,
            cleanupCts.Token);
        try
        {
            await revokeTask.WaitAsync(
                ScheduledCallerCredentialCleanupTimeout,
                _timeProvider,
                ct);
        }
        catch (TimeoutException ex)
        {
            cleanupCts.Cancel();
            Logger.LogWarning(
                ex,
                "Scheduled workflow caller credential cleanup timed out after {TimeoutSeconds}s. run={RunId}",
                ScheduledCallerCredentialCleanupTimeout.TotalSeconds,
                stateBeforeTerminal.RunId);
        }
    }

    private async Task RevokeScheduledCallerCredentialAsync(
        DurableCallerCredentialRef reference,
        string auditReason,
        string runId,
        CancellationToken ct)
    {
        try
        {
            await _secretVault!.RevokeAsync(new RevokeSecretRequest(
                reference.Ref,
                CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
                reference.OwnerScopeKey,
                reference.SubjectId,
                auditReason), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The actor turn owns a bounded cleanup budget; token expiry is the durable fallback.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Scheduled workflow caller credential cleanup failed. run={RunId}",
                runId);
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
            Logger.LogWarning(ex, "EvaluateWorkflowCompilation: parse/validation failed.");
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

        try
        {
            var workflow = _parser.Parse(State.WorkflowYaml);
            var errors = ValidateWorkflowDefinition(workflow);
            _compiledWorkflow = errors.Count == 0 ? workflow : null;
            if (errors.Count > 0)
            {
                Logger.LogWarning(
                    "RebuildCompiledWorkflowCache: workflow has validation errors. errors={Errors}",
                    string.Join("; ", errors));
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "RebuildCompiledWorkflowCache: parse failed.");
            _compiledWorkflow = null;
        }
    }

    private WorkflowDefinition? ResolveWorkflowForTransition(WorkflowRunState state)
    {
        if (_compiledWorkflow != null)
            return _compiledWorkflow;

        if (string.IsNullOrWhiteSpace(state.WorkflowYaml))
            return null;

        try
        {
            return _parser.Parse(state.WorkflowYaml);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to parse workflow while rebuilding run transition state.");
            return null;
        }
    }

    private async Task<WorkflowCompilationResult> ReplaceWorkflowDefinitionBypassingBindingAsync(
        string workflowYaml,
        CancellationToken ct = default)
    {
        var childActorIdsToReset = CaptureDerivedChildActorIdsForReset();
        var stateBeforeBind = State.Clone();
        WorkflowDefinition parsed;
        try
        {
            parsed = _parser.Parse(workflowYaml);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "ReplaceWorkflowDefinitionBypassingBinding: parse failed.");
            return WorkflowCompilationResult.Invalid(ex.Message);
        }

        var validationErrors = ValidateWorkflowDefinition(parsed);
        if (validationErrors.Count > 0)
            return WorkflowCompilationResult.Invalid(string.Join("; ", validationErrors));

        var workflowName = parsed.Name ?? string.Empty;
        await PersistDomainEventAsync(new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            WorkflowName = workflowName,
            WorkflowYaml = workflowYaml,
            RunId = string.IsNullOrWhiteSpace(State.RunId) ? Id : State.RunId,
            ScopeId = State.ScopeId ?? string.Empty,
            RunOrigin = State.RunOrigin ?? string.Empty,
            ScheduleId = State.ScheduleId ?? string.Empty,
            CapabilityAdmissionPlan = State.CapabilityAdmissionPlan?.Clone(),
            ExpectedExecutionMode = State.ExpectedExecutionMode,
            InlineWorkflowYamls = { State.InlineWorkflowYamls },
        }, ct);
        await _subWorkflowOrchestrator.CancelPendingDefinitionResolutionTimeoutsAsync(stateBeforeBind, CancellationToken.None);
        RebuildCompiledWorkflowCache();
        await ResetDerivedRuntimeStateAsync(childActorIdsToReset, ct);
        InstallCognitiveModules();
        return WorkflowCompilationResult.Success(parsed);
    }

    private static string ResolveScopeId(
        string? requestedScopeId,
        string? fallbackScopeId)
    {
        // Refactor (iter56/cluster-917-workflow-llm-control-metadata): old=Headers/Metadata bag for control fields, new=typed ChatRequestEvent.Telegram
        if (!string.IsNullOrWhiteSpace(requestedScopeId))
            return requestedScopeId.Trim();

        return fallbackScopeId?.Trim() ?? string.Empty;
    }

    private IReadOnlyCollection<string> CaptureDerivedChildActorIdsForReset()
    {
        var childActorIds = new HashSet<string>(_childAgentIds, StringComparer.Ordinal);

        foreach (var roleActorId in CaptureRoleActorIdsFromCurrentDefinition())
        {
            if (!string.IsNullOrWhiteSpace(roleActorId))
                childActorIds.Add(roleActorId);
        }

        foreach (var binding in State.SubWorkflowBindings)
        {
            var childActorId = binding.ChildActorId?.Trim();
            if (!string.IsNullOrWhiteSpace(childActorId))
                childActorIds.Add(childActorId);
        }

        foreach (var pending in State.PendingSubWorkflowInvocations)
        {
            var childActorId = pending.ChildActorId?.Trim();
            if (!string.IsNullOrWhiteSpace(childActorId))
                childActorIds.Add(childActorId);
        }

        return childActorIds;
    }

    private IReadOnlyCollection<string> CaptureRoleActorIdsFromCurrentDefinition()
    {
        var roleActorIds = new HashSet<string>(StringComparer.Ordinal);
        var currentWorkflow = _compiledWorkflow;
        if (currentWorkflow == null && !string.IsNullOrWhiteSpace(State.WorkflowYaml))
        {
            try
            {
                currentWorkflow = _parser.Parse(State.WorkflowYaml);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to parse current workflow while capturing role actor ids for reset.");
            }
        }

        if (currentWorkflow == null)
            return roleActorIds;

        foreach (var role in WorkflowImplicitLlmRolePolicy.GetEffectiveRoles(currentWorkflow))
        {
            if (string.IsNullOrWhiteSpace(role.Id))
                continue;

            roleActorIds.Add(BuildChildActorId(role.Id));
        }

        return roleActorIds;
    }

    private async Task ResetDerivedRuntimeStateAsync(
        IReadOnlyCollection<string> childActorIds,
        CancellationToken ct)
    {
        _runtimeContext.Clear();
        foreach (var childActorId in childActorIds)
        {
            await _runtime.UnlinkAsync(childActorId, ct);
            await _runtime.DestroyAsync(childActorId, ct);
        }

        _childAgentIds.Clear();
    }

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

    private List<string> ValidateWorkflowDefinition(WorkflowDefinition workflow) =>
        WorkflowRunDefinitionValidationSupport.Validate(workflow, _knownModuleStepTypes, _stepExecutorFactory);
}
