using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
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
[GAgent(
    "workflow.run",
    StateSchemaVersion = 2)]
public sealed partial class WorkflowRunGAgent
    : GAgentBase<WorkflowRunState>,
      IWorkflowExecutionStateHost,
      IRuntimeSecretStoreAccessor,
      ISecretVaultAccessor
{
    private const string RunningStatus = "running";
    private const string BoundStatus = "bound";
    private const string CompletedStatus = "completed";
    private const string FailedStatus = "failed";
    private const string StoppedStatus = "stopped";
    private static readonly TimeSpan ScheduledCallerCredentialCleanupTimeout = TimeSpan.FromSeconds(5);
    private const string StartedNotificationDispatchOperationPrefix = "workflow-started-notification";
    private const string ToolApprovalNotificationDispatchOperationPrefix = "workflow-tool-approval-notification";
    private const string TerminalNotificationDispatchOperationPrefix = "workflow-terminal-notification";
    private const string TerminalNotificationRetryCallbackPrefix = "workflow-terminal-notification-retry";
    private const string TerminalToolCallCleanupRetryCallbackPrefix = "workflow-tool-terminal-cleanup-retry";
    private const int TerminalNotificationInitialRetryDelayMs = 250;
    private const int TerminalNotificationMaxRetryDelayMs = 30_000;
    private static readonly TimeSpan TerminalToolCallCleanupRetryDelay = TimeSpan.FromSeconds(1);
    private const string WorkflowNotExecutableError = "Workflow run is not definition-bound or compiled.";
    private const string InputFileBindingError = "workflow_input_file_binding_failed";
    internal const string OrphanedExecutionTerminalError =
        "workflow execution became inactive before a terminal completion was committed";
    internal const string StrandedEmitTerminalError =
        "workflow emit step completion was not observed after dispatch; external outcome is uncertain";
    private const int InteractiveActionHandoffLimit = 32;
    private const string NyxIdChatAgentKind = "nyxid.chat";
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
    private string? _inFlightChatCommandId;

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
            (lease, token) => CancelDurableCallbackAsync(lease, token),
            SelectSubWorkflowValueRepresentationAsync);
    }

    public string RunId => string.IsNullOrWhiteSpace(State.RunId)
        ? Id
        : State.RunId;

    public string ScopeId => State.ScopeId ?? string.Empty;

    public string ScheduleId => State.ScheduleId ?? string.Empty;

    string IWorkflowExecutionStateHost.DefinitionActorId => State.DefinitionActorId ?? string.Empty;

    string IWorkflowExecutionStateHost.WorkflowId => State.WorkflowId ?? string.Empty;

    string IWorkflowExecutionStateHost.RevisionId => State.RevisionId ?? string.Empty;

    string IWorkflowExecutionStateHost.RunOrigin => State.RunOrigin ?? string.Empty;

    string IWorkflowExecutionStateHost.ToolCatalogPolicyVersion =>
        State.ToolCatalogPolicyVersion ?? string.Empty;

    long IWorkflowExecutionStateHost.DefinitionVersion => Math.Max(0, State.DefinitionVersion);

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

    IRuntimeActorStateSchemaContextReader? IWorkflowExecutionStateHost.RuntimeStateSchemaContextReader =>
        Services.GetService<IRuntimeActorStateSchemaContextReader>();

    IRuntimeFleetCapabilityAdmissionReader? IWorkflowExecutionStateHost.RuntimeFleetCapabilityAdmissionReader =>
        Services.GetService<IRuntimeFleetCapabilityAdmissionReader>();

    IRuntimeLocalMembershipIdentityReader? IWorkflowExecutionStateHost.RuntimeLocalMembershipIdentityReader =>
        Services.GetService<IRuntimeLocalMembershipIdentityReader>();

    TimeProvider? IWorkflowExecutionStateHost.RuntimeFleetAdmissionTimeProvider =>
        Services.GetService<TimeProvider>() ?? _timeProvider;

    RuntimeActorStateMigrationAdmissionOptions? IWorkflowExecutionStateHost.RuntimeFleetAdmissionOptions =>
        Services.GetService<RuntimeActorStateMigrationAdmissionOptions>();

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
        IReadOnlyList<WorkflowToolCallAttemptPersistenceFact> toolCallAttemptPersistenceFacts = [];
        if (string.Equals(scopeKey, ToolCallModule.ModuleStateKey, StringComparison.Ordinal) &&
            state.Is(ToolCallModuleState.Descriptor))
        {
            var toolCallState = state.Unpack<ToolCallModuleState>();
            ToolCallModule.ScrubLegacyPayloadFields(toolCallState);
            var authoritative = State.ExecutionStates.TryGetValue(scopeKey, out var authoritativeState) &&
                                authoritativeState.Is(ToolCallModuleState.Descriptor)
                ? authoritativeState.Unpack<ToolCallModuleState>()
                : null;
            toolCallAttemptPersistenceFacts = WorkflowToolCallAttemptPersistence.BuildNewFacts(
                authoritative,
                toolCallState,
                ScopeId,
                _timeProvider.GetUtcNow());
            state = Any.Pack(toolCallState);
        }
        var upserted = new WorkflowExecutionStateUpsertedEvent
        {
            ScopeKey = scopeKey,
            State = state,
        };
        upserted.ToolCallAttemptPersistenceFacts.Add(toolCallAttemptPersistenceFacts);
        return PersistDomainEventAsync(upserted, ct);
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

    Task IWorkflowExecutionStateHost.RecordCompensableStepOutcomeAsync(
        CompensableStepOutputCapturedEvent evt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return PersistDomainEventAsync(evt, ct);
    }

    bool IWorkflowExecutionStateHost.IsValuePinnedForCompensation(string valueId)
    {
        var key = valueId?.Trim() ?? string.Empty;
        return key.Length > 0 && State.CompensableLedger.Any(entry =>
            string.Equals(entry.CapturedOutputValueId, key, StringComparison.Ordinal));
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
            CapturedOutputValueId = entry.CapturedOutputValueId,
            CapturedOutputProducerStepId = entry.CapturedOutputProducerStepId,
            CapturedOutputProducerExecutionId = entry.CapturedOutputProducerExecutionId,
            CapturedOutputSourceKind = entry.CapturedOutputSourceKind,
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

        if (MatchesPendingCompensationCompletion(State, completion))
            return await ContinueCommittedCompensationCompletionAsync(completion, ct);

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

        await PersistDomainEventAsync(new CompensationStepCompletedEvent
        {
            RunId = WorkflowRunIdNormalizer.Normalize(completion.RunId),
            CompensationStepId = currentEntry.CompensationStepId,
            Success = completion.Success,
            Error = completion.Error ?? string.Empty,
            ExecutionId = completion.ExecutionId ?? string.Empty,
        }, ct);

        return await ContinueCommittedCompensationCompletionAsync(completion, ct);
    }

    async Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.RecoverCompensationStepCompletionAsync(
        CompensationStepCompletedEvent completion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completion);

        if (MatchesPendingCompensationCompletion(State, completion))
            return await ContinueCommittedCompensationCompletionAsync(completion, ct);

        if (State.SagaStatus == WorkflowSagaStatus.CompensatedFailed)
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.CompletedAll);
        if (State.SagaStatus == WorkflowSagaStatus.CompensationDeadLetter)
        {
            return EmptyCompensationResult(
                WorkflowCompensationTransitionStatus.CompensationDeadLettered);
        }

        if (!IsCompensating(State))
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.NoCompensableLedger);

        if (TryGetLedgerEntry(State.CompensationCursor, out var currentEntry) &&
            MatchesCurrentCompensation(completion, currentEntry))
        {
            return await ((IWorkflowExecutionStateHost)this)
                .RecordCompensationStepCompletionAsync(completion, ct);
        }

        if (!string.IsNullOrWhiteSpace(State.CompensationExecutionId))
        {
            return BuildCurrentCompensationResult(
                WorkflowCompensationTransitionStatus.AdvancedAndRequestedNext);
        }

        return EmptyCompensationResult(WorkflowCompensationTransitionStatus.NoCompensableLedger);
    }

    private async Task<WorkflowCompensationTransitionResult> ContinueCommittedCompensationCompletionAsync(
        CompensationStepCompletedEvent completion,
        CancellationToken ct)
    {
        if (!completion.Success)
        {
            if (State.SagaStatus == WorkflowSagaStatus.CompensationDeadLetter)
            {
                return EmptyCompensationResult(
                    WorkflowCompensationTransitionStatus.CompensationDeadLettered);
            }

            if (!IsCompensating(State))
                return EmptyCompensationResult(WorkflowCompensationTransitionStatus.NoCompensableLedger);

            var cursor = State.CompensationCursor;
            if (!TryGetLedgerEntry(cursor, out var currentEntry))
                return EmptyCompensationResult(WorkflowCompensationTransitionStatus.NoCompensableLedger);
            var remainingUncompensated = CalculateRemainingUncompensated(
                State.CompensableLedger.Count,
                cursor);
            await PersistDomainEventAsync(new WorkflowCompensationFailedEvent
            {
                RunId = State.RunId ?? string.Empty,
                FailedCompensationStepId = currentEntry.CompensationStepId ?? string.Empty,
                RemainingUncompensated = remainingUncompensated,
                Error = completion.Error ?? string.Empty,
            }, ct);
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.CompensationDeadLettered);
        }

        if (State.SagaStatus == WorkflowSagaStatus.CompensatedFailed)
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.CompletedAll);

        if (!IsCompensating(State))
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.NoCompensableLedger);

        var nextCursor = State.CompensationCursor;
        if (nextCursor < 0)
        {
            await PersistDomainEventAsync(new WorkflowCompensationCompletedEvent
            {
                RunId = State.RunId ?? string.Empty,
                CompensatedSteps = State.CompensableLedger.Count,
            }, ct);
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.CompletedAll);
        }

        if (!TryGetLedgerEntry(nextCursor, out var nextEntry))
            return EmptyCompensationResult(WorkflowCompensationTransitionStatus.NoCompensableLedger);
        var nextExecutionId = Guid.NewGuid().ToString("N");
        var originFailedStepId = ResolveLastFailedStepId(State);
        await PersistDomainEventAsync(new CompensationRequestEvent
        {
            RunId = State.RunId ?? string.Empty,
            FailedStepId = originFailedStepId,
            CompensationStepId = nextEntry.CompensationStepId,
            IdempotencyKey = nextEntry.IdempotencyKey,
            CapturedOutput = nextEntry.CapturedOutput,
            CapturedOutputValueId = nextEntry.CapturedOutputValueId,
            CapturedOutputProducerStepId = nextEntry.CapturedOutputProducerStepId,
            CapturedOutputProducerExecutionId = nextEntry.CapturedOutputProducerExecutionId,
            CapturedOutputSourceKind = nextEntry.CapturedOutputSourceKind,
            ExecutionId = nextExecutionId,
        }, ct);

        return BuildCompensationResult(
            WorkflowCompensationTransitionStatus.AdvancedAndRequestedNext,
            nextEntry,
            originFailedStepId,
            nextExecutionId);
    }

    private static bool MatchesPendingCompensationCompletion(
        WorkflowRunState state,
        CompensationStepCompletedEvent completion)
    {
        var pending = state.PendingCompensationCompletion;
        return pending != null &&
               string.Equals(
                   WorkflowRunIdNormalizer.Normalize(pending.RunId),
                   WorkflowRunIdNormalizer.Normalize(completion.RunId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   pending.CompensationStepId?.Trim(),
                   completion.CompensationStepId?.Trim(),
                   StringComparison.Ordinal) &&
               string.Equals(
                   pending.ExecutionId?.Trim(),
                   completion.ExecutionId?.Trim(),
                   StringComparison.Ordinal) &&
               pending.Success == completion.Success &&
               string.Equals(
                   pending.Error ?? string.Empty,
                   completion.Error ?? string.Empty,
                   StringComparison.Ordinal);
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
        if (await RecoverPendingWorkflowCompletionAsync(ct))
            return;
        var definitionStartDispatched = await DrainPendingDefinitionBindingContinuationAsync(ct);
        InstallCognitiveModules();
        if (!definitionStartDispatched)
            await RecoverPendingWorkflowStartAsync(ct);
        await RecoverPendingWorkflowExecutionDispatchAsync(ct);
        await RecoverTerminalNotificationAsync(ct);
        await RecoverToolCallActorLocalStateAsync(ct);
        await RecoverForEachDurablePublicationsAsync(ct);

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

        await DispatchPendingInteractiveActionContinuationsAsync(ct);
        await ResumeCompensationAsync(ct);
    }

    private async Task RecoverPendingWorkflowStartAsync(CancellationToken ct)
    {
        var pending = State.PendingStartWorkflow?.Clone();
        if (pending == null ||
            IsTerminalStatus(State.Status) ||
            !string.Equals(State.Status, RunningStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Logger.LogWarning(
            "Recovering committed workflow start dispatch. actor={ActorId} run={RunId} workflow={WorkflowName} generation={BindingGeneration}",
            Id,
            pending.RunId,
            pending.WorkflowName,
            pending.BindingGeneration);
        await PublishStartWorkflowOrTerminalFailureAsync(pending, sessionId: string.Empty, ct);
    }

    private async Task RecoverPendingWorkflowExecutionDispatchAsync(CancellationToken ct)
    {
        if (!State.ExecutionStates.TryGetValue(WorkflowExecutionKernel.ModuleStateKey, out var packed) ||
            packed?.Is(WorkflowExecutionKernelState.Descriptor) != true)
        {
            return;
        }

        var kernelState = packed.Unpack<WorkflowExecutionKernelState>();
        var hasPendingCompensationOutcome =
            kernelState.PendingCompensationOutcome?.Completion != null;
        var hasPendingDispatch = kernelState.Active &&
                                 kernelState.CurrentStepDispatchPending &&
                                 !string.IsNullOrWhiteSpace(kernelState.RunId);
        if (!hasPendingCompensationOutcome && !hasPendingDispatch)
        {
            return;
        }

        await PublishAsync(
            new WorkflowExecutionRecoveryRequestedEvent
            {
                RunId = hasPendingCompensationOutcome
                    ? kernelState.PendingCompensationOutcome!.Completion.RunId
                    : kernelState.RunId,
            },
            TopologyAudience.Self,
            ct);
    }

    protected override Task OnDeactivateAsync(CancellationToken ct)
    {
        CancelWorkflowExecutionBackgroundWork();
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    protected override async Task OnCommittedStatePublicationRecoveredAsync(
        EventEnvelope envelope,
        CancellationToken ct)
    {
        await base.OnCommittedStatePublicationRecoveredAsync(envelope, ct);
        if (await RecoverPendingWorkflowCompletionAsync(ct))
            return;
        var definitionStartDispatched = await DrainPendingDefinitionBindingContinuationAsync(ct);
        InstallCognitiveModules();
        if (!definitionStartDispatched)
            await RecoverPendingWorkflowStartAsync(ct);
        await RecoverTerminalNotificationAsync(ct);
        await RecoverToolCallActorLocalStateAsync(ct);
        await DispatchPendingInteractiveActionContinuationsAsync(ct);
        await RecoverForEachDurablePublicationsAsync(ct);
    }

    private async Task<bool> RecoverPendingWorkflowCompletionAsync(CancellationToken ct)
    {
        if (ShouldIgnoreWorkflowCompleted(State))
            return false;

        var pending = GetPendingWorkflowCompletion(State);
        if (pending == null)
            return false;

        if (IsMismatchedRunIdentity(State, pending.RunId))
        {
            Logger.LogWarning(
                "Ignore persisted workflow completion with mismatched run id. actor={ActorId} stateRun={StateRunId} pendingRun={PendingRunId}",
                Id,
                State.RunId,
                pending.RunId);
            return false;
        }

        try
        {
            await PublishAsync(
                pending.Clone(),
                TopologyAudience.Self,
                ct,
                WorkflowExecutionKernel.BuildWorkflowCompletionPublishOptions(pending));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IRuntimeEnvelopeRetryableException ||
            WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WorkflowDurablePublicationPendingException(
                $"Persisted workflow completion recovery remains pending for run '{pending.RunId}'.",
                ex);
        }
    }

    private static WorkflowCompletedEvent? GetPendingWorkflowCompletion(WorkflowRunState state)
    {
        var packed = state.ExecutionStates.GetValueOrDefault(WorkflowExecutionKernel.ModuleStateKey);
        return packed?.Is(WorkflowExecutionKernelState.Descriptor) == true
            ? packed.Unpack<WorkflowExecutionKernelState>().PendingWorkflowCompletion
            : null;
    }

    [EventHandler]
    public async Task HandleReconcileWorkflowTerminalState(
        ReconcileWorkflowTerminalStateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var requestedRunId = WorkflowRunIdNormalizer.Normalize(command.RunId);
        var currentRunId = WorkflowRunIdNormalizer.Normalize(State.RunId);
        if (string.IsNullOrWhiteSpace(requestedRunId) ||
            !string.Equals(requestedRunId, currentRunId, StringComparison.Ordinal))
        {
            return;
        }

        Logger.LogInformation(
            "Workflow terminal reconciliation reached authoritative run state. actor={ActorId} run={RunId} authoritativeStatus={AuthoritativeStatus} observedStateVersion={ObservedStateVersion}",
            Id,
            currentRunId,
            State.Status,
            command.ObservedStateVersion);

        if (IsTerminalStatus(State.Status))
        {
            await RepublishAuthoritativeTerminalStateAsync(
                currentRunId,
                command.ObservedStateVersion);
            return;
        }

        if (!string.Equals(State.Status, RunningStatus, StringComparison.OrdinalIgnoreCase))
            return;

        var packed = State.ExecutionStates.GetValueOrDefault(WorkflowExecutionKernel.ModuleStateKey);
        if (packed == null || !packed.Is(WorkflowExecutionKernelState.Descriptor))
        {
            if (State.PendingStartWorkflow != null ||
                State.PendingDefinitionBindingContinuation != null)
            {
                return;
            }

            await ReconcileOrphanedExecutionAsFailedAsync(
                new WorkflowExecutionKernelState(),
                currentRunId,
                command.ObservedStateVersion);
            return;
        }

        var kernelState = packed.Unpack<WorkflowExecutionKernelState>();
        if (kernelState.PendingWorkflowCompletion != null)
        {
            await RecoverPendingWorkflowCompletionAsync(CancellationToken.None);
            return;
        }

        if (IsCompensating(State))
            return;

        if (kernelState.Active || !string.IsNullOrWhiteSpace(kernelState.RunId))
        {
            if (await TryRecoverPendingLlmExecutionAsync(
                    kernelState,
                    currentRunId,
                    command.ObservedStateVersion))
            {
                return;
            }

            if (await TryRecoverFailedForEachParentAsync(
                    kernelState,
                    currentRunId,
                    command.ObservedStateVersion))
            {
                return;
            }

            if (await TryFailStrandedEmitExecutionAsync(
                    kernelState,
                    currentRunId,
                    command.ObservedStateVersion))
            {
                return;
            }

            LogPreservedActiveTerminalReconciliation(
                kernelState,
                currentRunId,
                command.ObservedStateVersion);
            return;
        }

        await ReconcileOrphanedExecutionAsFailedAsync(
            kernelState,
            currentRunId,
            command.ObservedStateVersion);
    }

    private async Task RepublishAuthoritativeTerminalStateAsync(
        string runId,
        long observedStateVersion)
    {
        IMessage routingEvent;
        if (string.Equals(State.Status, StoppedStatus, StringComparison.OrdinalIgnoreCase))
        {
            var stopped = new WorkflowRunStoppedEvent
            {
                RunId = runId,
                Reason = State.FinalError ?? string.Empty,
            };
            if (State.CompletedAtUtc != null)
                stopped.CompletedAtUtc = State.CompletedAtUtc.Clone();
            routingEvent = stopped;
        }
        else
        {
            var completed = new WorkflowCompletedEvent
            {
                WorkflowName = State.WorkflowName ?? string.Empty,
                RunId = runId,
                Success = string.Equals(State.Status, CompletedStatus, StringComparison.OrdinalIgnoreCase),
                Output = State.FinalOutput ?? string.Empty,
                Error = State.FinalError ?? string.Empty,
                RecoveryFailureKind = State.TerminalRecoveryFailureKind,
                ValueLifecycleFailureKind = State.TerminalValueLifecycleFailureKind,
            };
            if (State.CompletedAtUtc != null)
                completed.CompletedAtUtc = State.CompletedAtUtc.Clone();
            routingEvent = completed;
        }

        Logger.LogWarning(
            "Republish authoritative terminal workflow state for stale running read model. actor={ActorId} run={RunId} authoritativeStatus={AuthoritativeStatus} observedStateVersion={ObservedStateVersion}",
            Id,
            runId,
            State.Status,
            observedStateVersion);
        await RepublishCommittedStateAsync(routingEvent, CancellationToken.None);
    }

    private async Task ReconcileOrphanedExecutionAsFailedAsync(
        WorkflowExecutionKernelState kernelState,
        string runId,
        long observedStateVersion)
    {
        var completion = new WorkflowCompletedEvent
        {
            WorkflowName = State.WorkflowName ?? string.Empty,
            RunId = runId,
            Success = false,
            Error = OrphanedExecutionTerminalError,
        };
        kernelState.PendingWorkflowCompletion = completion.Clone();

        Logger.LogWarning(
            "Reconcile orphaned workflow execution as failed. actor={ActorId} run={RunId} observedStateVersion={ObservedStateVersion}",
            Id,
            runId,
            observedStateVersion);
        await UpsertExecutionStateAsync(
            WorkflowExecutionKernel.ModuleStateKey,
            Any.Pack(kernelState),
            CancellationToken.None);
        await RecoverPendingWorkflowCompletionAsync(CancellationToken.None);
    }

    private async Task<bool> TryRecoverPendingLlmExecutionAsync(
        WorkflowExecutionKernelState kernelState,
        string runId,
        long observedStateVersion)
    {
        if (!kernelState.Active ||
            !string.Equals(
                WorkflowRunIdNormalizer.Normalize(kernelState.RunId),
                runId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(kernelState.CurrentStepId))
        {
            return false;
        }

        if (kernelState.CurrentStepDispatchPending)
        {
            Logger.LogWarning(
                "Resume committed workflow step dispatch during terminal reconciliation. actor={ActorId} run={RunId} step={StepId} observedStateVersion={ObservedStateVersion}",
                Id,
                runId,
                kernelState.CurrentStepId,
                observedStateVersion);
            await PublishWorkflowExecutionRecoveryRequestAsync(runId);
            return true;
        }

        if (!kernelState.ExecutionIdsByStepId.TryGetValue(
                kernelState.CurrentStepId,
                out var currentExecutionId) ||
            string.IsNullOrWhiteSpace(currentExecutionId))
        {
            return false;
        }

        var packed = State.ExecutionStates.GetValueOrDefault(LLMCallModule.ModuleStateKey);
        if (packed?.Is(LLMCallModuleState.Descriptor) != true)
            return false;

        var llmState = packed.Unpack<LLMCallModuleState>();
        var matches = llmState.PendingBySessionId
            .Where(entry =>
                string.Equals(
                    WorkflowRunIdNormalizer.Normalize(entry.Value.RunId),
                    runId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    entry.Value.StepId,
                    kernelState.CurrentStepId,
                    StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(entry.Value.ExecutionId) ||
                 string.Equals(
                     entry.Value.ExecutionId,
                     currentExecutionId,
                     StringComparison.Ordinal)))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
            return false;

        var (sessionId, pending) = matches[0];
        var targetRole = ResolvePendingLlmTargetRole(kernelState.CurrentStepId, pending);

        if (!pending.RequestDispatched)
        {
            kernelState.CurrentStepDispatchPending = true;
            Logger.LogWarning(
                "Rearm unconfirmed workflow LLM dispatch during terminal reconciliation. actor={ActorId} run={RunId} step={StepId} session={SessionId} observedStateVersion={ObservedStateVersion}",
                Id,
                runId,
                kernelState.CurrentStepId,
                sessionId,
                observedStateVersion);
            await UpsertExecutionStateAsync(
                WorkflowExecutionKernel.ModuleStateKey,
                Any.Pack(kernelState),
                CancellationToken.None);

            if (!string.IsNullOrWhiteSpace(targetRole))
            {
                await RequestPendingLlmCompletionRedeliveryAsync(
                    runId,
                    kernelState.CurrentStepId,
                    currentExecutionId,
                    sessionId,
                    targetRole,
                    observedStateVersion);
            }

            await PublishWorkflowExecutionRecoveryRequestAsync(runId);
            return true;
        }

        if (string.IsNullOrWhiteSpace(targetRole))
            return false;

        await RequestPendingLlmCompletionRedeliveryAsync(
            runId,
            kernelState.CurrentStepId,
            currentExecutionId,
            sessionId,
            targetRole,
            observedStateVersion);
        return true;
    }

    private string ResolvePendingLlmTargetRole(
        string currentStepId,
        PendingLlmCallState pending)
    {
        if (!string.IsNullOrWhiteSpace(pending.TargetRole))
            return pending.TargetRole.Trim();

        var workflow = ResolveWorkflowForTransition(State);
        var currentStep = workflow?.GetStep(currentStepId);
        return currentStep == null
            ? string.Empty
            : WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(workflow, currentStep);
    }

    private async Task RequestPendingLlmCompletionRedeliveryAsync(
        string runId,
        string currentStepId,
        string currentExecutionId,
        string sessionId,
        string targetRole,
        long observedStateVersion)
    {
        var targetActorId = WorkflowRoleActorIdResolver.ResolveTargetActorId(
            Id,
            targetRole);
        var reconcile = new ReconcileWorkflowLlmCompletionCommand
        {
            RunId = runId,
            StepId = currentStepId,
            SessionId = sessionId,
            ExecutionId = currentExecutionId,
            ObservedParentStateVersion = observedStateVersion,
        };

        Logger.LogWarning(
            "Request committed workflow LLM completion redelivery. actor={ActorId} run={RunId} step={StepId} session={SessionId} target={TargetActorId} observedStateVersion={ObservedStateVersion}",
            Id,
            runId,
            currentStepId,
            sessionId,
            targetActorId,
            observedStateVersion);
        await SendToAsync(
            targetActorId,
            reconcile,
            CancellationToken.None,
            BuildDeliveryOptions(BuildStableIdentity(
                "workflow-llm-terminal-reconcile",
                runId,
                currentStepId,
                sessionId,
                currentExecutionId)));
    }

    private Task PublishWorkflowExecutionRecoveryRequestAsync(string runId) =>
        PublishAsync(
            new WorkflowExecutionRecoveryRequestedEvent
            {
                RunId = runId,
            },
            TopologyAudience.Self,
            CancellationToken.None);

    private void LogPreservedActiveTerminalReconciliation(
        WorkflowExecutionKernelState kernelState,
        string runId,
        long observedStateVersion)
    {
        var pendingLlmCount = 0;
        var packed = State.ExecutionStates.GetValueOrDefault(LLMCallModule.ModuleStateKey);
        if (packed?.Is(LLMCallModuleState.Descriptor) == true)
            pendingLlmCount = packed.Unpack<LLMCallModuleState>().PendingBySessionId.Count;

        Logger.LogInformation(
            "Workflow terminal reconciliation preserved active execution. actor={ActorId} run={RunId} step={StepId} kernelActive={KernelActive} kernelRunPresent={KernelRunPresent} dispatchPending={DispatchPending} executionIdentityPresent={ExecutionIdentityPresent} pendingLlmCount={PendingLlmCount} observedStateVersion={ObservedStateVersion}",
            Id,
            runId,
            kernelState.CurrentStepId,
            kernelState.Active,
            !string.IsNullOrWhiteSpace(kernelState.RunId),
            kernelState.CurrentStepDispatchPending,
            kernelState.ExecutionIdsByStepId.ContainsKey(kernelState.CurrentStepId),
            pendingLlmCount,
            observedStateVersion);
    }

    private async Task<bool> TryFailStrandedEmitExecutionAsync(
        WorkflowExecutionKernelState kernelState,
        string runId,
        long observedStateVersion)
    {
        if (!kernelState.Active ||
            !string.Equals(
                WorkflowRunIdNormalizer.Normalize(kernelState.RunId),
                runId,
                StringComparison.Ordinal) ||
            kernelState.CurrentStepDispatchPending ||
            string.IsNullOrWhiteSpace(kernelState.CurrentStepId) ||
            !kernelState.ExecutionIdsByStepId.TryGetValue(
                kernelState.CurrentStepId,
                out var executionId) ||
            string.IsNullOrWhiteSpace(executionId))
        {
            return false;
        }

        var workflow = ResolveWorkflowForTransition(State);
        var currentStep = workflow?.GetStep(kernelState.CurrentStepId);
        if (currentStep == null ||
            !string.Equals(
                WorkflowPrimitiveCatalog.ToCanonicalType(currentStep.Type),
                "emit",
                StringComparison.Ordinal))
        {
            return false;
        }

        var completion = new StepCompletedEvent
        {
            RunId = runId,
            StepId = currentStep.Id,
            ExecutionId = executionId,
            Success = false,
            Outcome = WorkflowStepCompletionOutcome.Failed,
            FailureOutcome = WorkflowStepFailureOutcome.OutcomeUncertain,
            RetryDisposition = WorkflowStepRetryDisposition.Forbidden,
            Error = StrandedEmitTerminalError,
            OutputProvenance = WorkflowStepOutputProvenance.Produced,
        };

        Logger.LogWarning(
            "Fail stranded workflow emit completion during terminal reconciliation. actor={ActorId} run={RunId} step={StepId} execution={ExecutionId} observedStateVersion={ObservedStateVersion}",
            Id,
            runId,
            currentStep.Id,
            executionId,
            observedStateVersion);
        await PublishAsync(
            completion,
            TopologyAudience.Self,
            CancellationToken.None,
            BuildDeliveryOptions(BuildStableIdentity(
                "workflow-emit-terminal-reconcile",
                runId,
                currentStep.Id,
                executionId)));
        return true;
    }

    private async Task<bool> TryRecoverFailedForEachParentAsync(
        WorkflowExecutionKernelState kernelState,
        string runId,
        long observedStateVersion)
    {
        if (!kernelState.Active ||
            !string.Equals(
                WorkflowRunIdNormalizer.Normalize(kernelState.RunId),
                runId,
                StringComparison.Ordinal) ||
            kernelState.CurrentStepDispatchPending ||
            string.IsNullOrWhiteSpace(kernelState.CurrentStepId) ||
            !kernelState.ExecutionIdsByStepId.TryGetValue(
                kernelState.CurrentStepId,
                out var parentExecutionId) ||
            string.IsNullOrWhiteSpace(parentExecutionId))
        {
            return false;
        }

        var workflow = ResolveWorkflowForTransition(State);
        var currentStep = workflow?.GetStep(kernelState.CurrentStepId);
        if (currentStep == null ||
            !string.Equals(
                WorkflowPrimitiveCatalog.ToCanonicalType(currentStep.Type),
                "foreach",
                StringComparison.Ordinal))
        {
            return false;
        }

        var packed = State.ExecutionStates.GetValueOrDefault(ForEachModule.ModuleStateKey);
        if (packed?.Is(ForEachModuleState.Descriptor) != true)
            return false;

        var forEachState = packed.Unpack<ForEachModuleState>();
        if (!ForEachModule.TryPrepareFailedParentCompletion(
                forEachState,
                kernelState,
                runId,
                currentStep.Id,
                parentExecutionId,
                kernelState.RetryAttemptsByStepId.GetValueOrDefault(currentStep.Id),
                out var parentKey,
                out var stateChanged,
                out var collectedCount,
                out var expectedCount))
        {
            return false;
        }

        if (stateChanged)
        {
            await UpsertExecutionStateAsync(
                ForEachModule.ModuleStateKey,
                Any.Pack(forEachState),
                CancellationToken.None);
        }

        Logger.LogWarning(
            "Recover failed foreach parent completion. actor={ActorId} run={RunId} step={StepId} parent={ParentKey} collected={CollectedCount} expected={ExpectedCount} observedStateVersion={ObservedStateVersion} stateChanged={StateChanged}",
            Id,
            runId,
            currentStep.Id,
            parentKey,
            collectedCount,
            expectedCount,
            observedStateVersion,
            stateChanged);
        await RecoverForEachDurablePublicationsAsync(CancellationToken.None);
        return true;
    }

    private async Task RecoverToolCallActorLocalStateAsync(CancellationToken ct)
    {
        if (IsTerminalStatus(State.Status) && !IsCompensating(State))
        {
            DisableExecutionModules();
            await CleanupTerminalToolCallStateAsync(
                ct,
                allowImmediateFallback: false);
            return;
        }

        await RecoverToolCallPendingApprovalWatchdogsAsync(ct);
        await RecoverToolCallPendingExecutionWatchdogsAsync(ct);
        await RecoverToolCallPendingExecutionRetriesAsync(ct);
        await RecoverToolCallPendingExecutionsAsync(ct);
        await RecoverToolCallDurablePublicationsAsync(ct);
    }

    private async Task RecoverForEachDurablePublicationsAsync(CancellationToken ct)
    {
        if (IsTerminalStatus(State.Status) && !IsCompensating(State))
            return;

        var packed = State.ExecutionStates.GetValueOrDefault(ForEachModule.ModuleStateKey);
        if (packed == null || !packed.Is(ForEachModuleState.Descriptor))
            return;

        var state = packed.Unpack<ForEachModuleState>();
        foreach (var retry in ForEachModule.BuildPendingPublicationRetries(state))
        {
            try
            {
                await ScheduleSelfDurableTimeoutAsync(
                    ForEachModule.BuildPublicationRetryCallbackId(retry),
                    ForEachModule.DurablePublicationRetryDelay,
                    retry,
                    ForEachModule.BuildPublicationRetryOptions(retry),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception scheduleException)
            {
                Logger.LogWarning(
                    scheduleException,
                    "ForEach publication recovery scheduling failed; falling back to typed self continuation. actor={ActorId} parent={ParentKey}",
                    Id,
                    retry.ParentKey);
                try
                {
                    await PublishAsync(
                        retry.Clone(),
                        TopologyAudience.Self,
                        ct,
                        ForEachModule.BuildPublicationRetryOptions(retry));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception continuationException)
                {
                    throw new WorkflowDurablePublicationPendingException(
                        "Durable foreach publication recovery remains pending during activation.",
                        continuationException);
                }
            }
        }
    }

    private async Task RecoverToolCallDurablePublicationsAsync(CancellationToken ct)
    {
        var packed = State.ExecutionStates.GetValueOrDefault(ToolCallModule.ModuleStateKey);
        if (packed == null || !packed.Is(ToolCallModuleState.Descriptor))
            return;

        var state = packed.Unpack<ToolCallModuleState>();
        foreach (var retry in ToolCallModule.BuildPendingPublicationRetries(state))
        {
            try
            {
                await ScheduleSelfDurableTimeoutAsync(
                    ToolCallModule.BuildPublicationRetryCallbackId(retry),
                    ToolCallModule.DurablePublicationRetryDelay,
                    retry,
                    ToolCallModule.BuildPublicationRetryOptions(retry),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception scheduleException)
            {
                Logger.LogWarning(
                    scheduleException,
                    "Workflow tool publication recovery scheduling failed; falling back to a typed self continuation. kind={PublicationKind}",
                    retry.PublicationKind);
                try
                {
                    await PublishAsync(
                        retry.Clone(),
                        TopologyAudience.Self,
                        ct,
                        ToolCallModule.BuildPublicationRetryOptions(retry));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception continuationException)
                {
                    throw new WorkflowDurablePublicationPendingException(
                        "Durable workflow tool publication recovery remains pending during activation.",
                        continuationException);
                }
            }
        }

        if (state.StopCancellation != null)
        {
            var pendingCancellations = ToolCallModule.PreparePendingStopCancellationRecoveries(
                state,
                _timeProvider.GetUtcNow(),
                out var cancellationStateChanged);
            if (cancellationStateChanged)
            {
                await UpsertExecutionStateAsync(
                    ToolCallModule.ModuleStateKey,
                    Any.Pack(state),
                    ct);
            }

            if (pendingCancellations.Count == 0)
            {
                await PublishPendingToolStopReleaseAsync(state, ct);
                return;
            }

            foreach (var pending in pendingCancellations)
            {
                try
                {
                    await ScheduleSelfDurableTimeoutAsync(
                        pending.StopCancellationCallbackId,
                        ToolCallModule.BuildStopCancellationDelay(
                            pending,
                            _timeProvider.GetUtcNow(),
                            state.StopCancellation.ExpiresAtUnixMs),
                        ToolCallModule.BuildStopCancellationEvent(pending),
                        ToolCallModule.BuildStopCancellationOptions(pending),
                        ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception scheduleException)
                {
                    Logger.LogWarning(
                        "Durable workflow tool stop cancellation recovery scheduling failed; falling back to a typed self continuation. exceptionType={ExceptionType}",
                        scheduleException.GetType().Name);
                    try
                    {
                        await PublishAsync(
                            ToolCallModule.BuildStopCancellationEvent(pending),
                            TopologyAudience.Self,
                            ct,
                            ToolCallModule.BuildStopCancellationOptions(pending));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception continuationException)
                    {
                        throw new WorkflowDurablePublicationPendingException(
                            "Durable workflow tool stop cancellation remains pending during activation.",
                            continuationException);
                    }
                }
            }

            return;
        }

        if (IsTerminalStatus(State.Status))
            return;

        var pendingOperations = ToolCallModule.PreparePendingOperationPollRecoveries(
            state,
            _timeProvider.GetUtcNow(),
            out var stateChanged);
        if (stateChanged)
        {
            await UpsertExecutionStateAsync(
                ToolCallModule.ModuleStateKey,
                Any.Pack(state),
                ct);
        }

        foreach (var pending in pendingOperations)
        {
            try
            {
                await ScheduleSelfDurableTimeoutAsync(
                    pending.PollCallbackId,
                    ToolCallModule.BuildOperationPollDelay(pending, _timeProvider.GetUtcNow()),
                    ToolCallModule.BuildOperationPollEvent(pending),
                    ToolCallModule.BuildOperationPollOptions(pending),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception scheduleException)
            {
                Logger.LogWarning(
                    "Durable workflow tool poll recovery scheduling failed; falling back to a typed self continuation. exceptionType={ExceptionType}",
                    scheduleException.GetType().Name);
                try
                {
                    await PublishAsync(
                        ToolCallModule.BuildOperationPollEvent(pending),
                        TopologyAudience.Self,
                        ct,
                        ToolCallModule.BuildOperationPollOptions(pending));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception continuationException)
                {
                    throw new WorkflowDurablePublicationPendingException(
                        "Durable workflow tool poll recovery remains pending during activation.",
                        continuationException);
                }
            }
        }
    }

    private async Task PublishPendingToolStopReleaseAsync(
        ToolCallModuleState state,
        CancellationToken ct)
    {
        switch (ToolCallModule.BuildPendingStopReleaseEvent(state))
        {
            case WorkflowStoppedEvent workflowStopped:
                await PublishAsync(workflowStopped, TopologyAudience.Self, ct);
                break;
            case WorkflowRunStoppedEvent workflowRunStopped:
                await PublishAsync(workflowRunStopped, TopologyAudience.Self, ct);
                break;
            default:
                var stopKind = state.StopCancellation == null
                    ? "missing"
                    : ((int)state.StopCancellation.StopKind).ToString(CultureInfo.InvariantCulture);
                throw new WorkflowDurablePublicationPendingException(
                    $"Persisted workflow tool stop release has unsupported stop kind '{stopKind}' during activation.",
                    new InvalidOperationException(
                        "Persisted workflow tool stop cancellation has no releasable stop event."));
        }
    }

    private async Task RecoverToolCallPendingExecutionWatchdogsAsync(CancellationToken ct)
    {
        var packed = State.ExecutionStates.GetValueOrDefault(ToolCallModule.ModuleStateKey);
        if (packed == null || !packed.Is(ToolCallModuleState.Descriptor))
            return;

        var state = packed.Unpack<ToolCallModuleState>();
        foreach (var recovery in ToolCallModule.BuildPendingExecutionWatchdogRecoveries(
                     state,
                     _timeProvider.GetUtcNow()))
        {
            var lease = await ScheduleSelfDurableTimeoutAsync(
                recovery.CallbackId,
                recovery.DueTime,
                recovery.Timeout,
                recovery.Options,
                ct);

            packed = State.ExecutionStates.GetValueOrDefault(ToolCallModule.ModuleStateKey);
            if (packed == null || !packed.Is(ToolCallModuleState.Descriptor))
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    CancelDurableCallbackAsync,
                    Logger,
                    lease,
                    "orphaned recovered tool execution watchdog",
                    CancellationToken.None);
                return;
            }

            state = packed.Unpack<ToolCallModuleState>();
            if (!state.PendingExecutions.TryGetValue(recovery.PendingKey, out var pending) ||
                !ToolCallModule.MatchesPendingExecution(pending, recovery))
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    CancelDurableCallbackAsync,
                    Logger,
                    lease,
                    "orphaned recovered tool execution watchdog",
                    ct);
                continue;
            }

            pending.TimeoutLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
            state.PendingExecutions[recovery.PendingKey] = pending;
            await UpsertExecutionStateAsync(
                ToolCallModule.ModuleStateKey,
                Any.Pack(state),
                ct);
        }
    }

    private async Task RecoverToolCallPendingApprovalWatchdogsAsync(CancellationToken ct)
    {
        var packed = State.ExecutionStates.GetValueOrDefault(ToolCallModule.ModuleStateKey);
        if (packed == null || !packed.Is(ToolCallModuleState.Descriptor))
            return;

        var state = packed.Unpack<ToolCallModuleState>();
        foreach (var recovery in ToolCallModule.BuildPendingApprovalWatchdogRecoveries(
                     state,
                     _timeProvider.GetUtcNow()))
        {
            var lease = await ScheduleSelfDurableTimeoutAsync(
                recovery.CallbackId,
                recovery.DueTime,
                recovery.Timeout,
                recovery.Options,
                ct);

            packed = State.ExecutionStates.GetValueOrDefault(ToolCallModule.ModuleStateKey);
            if (packed == null || !packed.Is(ToolCallModuleState.Descriptor))
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    CancelDurableCallbackAsync,
                    Logger,
                    lease,
                    "orphaned recovered tool approval watchdog",
                    CancellationToken.None);
                return;
            }

            state = packed.Unpack<ToolCallModuleState>();
            if (!state.PendingApprovals.TryGetValue(recovery.PendingKey, out var pending) ||
                !ToolCallModule.MatchesPendingApproval(pending, recovery))
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    CancelDurableCallbackAsync,
                    Logger,
                    lease,
                    "orphaned recovered tool approval watchdog",
                    CancellationToken.None);
                continue;
            }

            pending.TimeoutCallbackId = recovery.CallbackId;
            pending.TimeoutLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
            state.PendingApprovals[recovery.PendingKey] = pending;
            try
            {
                await UpsertExecutionStateAsync(
                    ToolCallModule.ModuleStateKey,
                    Any.Pack(state),
                    ct);
            }
            catch
            {
                if (!IsToolCallApprovalWatchdogCheckpointed(recovery, lease))
                {
                    await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                        CancelDurableCallbackAsync,
                        Logger,
                        lease,
                        "uncommitted recovered tool approval watchdog",
                        CancellationToken.None);
                }
                throw;
            }
        }
    }

    private bool IsToolCallApprovalWatchdogCheckpointed(
        ToolCallModule.PendingApprovalWatchdogRecovery recovery,
        RuntimeCallbackLease lease)
    {
        var packed = State.ExecutionStates.GetValueOrDefault(ToolCallModule.ModuleStateKey);
        if (packed == null || !packed.Is(ToolCallModuleState.Descriptor))
            return false;

        var state = packed.Unpack<ToolCallModuleState>();
        if (!state.PendingApprovals.TryGetValue(recovery.PendingKey, out var pending))
            return false;

        var checkpointedLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
        return ToolCallModule.MatchesPendingApproval(
            pending,
            recovery with { ExpectedTimeoutLease = checkpointedLease });
    }

    private async Task RecoverToolCallPendingExecutionRetriesAsync(CancellationToken ct)
    {
        var packed = State.ExecutionStates.GetValueOrDefault(ToolCallModule.ModuleStateKey);
        if (packed == null || !packed.Is(ToolCallModuleState.Descriptor))
            return;

        var state = packed.Unpack<ToolCallModuleState>();
        foreach (var recovery in ToolCallModule.BuildPendingExecutionRetryRecoveries(
                     state,
                     _timeProvider.GetUtcNow()))
        {
            var lease = await ScheduleSelfDurableTimeoutAsync(
                recovery.CallbackId,
                recovery.DueTime,
                recovery.Retry,
                recovery.Options,
                ct);

            packed = State.ExecutionStates.GetValueOrDefault(ToolCallModule.ModuleStateKey);
            if (packed == null || !packed.Is(ToolCallModuleState.Descriptor))
                return;

            state = packed.Unpack<ToolCallModuleState>();
            if (!state.PendingExecutions.TryGetValue(recovery.PendingKey, out var pending) ||
                !ToolCallModule.MatchesPendingExecution(pending, recovery))
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    CancelDurableCallbackAsync,
                    Logger,
                    lease,
                    "orphaned recovered tool execution retry",
                    ct);
                continue;
            }

            pending.RetryLease = WorkflowRuntimeCallbackLeaseStateCodec.ToState(lease);
            state.PendingExecutions[recovery.PendingKey] = pending;
            await UpsertExecutionStateAsync(
                ToolCallModule.ModuleStateKey,
                Any.Pack(state),
                ct);
        }
    }

    private async Task RecoverToolCallPendingExecutionsAsync(CancellationToken ct)
    {
        var packed = State.ExecutionStates.GetValueOrDefault(ToolCallModule.ModuleStateKey);
        if (packed == null || !packed.Is(ToolCallModuleState.Descriptor))
            return;

        var state = packed.Unpack<ToolCallModuleState>();
        foreach (var recovery in ToolCallModule.BuildPendingExecutionRecoveries(state))
        {
            await ScheduleSelfDurableTimeoutAsync(
                recovery.CallbackId,
                TimeSpan.FromMilliseconds(1),
                recovery.Recovery,
                recovery.Options,
                ct);
        }
    }

    private async Task CleanupTerminalToolCallStateAsync(
        CancellationToken ct,
        bool allowImmediateFallback = true)
    {
        CancelWorkflowExecutionBackgroundWork();
        if (!State.ExecutionStates.TryGetValue(ToolCallModule.ModuleStateKey, out var packed) ||
            !packed.Is(ToolCallModuleState.Descriptor))
        {
            return;
        }

        var toolState = packed.Unpack<ToolCallModuleState>();
        await CancelToolCallPendingCallbacksAsync(toolState, ct);
        var materialCleanup = await RevokeToolCallProtectedMaterialsAsync(toolState, ct);
        if (materialCleanup.AllReferencesRevoked)
        {
            await ClearExecutionStateAsync(ToolCallModule.ModuleStateKey, ct);
            return;
        }

        if (materialCleanup.StateChanged)
        {
            await UpsertExecutionStateAsync(
                ToolCallModule.ModuleStateKey,
                Any.Pack(toolState),
                ct);
        }

        await ScheduleTerminalToolCallCleanupRetryAsync(allowImmediateFallback, ct);
    }

    private async Task CancelToolCallPendingCallbacksAsync(
        ToolCallModuleState toolState,
        CancellationToken ct)
    {
        foreach (var pending in toolState.PendingApprovals.Values)
        {
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                CancelDurableCallbackAsync,
                Logger,
                WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(pending.TimeoutLease),
                "terminal tool approval watchdog",
                ct);
        }

        foreach (var pending in toolState.PendingExecutions.Values)
        {
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                CancelDurableCallbackAsync,
                Logger,
                WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(pending.TimeoutLease),
                "terminal tool execution watchdog",
                ct);
            await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                CancelDurableCallbackAsync,
                Logger,
                WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(pending.RetryLease),
                "terminal tool execution retry",
                ct);
        }
    }

    private async Task<ToolCallProtectedMaterialCleanupResult> RevokeToolCallProtectedMaterialsAsync(
        ToolCallModuleState toolState,
        CancellationToken ct)
    {
        var runtimeSecretStore = (IRuntimeSecretStore?)Services.GetService(typeof(IRuntimeSecretStore));
        var references = toolState.PendingApprovals.Values
            .Select(static pending => pending.ProtectedMaterialReference)
            .Concat(toolState.PendingExecutions.Values.Select(static pending => pending.ProtectedMaterialReference))
            .Concat(toolState.PendingOperations.Values.Select(static pending => pending.ProtectedMaterialReference))
            .Concat(toolState.Completions.Select(static completion => completion.ProtectedMaterialReference))
            .Where(static reference => reference != null && !string.IsNullOrWhiteSpace(reference.Ref))
            .GroupBy(static reference => reference!.Ref, StringComparer.Ordinal)
            .Select(static group => group.First()!)
            .ToArray();
        var revokedReferences = new HashSet<string>(StringComparer.Ordinal);
        var allReferencesRevoked = true;
        foreach (var reference in references)
        {
            var revoked = await ToolCallModule.RevokeOrConfirmProtectedMaterialUnavailableAsync(
                reference,
                runtimeSecretStore,
                ct);
            if (revoked)
            {
                revokedReferences.Add(reference.Ref);
                continue;
            }

            allReferencesRevoked = false;
            Logger.LogWarning(
                "Workflow tool protected material revocation was not confirmed. actor={ActorId} run={RunId} step={StepId}",
                Id,
                reference.OwnerRunId,
                reference.OwnerStepId);
        }

        if (revokedReferences.Count == 0)
        {
            return new ToolCallProtectedMaterialCleanupResult(
                allReferencesRevoked,
                StateChanged: false);
        }

        foreach (var pending in toolState.PendingApprovals.Values)
        {
            if (pending.ProtectedMaterialReference != null &&
                revokedReferences.Contains(pending.ProtectedMaterialReference.Ref))
            {
                pending.ProtectedMaterialReference = null;
                pending.ProtectedMaterialDigestSha256 = string.Empty;
            }
        }

        foreach (var pending in toolState.PendingExecutions.Values)
        {
            if (pending.ProtectedMaterialReference != null &&
                revokedReferences.Contains(pending.ProtectedMaterialReference.Ref))
            {
                pending.ProtectedMaterialReference = null;
                pending.ProtectedMaterialDigestSha256 = string.Empty;
            }
        }

        foreach (var pending in toolState.PendingOperations.Values)
        {
            if (pending.ProtectedMaterialReference != null &&
                revokedReferences.Contains(pending.ProtectedMaterialReference.Ref))
            {
                pending.ProtectedMaterialReference = null;
                pending.ProtectedMaterialDigestSha256 = string.Empty;
            }
        }

        foreach (var completion in toolState.Completions)
        {
            if (completion.ProtectedMaterialReference != null &&
                revokedReferences.Contains(completion.ProtectedMaterialReference.Ref))
            {
                completion.ProtectedMaterialReference = null;
            }
        }

        return new ToolCallProtectedMaterialCleanupResult(
            allReferencesRevoked,
            StateChanged: true);
    }

    private readonly record struct ToolCallProtectedMaterialCleanupResult(
        bool AllReferencesRevoked,
        bool StateChanged);

    private async Task ScheduleTerminalToolCallCleanupRetryAsync(
        bool allowImmediateFallback,
        CancellationToken ct)
    {
        var retry = new WorkflowToolCallTerminalCleanupRetryFiredEvent
        {
            RunId = RunId,
        };
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                BuildTerminalToolCallCleanupRetryCallbackId(retry.RunId),
                TerminalToolCallCleanupRetryDelay,
                retry,
                BuildTerminalToolCallCleanupRetryOptions(retry.RunId),
                ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception scheduleException)
        {
            Logger.LogWarning(
                scheduleException,
                "Terminal workflow tool cleanup retry scheduling failed; falling back to a typed self continuation. actor={ActorId} run={RunId}",
                Id,
                retry.RunId);
            if (!allowImmediateFallback)
            {
                throw new WorkflowDurablePublicationPendingException(
                    "Terminal workflow tool cleanup durable retry scheduling remains unavailable.",
                    scheduleException);
            }

            try
            {
                await PublishAsync(
                    retry.Clone(),
                    TopologyAudience.Self,
                    ct,
                    BuildTerminalToolCallCleanupRetryOptions(retry.RunId));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception continuationException)
            {
                throw new WorkflowDurablePublicationPendingException(
                    "Terminal workflow tool cleanup remains pending.",
                    continuationException);
            }
        }
    }

    private static string BuildTerminalToolCallCleanupRetryCallbackId(string runId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            TerminalToolCallCleanupRetryCallbackPrefix,
            WorkflowRunIdNormalizer.Normalize(runId));

    private static EventEnvelopePublishOptions BuildTerminalToolCallCleanupRetryOptions(string runId) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = BuildTerminalToolCallCleanupRetryCallbackId(runId),
            },
        };

    public async Task BindWorkflowRunDefinitionAsync(
        string definitionActorId,
        string workflowYaml,
        string? workflowName,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
        string? runId,
        string? scopeId,
        string? runOrigin,
        string? scheduleId,
        string? workflowId,
        string? revisionId,
        long definitionVersion,
        WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan,
        ExternalCapabilityExecutionMode expectedExecutionMode,
        WorkflowRunLineage? initialLineage = null,
        WorkflowRunActorReusePolicy reusePolicy = WorkflowRunActorReusePolicy.SingleRun,
        long bindingGeneration = 0,
        string? reuseAuthorityActorId = null,
        string? bindPublisherActorId = null,
        CancellationToken ct = default,
        string? toolCatalogPolicyVersion = null)
    {
        if (GetPendingWorkflowCompletion(State) != null)
        {
            Logger.LogInformation(
                "Defer workflow definition bind until pending terminal completion is recovered. actor={ActorId} run={RunId}",
                Id,
                RunId);
            await RecoverPendingWorkflowCompletionAsync(ct);
            return;
        }

        if (State.PendingDefinitionBindingContinuation != null)
        {
            throw new InvalidOperationException(
                "Workflow Run cannot bind while definition binding cleanup is pending.");
        }

        var requestedRunId = ResolveRequestedBindRunId(State, runId);
        var normalizedReusePolicy = reusePolicy == WorkflowRunActorReusePolicy.Unspecified
            ? WorkflowRunActorReusePolicy.SingleRun
            : reusePolicy;
        var normalizedToolCatalogPolicyVersion = NormalizeNewToolCatalogPolicyVersion(
            toolCatalogPolicyVersion);
        var bindDefinitionEvent = new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = definitionActorId ?? string.Empty,
            WorkflowName = workflowName ?? string.Empty,
            WorkflowYaml = workflowYaml ?? string.Empty,
            RunId = requestedRunId,
            ScopeId = scopeId?.Trim() ?? string.Empty,
            RunOrigin = runOrigin?.Trim() ?? string.Empty,
            ScheduleId = scheduleId?.Trim() ?? string.Empty,
            WorkflowId = workflowId?.Trim() ?? string.Empty,
            RevisionId = revisionId?.Trim() ?? string.Empty,
            DefinitionVersion = Math.Max(0, definitionVersion),
            CapabilityAdmissionPlan = capabilityAdmissionPlan?.Clone(),
            ExpectedExecutionMode = expectedExecutionMode,
            ReusePolicy = normalizedReusePolicy,
            BindingGeneration = bindingGeneration,
            ReuseAuthorityActorId = reuseAuthorityActorId?.Trim() ?? string.Empty,
            ToolCatalogPolicyVersion = normalizedToolCatalogPolicyVersion,
        };
        if (initialLineage != null)
            bindDefinitionEvent.InitialLineage = initialLineage.Clone();
        if (inlineWorkflowYamls != null)
        {
            foreach (var (key, value) in inlineWorkflowYamls)
                bindDefinitionEvent.InlineWorkflowYamls[key] = value;
        }

        var bindDecision = EvaluateRunDefinitionBind(
            State,
            bindDefinitionEvent,
            bindPublisherActorId,
            verifyPublisher: true);
        if (bindDecision.Disposition == RunDefinitionBindDisposition.Ignore)
        {
            Logger.LogInformation(
                "Ignore stale definition bind for workflow run. actor={ActorId} currentRun={RunId} requestedRun={RequestedRunId} currentGeneration={CurrentGeneration} requestedGeneration={RequestedGeneration} status={Status}",
                Id,
                RunId,
                requestedRunId,
                State.BindingGeneration,
                bindingGeneration,
                State.Status);
            return;
        }
        if (bindDecision.Disposition == RunDefinitionBindDisposition.Reject)
        {
            throw new InvalidOperationException(bindDecision.Error);
        }
        if (HasPendingToolCallState(State))
        {
            throw new InvalidOperationException(
                "Workflow Run cannot bind while actor-local tool call cleanup is pending.");
        }

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

        if (WorkflowToolCatalogPolicies.IsCurrent(bindDefinitionEvent.ToolCatalogPolicyVersion))
        {
            var compilation = EvaluateWorkflowCompilation(
                bindDefinitionEvent.WorkflowYaml,
                bindDefinitionEvent.ToolCatalogPolicyVersion);
            if (!compilation.Compiled)
                throw new InvalidOperationException(compilation.CompilationError);
            foreach (var (inlineName, inlineYaml) in bindDefinitionEvent.InlineWorkflowYamls)
            {
                var inlineCompilation = EvaluateWorkflowCompilation(
                    inlineYaml,
                    bindDefinitionEvent.ToolCatalogPolicyVersion);
                if (!inlineCompilation.Compiled)
                {
                    throw new InvalidOperationException(
                        $"Inline workflow '{inlineName}' is invalid: {inlineCompilation.CompilationError}");
                }
            }
        }

        EnsureWorkflowNameCanBind(workflowName);
        var continuation = BuildDefinitionBindingContinuation(
            executeAfterBind: false,
            executionInput: string.Empty);
        await PersistDomainEventsAsync(
            [
                bindDefinitionEvent,
                new WorkflowRunDefinitionBindingContinuationRegisteredEvent
                {
                    Continuation = continuation,
                },
            ],
            ct);
        await DrainPendingDefinitionBindingContinuationAsync(ct);
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
            request.WorkflowId,
            request.RevisionId,
            request.DefinitionVersion,
            request.CapabilityAdmissionPlan,
            request.ExpectedExecutionMode,
            request.InitialLineage,
            request.ReusePolicy,
            request.BindingGeneration,
            request.ReuseAuthorityActorId,
            bindPublisherActorId: ActiveInboundEnvelope?.Route?.PublisherActorId,
            toolCatalogPolicyVersion: request.ToolCatalogPolicyVersion);

    public override Task<string> GetDescriptionAsync()
    {
        var status = State.Compiled ? (State.Status?.Trim() ?? "bound") : "invalid";
        return Task.FromResult($"WorkflowRunGAgent[{State.WorkflowName}] run={RunId} ({status})");
    }

    protected override async Task<bool> PrepareEnvelopeHandlingAsync(
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!await base.PrepareEnvelopeHandlingAsync(envelope, ct))
            return false;
        if (envelope.Payload?.Is(StartWorkflowEvent.Descriptor) != true)
            return true;

        var start = envelope.Payload.Unpack<StartWorkflowEvent>();
        var requestedRunId = string.IsNullOrWhiteSpace(start.RunId)
            ? WorkflowRunIdNormalizer.Normalize(State.RunId)
            : WorkflowRunIdNormalizer.Normalize(start.RunId);
        var currentRunId = WorkflowRunIdNormalizer.Normalize(State.RunId);
        var valid = !string.IsNullOrWhiteSpace(currentRunId) &&
                    string.Equals(currentRunId, requestedRunId, StringComparison.Ordinal) &&
                    start.BindingGeneration == State.BindingGeneration &&
                    !IsTerminalStatus(State.Status) &&
                    IsValidCommittedReuseBinding(State);
        if (valid)
            valid = HasValidInheritedStartPublisher(start, envelope, State);
        if (valid && State.ReusePolicy == WorkflowRunActorReusePolicy.SerialSingleton)
        {
            var publisherActorId = envelope.Route?.PublisherActorId?.Trim() ?? string.Empty;
            valid = string.Equals(publisherActorId, State.ReuseAuthorityActorId, StringComparison.Ordinal) ||
                    string.Equals(publisherActorId, Id, StringComparison.Ordinal);
        }

        if (valid)
            return true;

        Logger.LogWarning(
            "Ignore fenced workflow start. actor={ActorId} currentRun={CurrentRunId} requestedRun={RequestedRunId} currentGeneration={CurrentGeneration} requestedGeneration={RequestedGeneration} status={Status}",
            Id,
            currentRunId,
            requestedRunId,
            State.BindingGeneration,
            start.BindingGeneration,
            State.Status);
        return false;
    }

    private static bool HasValidInheritedStartPublisher(
        StartWorkflowEvent start,
        EventEnvelope envelope,
        WorkflowRunState state)
    {
        if (start.ExecutionContextDelta == null)
            return true;

        var publisherActorId = envelope.Route?.PublisherActorId?.Trim() ?? string.Empty;
        var committedParentActorId =
            state.Lineage?.SubWorkflow?.Availability == WorkflowRunLineageAvailability.Available
                ? state.Lineage.SubWorkflow.ParentActorId?.Trim() ?? string.Empty
                : string.Empty;
        return !string.IsNullOrWhiteSpace(committedParentActorId) &&
               string.Equals(publisherActorId, committedParentActorId, StringComparison.Ordinal);
    }

    [EventHandler]
    public async Task HandleChatRequest(WorkflowChatRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsTerminalStatus(State.Status))
        {
            Logger.LogInformation(
                "Ignore chat request for terminal workflow run. actor={ActorId} run={RunId} status={Status}",
                Id,
                RunId,
                State.Status);
            return;
        }

        var commandId = ActiveInboundEnvelope?.Id?.Trim() ?? string.Empty;
        var correlationId = ActiveInboundEnvelope?.Propagation?.CorrelationId?.Trim() ?? string.Empty;
        if (!TryAcquireChatCommandExecutionLease(commandId))
            return;

        try
        {
            var start = await PrepareChatRequestStartAsync(request, commandId, correlationId);
            if (start == null)
                return;

            await PublishStartWorkflowOrTerminalFailureAsync(start, request.SessionId, CancellationToken.None);
            await SendWorkflowRunStartedNotificationAsync(CancellationToken.None);
        }
        finally
        {
            ReleaseChatCommandExecutionLease(commandId);
        }
    }

    private async Task<StartWorkflowEvent?> PrepareChatRequestStartAsync(
        WorkflowChatRequestEvent request,
        string commandId,
        string correlationId)
    {
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
                return null;
            }
        }

        var valueRepresentation = await SelectWorkflowRunRepresentationAsync(
            request.ForkSeed,
            CancellationToken.None);

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
            return null;
        }

        // Refactor (iter163/cluster-002-first):
        //   Old pattern: actor read command id from request.Headers[workflow.command_id],
        //                making Headers a stable control flow channel.
        //   New principle: actor reads command id from ActiveInboundEnvelope.Id,
        //                  the typed envelope identity.
        ValidateUnattendedWebhookCredential(request, scopeId);
        var callerCredentialDelta = await WorkflowCallerCredentialRuntimeContextAccess.BuildCredentialDeltaAsync(
            this,
            request.CallerCredential,
            CancellationToken.None);
        if (request.CallerCredential?.UnattendedEffectAuthorization is { } unattendedAuthorization)
        {
            callerCredentialDelta.UnattendedEffectAuthorization = unattendedAuthorization.Clone();
        }
        else
        {
            callerCredentialDelta.ClearUnattendedEffectAuthorization = true;
        }
        _runtimeContext.ApplyRequestMetadata(request.Metadata);
        _runtimeContext.ApplySenderNyxIdAccessToken(request.LlmControl?.SenderNyxIdAccessToken);
        var llmControlDelta = WorkflowRunExecutionContextStateAccess.BuildLlmControlDelta(request.LlmControl);
        var inputFileRefs = ExtractInputFileRefs(request.InputParts);
        LogWorkflowChatRequestStartBoundary(request, runId, commandId, correlationId, scopeId, inputFileRefs);

        Logger.LogWarning(
            "Workflow run agent tree ensure starting. workflowName={WorkflowName} runId={RunId} commandId={CommandId} linkedRoleActorCount={LinkedRoleActorCount} effectiveRoleCount={EffectiveRoleCount} effectiveRoles={EffectiveRoles}",
            _compiledWorkflow.Name,
            runId,
            commandId,
            _childAgentIds.Count,
            WorkflowImplicitLlmRolePolicy.GetEffectiveRoles(_compiledWorkflow).Count(),
            FormatWorkflowRoles(_compiledWorkflow));
        await EnsureAgentTreeAsync();
        Logger.LogWarning(
            "Workflow run agent tree ensure completed. workflowName={WorkflowName} runId={RunId} commandId={CommandId} linkedRoleActorCount={LinkedRoleActorCount}",
            _compiledWorkflow.Name,
            runId,
            commandId,
            _childAgentIds.Count);

        inputFileRefs = StampInputFileRefs(inputFileRefs, runId, scopeId);
        var firstInputFileRef = inputFileRefs.FirstOrDefault();
        Logger.LogWarning(
            "Workflow chat request input file refs extracted. workflowName={WorkflowName} runId={RunId} commandId={CommandId} correlationId={CorrelationId} scopeId={ScopeId} requestInputPartCount={RequestInputPartCount} inputFileRefCount={InputFileRefCount} firstFileId={FirstFileId} firstArtifactId={FirstArtifactId} firstMediaType={FirstMediaType}",
            _compiledWorkflow.Name,
            runId,
            commandId,
            correlationId,
            scopeId ?? string.Empty,
            request.InputParts.Count,
            inputFileRefs.Count,
            firstInputFileRef?.FileId ?? string.Empty,
            firstInputFileRef?.ArtifactId ?? string.Empty,
            firstInputFileRef?.MediaType ?? string.Empty);
        if (!await BindInputFileArtifactsAsync(inputFileRefs, runId))
        {
            await HandleWorkflowCompleted(new WorkflowCompletedEvent
            {
                RunId = runId,
                WorkflowName = _compiledWorkflow.Name,
                Success = false,
                Error = InputFileBindingError,
                RecoveryFailureKind = WorkflowRecoveryFailureKind.ConfigurationFailure,
            }, request.SessionId);
            return null;
        }

        var executionContextDelta = MergeExecutionContextDeltas(
            callerCredentialDelta,
            llmControlDelta,
            WorkflowRunExecutionContextStateAccess.ClearWorkflowRuntimeDelta());
        var requiresJsonInput = WorkflowRunInputContract.RequiresJsonInput(_compiledWorkflow);
        var executionInput = ResolveExecutionInput(request, requiresJsonInput);
        if (requiresJsonInput &&
            !WorkflowRunInputContract.IsBoundedJson(executionInput))
        {
            // Fail at start, before any step runs, with a corrective message the
            // calling agent can act on — instead of letting the first bounded
            // transform throw an opaque mid-run failure (see WorkflowRunInputContract).
            await HandleWorkflowCompleted(new WorkflowCompletedEvent
            {
                RunId = runId,
                WorkflowName = _compiledWorkflow.Name,
                Success = false,
                Error = WorkflowRunInputContract.BuildViolationMessage(_compiledWorkflow.Name, executionInput),
                RecoveryFailureKind = WorkflowRecoveryFailureKind.ConfigurationFailure,
            }, request.SessionId);
            return null;
        }

        var start = new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            Input = executionInput,
            RunId = runId,
            ForkSeed = request.ForkSeed,
            BindingGeneration = State.BindingGeneration,
            ValueRepresentation = valueRepresentation,
        };
        start.InputFileRefs.Add(inputFileRefs.Select(static fileRef => fileRef.Clone()));

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
            CurrentTurnId = string.IsNullOrWhiteSpace(request.CurrentTurnId)
                ? request.ConversationContext?.CurrentTurnId ?? string.Empty
                : request.CurrentTurnId,
            Lineage = BuildExecutionStartLineage(request.ForkSeed, State.Lineage, runId),
            PendingStartWorkflow = start.Clone(),
        };
        if (request.CompletionNotificationTarget != null)
            executionStarted.CompletionNotificationTarget = request.CompletionNotificationTarget.Clone();
        await PersistDomainEventAsync(executionStarted);
        return start;
    }

    private Task<WorkflowExecutionValueRepresentation> SelectWorkflowRunRepresentationAsync(
        WorkflowRunForkSeed? forkSeed,
        CancellationToken ct) =>
        WorkflowNormalizedStateWriteAdmission.SelectNewRunRepresentationAsync(
            (IWorkflowExecutionStateHost)this,
            forkSeed,
            ct,
            requiresValueLifecycle: WorkflowValueLifecyclePolicy.HasDeclarations(_compiledWorkflow));

    private async Task<WorkflowExecutionValueRepresentation> SelectSubWorkflowValueRepresentationAsync(
        WorkflowExecutionValueRepresentation requested,
        CancellationToken ct)
    {
        if (!System.Enum.IsDefined(requested))
            throw new InvalidOperationException("Sub-workflow start declares an unknown value representation.");
        if (requested == WorkflowExecutionValueRepresentation.Legacy)
            return requested;

        var selected = await SelectWorkflowRunRepresentationAsync(forkSeed: null, ct);
        if (requested == WorkflowExecutionValueRepresentation.Normalized &&
            selected != WorkflowExecutionValueRepresentation.Normalized)
        {
            throw new InvalidOperationException(
                "A normalized sub-workflow start requires live fleet admission for normalized state writes.");
        }

        return requested == WorkflowExecutionValueRepresentation.Unspecified
            ? selected
            : requested;
    }

    private static WorkflowExecutionValueRepresentation NormalizePersistedValueRepresentation(
        WorkflowExecutionValueRepresentation representation) =>
        representation switch
        {
            WorkflowExecutionValueRepresentation.Unspecified => WorkflowExecutionValueRepresentation.Legacy,
            WorkflowExecutionValueRepresentation.Legacy => WorkflowExecutionValueRepresentation.Legacy,
            WorkflowExecutionValueRepresentation.Normalized => WorkflowExecutionValueRepresentation.Normalized,
            _ => throw new InvalidOperationException(
                $"Workflow Run persisted an unknown value representation '{representation}'."),
        };

    private async Task EnsureNormalizedRunStartAdmissionAsync(
        WorkflowChatRequestEvent request,
        CancellationToken ct)
    {
        _ = await SelectWorkflowRunRepresentationAsync(request.ForkSeed, ct);
    }

    private void ValidateUnattendedWebhookCredential(WorkflowChatRequestEvent request, string scopeId)
    {
        var authorization = request.CallerCredential?.UnattendedEffectAuthorization;
        if (authorization is null)
            return;

        if (!string.Equals(State.RunOrigin, WorkflowRunOrigins.Webhook, StringComparison.Ordinal) ||
            request.ExternalIngress is null ||
            State.ExpectedExecutionMode != ExternalCapabilityExecutionMode.Durable ||
            State.CapabilityAdmissionPlan is null ||
            request.CallerCredential?.NyxIdAuthority is not { } authority)
        {
            throw new InvalidOperationException(
                "Unattended effect authorization is only valid for an exact durable webhook run.");
        }

        WorkflowUnattendedEffectAuthorizationIntegrity.ValidateForDefinition(
            authorization,
            authority,
            request.ExternalIngress.RouteKey,
            State.DefinitionActorId,
            scopeId,
            State.WorkflowId,
            State.RevisionId,
            State.DefinitionVersion,
            State.CapabilityAdmissionPlan);
    }

    private bool TryAcquireChatCommandExecutionLease(string commandId)
    {
        if (_inFlightChatCommandId == null)
        {
            _inFlightChatCommandId = commandId;
            return true;
        }

        if (string.Equals(_inFlightChatCommandId, commandId, StringComparison.Ordinal))
            return false;

        throw new InvalidOperationException("workflow run is already processing another command");
    }

    private void ReleaseChatCommandExecutionLease(string commandId)
    {
        if (string.Equals(_inFlightChatCommandId, commandId, StringComparison.Ordinal))
            _inFlightChatCommandId = null;
    }

    [EventHandler]
    public async Task HandleReplaceWorkflowDefinitionAndExecute(ReplaceWorkflowDefinitionAndExecuteEvent request)
    {
        if (IsTerminalStatus(State.Status))
        {
            Logger.LogInformation(
                "Ignore dynamic definition replacement for terminal workflow run. actor={ActorId} run={RunId} status={Status}",
                Id,
                RunId,
                State.Status);
            return;
        }

        if (GetPendingWorkflowCompletion(State) != null)
        {
            Logger.LogInformation(
                "Ignore dynamic definition replacement while terminal completion is pending. actor={ActorId} run={RunId}",
                Id,
                RunId);
            await RecoverPendingWorkflowCompletionAsync(CancellationToken.None);
            return;
        }

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

        var replaceResult = await ReplaceWorkflowDefinitionBypassingBindingAsync(
            yaml,
            request.Input ?? string.Empty);
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
        catch (Exception ex) when (
            !ct.IsCancellationRequested &&
            !WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(ex))
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
                ValueLifecycleFailureKind = ex is WorkflowValueLifecycleException lifecycleFailure
                    ? lifecycleFailure.Kind
                    : WorkflowValueLifecycleFailureKind.Unspecified,
            };
            try
            {
                await HandleWorkflowCompleted(terminal);
            }
            catch (Exception terminalEx) when (
                !ct.IsCancellationRequested &&
                !WorkflowRuntimeInfrastructureFailurePolicy.IsCommitConsistencyFailure(terminalEx))
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

    [EventHandler(Priority = -5)]
    public async Task HandleInteractiveAuthorizationRequirementAsync(
        WorkflowLlmInvocationCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (completed.AuthorizationRequirement is not { } requirement)
            return;

        var terminalFailure = BuildInteractiveTerminalFailure(completed, requirement);
        var handoffCompletion = BuildInteractiveActionHandoffCompletion(completed);
        if (!TryBuildInteractiveActionHandoff(
                completed,
                requirement,
                handoffCompletion,
                out var handoff,
                out var command))
        {
            await PublishInteractiveTerminalContinuationAsync(completed, terminalFailure);
            return;
        }

        var existing = State.InteractiveActionHandoffs.FirstOrDefault(candidate =>
            string.Equals(candidate.HandoffId, handoff.HandoffId, StringComparison.Ordinal) ||
            (string.Equals(
                 candidate.TerminalContinuation?.RunId,
                 handoff.TerminalContinuation.RunId,
                 StringComparison.Ordinal) &&
             string.Equals(
                 candidate.TerminalContinuation?.StepId,
                 handoff.TerminalContinuation.StepId,
                 StringComparison.Ordinal) &&
             string.Equals(
                 candidate.TerminalContinuation?.SessionId,
                 handoff.TerminalContinuation.SessionId,
                 StringComparison.Ordinal)));
        if (existing is not null)
        {
            if (!existing.Request.ToByteString().Equals(handoff.Request.ToByteString()))
            {
                throw new InvalidOperationException(
                    "An interactive action handoff identity was reused with different content.");
            }

            if (!existing.ContinuationDispatched)
                await DispatchInteractiveActionContinuationAsync(existing, CancellationToken.None);
            return;
        }

        try
        {
            await EnsureInteractiveActionActorHandoffAsync(command, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.LogError(
                exception,
                "Interactive action handoff failed run={RunId} step={StepId} session={SessionId} actor={ActorId}.",
                completed.RunId,
                completed.StepId,
                completed.SessionId,
                command.Request.ActorId);
            await PublishInteractiveTerminalContinuationAsync(completed, terminalFailure);
            return;
        }

        await PersistDomainEventAsync(new WorkflowInteractiveActionHandoffDispatchedEvent
        {
            HandoffId = handoff.HandoffId,
            Request = handoff.Request.Clone(),
            TerminalContinuation = handoff.TerminalContinuation.Clone(),
        }, CancellationToken.None);

        var committed = State.InteractiveActionHandoffs.Single(candidate =>
            string.Equals(candidate.HandoffId, handoff.HandoffId, StringComparison.Ordinal));
        await DispatchInteractiveActionContinuationAsync(committed, CancellationToken.None);
    }

    private Task PublishInteractiveTerminalContinuationAsync(
        WorkflowLlmInvocationCompletedEvent completed,
        WorkflowLlmInvocationCompletedEvent terminalContinuation) =>
        PublishAsync(
            terminalContinuation,
            TopologyAudience.Self,
            CancellationToken.None,
            BuildDeliveryOptions(BuildStableIdentity(
                "interactive-terminal",
                Id,
                completed.RunId,
                completed.StepId,
                completed.SessionId)));

    private bool TryBuildInteractiveActionHandoff(
        WorkflowLlmInvocationCompletedEvent completed,
        WorkflowInteractiveAuthorizationRequirement requirement,
        WorkflowLlmInvocationCompletedEvent terminalContinuation,
        out WorkflowInteractiveActionHandoffState handoff,
        out WorkflowInteractiveActionHandoffCommand command)
    {
        handoff = null!;
        command = null!;
        var callerAuthority = CallerNyxIdAuthority;
        var currentTurnId = NormalizeInteractiveValue(State.CurrentTurnId, 256);
        var scopeId = NormalizeInteractiveValue(State.ScopeId, 256);
        if (State.ExpectedExecutionMode != ExternalCapabilityExecutionMode.Interactive ||
            callerAuthority is null ||
            currentTurnId is null ||
            scopeId is null)
        {
            return false;
        }

        if (!TryBuildInteractiveActionRequestParts(
                requirement,
                out var wireAction,
                out var wireParams,
                out var identityParts))
        {
            return false;
        }

        var actionActorId = wireAction == "service.connect"
            ? BuildStableIdentity(
                "nyxid-chat",
                scopeId,
                callerAuthority.ExternalUserId,
                Id,
                currentTurnId,
                identityParts[0])
            : BuildStableIdentity(
                "nyxid-chat",
                [
                    scopeId,
                    callerAuthority.ExternalUserId,
                    Id,
                    currentTurnId,
                    wireAction,
                    .. identityParts,
                ]);
        var taskId = BuildStableIdentity("task", actionActorId, currentTurnId, completed.SessionId);
        var actionRequestId = wireAction == "service.connect"
            ? BuildStableIdentity(
                "action",
                actionActorId,
                currentTurnId,
                taskId,
                identityParts[0],
                identityParts[1])
            : BuildStableIdentity(
                "action",
                [
                    actionActorId,
                    currentTurnId,
                    taskId,
                    wireAction,
                    .. identityParts,
                ]);
        var stepId = BuildStableIdentity(
            "step",
            actionActorId,
            currentTurnId,
            taskId,
            actionRequestId,
            "browser-action");
        var request = new WorkflowInteractiveActionRequestWirePayload
        {
            SchemaVersion = 4,
            ActorId = actionActorId,
            OriginTurnId = currentTurnId,
            TaskId = taskId,
            StepId = stepId,
            ActionRequestId = actionRequestId,
            Action = wireAction,
            Params = wireParams,
        };
        var handoffId = BuildStableIdentity(
            "handoff",
            Id,
            completed.RunId,
            completed.StepId,
            completed.SessionId,
            actionRequestId);
        handoff = new WorkflowInteractiveActionHandoffState
        {
            HandoffId = handoffId,
            Request = request,
            TerminalContinuation = terminalContinuation.Clone(),
        };
        command = new WorkflowInteractiveActionHandoffCommand
        {
            HandoffId = handoffId,
            ScopeId = scopeId,
            OwnerSubject = callerAuthority.ExternalUserId,
            SourceWorkflowActorId = Id,
            Request = request.Clone(),
        };
        return true;
    }

    private static bool TryBuildInteractiveActionRequestParts(
        WorkflowInteractiveAuthorizationRequirement requirement,
        out string wireAction,
        out WorkflowInteractiveActionParams wireParams,
        out string[] identityParts)
    {
        wireAction = string.Empty;
        wireParams = null!;
        identityParts = [];
        var serviceSlug = NormalizeInteractiveValue(requirement.ServiceSlug, 128);
        var hasServiceConnect = serviceSlug is not null;
        var hasKeyCreate = requirement.KeyCreate is not null;
        if (hasServiceConnect == hasKeyCreate)
            return false;

        if (hasServiceConnect)
        {
            if (!serviceSlug!.All(static character =>
                    char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            {
                return false;
            }

            var requestedScopes = requirement.RequestedScopes
                .Select(scope => NormalizeInteractiveValue(scope, 256))
                .Where(static scope => scope is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (requestedScopes.Length != requirement.RequestedScopes.Count)
                return false;

            wireAction = "service.connect";
            wireParams = new WorkflowInteractiveActionParams
            {
                CatalogService = new WorkflowInteractiveCatalogServiceActionParams
                {
                    ServiceSlug = serviceSlug,
                    RequestedScopes = { requestedScopes },
                },
            };
            identityParts = [serviceSlug!, string.Join("\n", requestedScopes)];
            return true;
        }

        var keyCreate = requirement.KeyCreate;
        if (requirement.RequestedScopes.Count > 0 || keyCreate is null)
            return false;
        var name = NormalizeInteractiveValue(keyCreate.Name, 256);
        var platform = NormalizeInteractiveValue(keyCreate.Platform, 128);
        var allowedServiceIds = keyCreate.AllowedServiceIds
            .Select(id => NormalizeInteractiveValue(id, 256))
            .Where(static id => id is not null)
            .Cast<string>()
            .ToArray();
        if (name is null || platform is null ||
            allowedServiceIds.Length is < 1 or > 64 ||
            allowedServiceIds.Length != keyCreate.AllowedServiceIds.Count ||
            !allowedServiceIds.SequenceEqual(keyCreate.AllowedServiceIds, StringComparer.Ordinal) ||
            allowedServiceIds.Distinct(StringComparer.Ordinal).Count() != allowedServiceIds.Length)
        {
            return false;
        }

        wireAction = "key.create";
        wireParams = new WorkflowInteractiveActionParams
        {
            KeyCreate = new WorkflowInteractiveKeyCreateActionParams
            {
                Name = name,
                Platform = platform,
                AllowedServiceIds = { allowedServiceIds },
            },
        };
        identityParts = [name, platform, string.Join("\n", allowedServiceIds)];
        return true;
    }

    private async Task EnsureInteractiveActionActorHandoffAsync(
        WorkflowInteractiveActionHandoffCommand command,
        CancellationToken ct)
    {
        var actorId = command.Request.ActorId;
        var actor = await _runtime.GetAsync(actorId) ??
                    await _runtime.CreateByKindAsync(NyxIdChatAgentKind, actorId, ct);
        await _runtime.LinkAsync(Id, actor.Id, ct);
        await SendToAsync(
            actor.Id,
            command,
            ct,
            BuildDeliveryOptions(BuildStableIdentity(
                "interactive-handoff-delivery",
                command.HandoffId,
                actor.Id)));
    }

    private async Task DispatchPendingInteractiveActionContinuationsAsync(CancellationToken ct)
    {
        foreach (var handoff in State.InteractiveActionHandoffs
                     .Where(static handoff => !handoff.ContinuationDispatched)
                     .ToArray())
        {
            await DispatchInteractiveActionContinuationAsync(handoff, ct);
        }
    }

    private async Task DispatchInteractiveActionContinuationAsync(
        WorkflowInteractiveActionHandoffState handoff,
        CancellationToken ct)
    {
        await PublishAsync(
            handoff.TerminalContinuation.Clone(),
            TopologyAudience.Self,
            ct,
            BuildDeliveryOptions(BuildStableIdentity(
                "interactive-continuation-delivery",
                handoff.HandoffId)));
        await PersistDomainEventAsync(new WorkflowInteractiveActionContinuationDispatchedEvent
        {
            HandoffId = handoff.HandoffId,
        }, ct);
    }

    private static WorkflowLlmInvocationCompletedEvent BuildInteractiveActionHandoffCompletion(
        WorkflowLlmInvocationCompletedEvent completed) =>
        new()
        {
            RunId = completed.RunId,
            StepId = completed.StepId,
            SessionId = completed.SessionId,
            RoleActorId = completed.RoleActorId,
            Success = true,
            Usage = completed.Usage?.Clone(),
        };

    private static WorkflowLlmInvocationCompletedEvent BuildInteractiveTerminalFailure(
        WorkflowLlmInvocationCompletedEvent completed,
        WorkflowInteractiveAuthorizationRequirement requirement) =>
        new()
        {
            RunId = completed.RunId,
            StepId = completed.StepId,
            SessionId = completed.SessionId,
            RoleActorId = completed.RoleActorId,
            Success = false,
            Error = NormalizeInteractiveValue(completed.Error, 512) ??
                    NormalizeInteractiveValue(requirement.SafeMessage, 512) ??
                    "The requested service requires authorization.",
            RecoveryFailureKind = WorkflowRecoveryFailureKind.AuthorizationFailure,
            Usage = completed.Usage?.Clone(),
        };

    private static EventEnvelopePublishOptions BuildDeliveryOptions(string operationId) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = operationId,
            },
        };

    private static string? NormalizeInteractiveValue(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is not null &&
               normalized.Length <= maxLength &&
               !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }

    private static string BuildStableIdentity(string prefix, params string[] parts)
    {
        var identity = string.Concat(parts.Select(static part => $"{part.Length}:{part}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..32]}";
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

    private static string ResolveExecutionInput(
        WorkflowChatRequestEvent request,
        bool requiresJsonInput)
    {
        if (request.ForkSeed != null &&
            request.ForkSeed.Variables.TryGetValue("input", out var seedInput))
        {
            return seedInput ?? string.Empty;
        }

        if (!requiresJsonInput && request.ConversationContext != null)
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
                if (string.IsNullOrWhiteSpace(clone.OwnerRunId))
                {
                    clone.OwnerRunId = runId;
                    clone.OwnerScopeId = scopeId;
                }

                return clone;
            })
            .ToArray();

    private async Task<bool> BindInputFileArtifactsAsync(
        IReadOnlyList<WorkflowFileRef> fileRefs,
        string runId)
    {
        if (fileRefs.Count == 0 || _fileArtifactOwnership == null)
            return true;

        foreach (var fileRef in fileRefs)
        {
            try
            {
                await _fileArtifactOwnership.BindOwnerAsync(
                    ToApplicationFileArtifactRef(fileRef),
                    fileRef.OwnerRunId!,
                    fileRef.OwnerScopeId,
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
        await _subWorkflowOrchestrator.HandleDefinitionResolvedAsync(
            resolved,
            ActiveInboundEnvelope?.Route?.PublisherActorId,
            State,
            CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleSubWorkflowDefinitionResolveFailed(SubWorkflowDefinitionResolveFailedEvent failed)
    {
        await _subWorkflowOrchestrator.HandleDefinitionResolveFailedAsync(
            failed,
            ActiveInboundEnvelope?.Route?.PublisherActorId,
            State,
            CancellationToken.None);
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
    public async Task HandleWorkflowToolCallTerminalCleanupRetryFired(
        WorkflowToolCallTerminalCleanupRetryFiredEvent retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (!IsTerminalStatus(State.Status) ||
            IsCompensating(State) ||
            !string.Equals(retry.RunId, RunId, StringComparison.Ordinal))
        {
            Logger.LogDebug(
                "Ignore stale terminal workflow tool cleanup retry. actor={ActorId} currentRun={CurrentRunId} retryRun={RetryRunId} status={Status}",
                Id,
                RunId,
                retry.RunId,
                State.Status);
            return;
        }

        DisableExecutionModules();
        await CleanupTerminalToolCallStateAsync(
            CancellationToken.None,
            allowImmediateFallback: false);
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
    public async Task HandleWorkflowToolApprovalSuspendedEnvelope(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(WorkflowSuspendedEvent.Descriptor) != true)
            return;

        var suspended = envelope.Payload.Unpack<WorkflowSuspendedEvent>();
        var publisherActorId = envelope.Route?.PublisherActorId ?? string.Empty;
        var target = State.CompletionNotificationTarget;
        if (!string.Equals(publisherActorId, Id, StringComparison.Ordinal) ||
            !string.Equals(suspended.RunId, RunId, StringComparison.Ordinal) ||
            !string.Equals(suspended.SuspensionType, "tool_approval", StringComparison.Ordinal) ||
            !HasCompletionNotificationTarget(target) ||
            target!.ExpiresAtUnixMs <= _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() ||
            string.IsNullOrWhiteSpace(State.LastCommandId) ||
            !HasToolApprovalIdentity(suspended))
        {
            return;
        }

        var notification = new WorkflowRunToolApprovalNotification
        {
            DeliveryId = target.DeliveryId.Trim(),
            WorkflowActorId = Id,
            WorkflowRunId = RunId,
            WorkflowCommandId = State.LastCommandId.Trim(),
            WorkflowCorrelationId = State.WorkflowCorrelationId?.Trim() ?? string.Empty,
            StepId = suspended.StepId.Trim(),
            ExecutionId = suspended.ToolApproval.ExecutionId.Trim(),
            ToolName = suspended.ToolApproval.ToolName.Trim(),
            ToolCallId = suspended.ToolApproval.ToolCallId.Trim(),
            ApprovalRequestId = suspended.ToolApproval.ApprovalRequestId.Trim(),
            Prompt = suspended.Prompt?.Trim() ?? string.Empty,
            RequestedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        };
        await SendToAsync(
            target.ActorId.Trim(),
            notification,
            CancellationToken.None,
            BuildToolApprovalNotificationDispatchOptions(notification));
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
        if (IsMismatchedRunIdentity(State, evt.RunId))
        {
            Logger.LogWarning(
                "Ignore WorkflowCompletedEvent with mismatched run id. actor={ActorId} stateRun={StateRunId} eventRun={EventRunId}",
                Id,
                State.RunId,
                evt.RunId);
            return;
        }

        if (ShouldIgnoreWorkflowCompleted(State))
        {
            Logger.LogDebug(
                "Ignore duplicate WorkflowCompletedEvent for terminal run={RunId} status={Status}.",
                string.IsNullOrWhiteSpace(evt.RunId) ? RunId : evt.RunId,
                State.Status);
            await RecoverDuplicateTerminalToolCallCleanupAsync(CancellationToken.None);
            return;
        }

        var stateBeforeCompletion = State.Clone();
        var completedAt = evt.CompletedAtUtc ?? Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        var persistedEvent = evt.Clone();
        persistedEvent.CompletedAtUtc = null;
        await PersistDomainEventsAsync(
            [
                persistedEvent,
                new WorkflowRunTerminalTimingRecordedEvent
                {
                    RunId = string.IsNullOrWhiteSpace(persistedEvent.RunId) ? RunId : persistedEvent.RunId,
                    CompletedAtUtc = completedAt,
                },
            ]);
        DisableExecutionModules();
        await TryRevokeScheduledCallerCredentialAsync(
            stateBeforeCompletion,
            "workflow-run-completed",
            CancellationToken.None);
        if (!ShouldSuppressGenericParentCompletion(stateBeforeCompletion))
            await PublishAsync(persistedEvent.Clone(), TopologyAudience.Parent);
        await PersistForkRequestOnTerminalFailureAsync(persistedEvent, stateBeforeCompletion, CancellationToken.None);
        await _subWorkflowOrchestrator.CancelPendingDefinitionResolutionTimeoutsAsync(stateBeforeCompletion, CancellationToken.None);
        await _subWorkflowOrchestrator.CleanupPendingInvocationsForRunAsync(evt.RunId, stateBeforeCompletion, CancellationToken.None);
        await CleanupRoleAgentTreeAsync(CancellationToken.None);
        _runtimeContext.Clear();
        if (evt.Success)
        {
            Logger.LogInformation(
                "Workflow run {Name} completed: success={Success} run={RunId} outputLen={OutputLen}",
                persistedEvent.WorkflowName,
                persistedEvent.Success,
                persistedEvent.RunId,
                (persistedEvent.Output ?? string.Empty).Length);
        }
        else
        {
            Logger.LogError(
                "Workflow run {Name} failed: run={RunId} error={Error} outputLen={OutputLen}",
                persistedEvent.WorkflowName,
                persistedEvent.RunId,
                string.IsNullOrWhiteSpace(persistedEvent.Error) ? "(none)" : persistedEvent.Error,
                (persistedEvent.Output ?? string.Empty).Length);
        }

        await PublishAsync(new WorkflowLlmInvocationCompletedEvent
        {
            RunId = persistedEvent.RunId,
            SessionId = sessionId?.Trim() ?? string.Empty,
            Success = persistedEvent.Success,
            Content = persistedEvent.Success ? persistedEvent.Output : $"Workflow execution failed: {persistedEvent.Error}",
            Error = persistedEvent.Success ? string.Empty : persistedEvent.Error,
        }, TopologyAudience.Parent);

        await PublishManagedParentInvocationCompletionAsync(persistedEvent, stateBeforeCompletion, CancellationToken.None);
        await EnsureTerminalNotificationAsync(CancellationToken.None);
        await CleanupTerminalToolCallStateAsync(CancellationToken.None);
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
            await RecoverDuplicateTerminalToolCallCleanupAsync(CancellationToken.None);
            return;
        }

        Logger.LogInformation(
            "Adopt relayed WorkflowCompletedEvent for own run={RunId} success={Success}.",
            RunId,
            evt.Success);
        var completedAt = evt.CompletedAtUtc ?? Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow());
        await PersistDomainEventsAsync(
            [
                NormalizeAdoptedCompleted(evt),
                new WorkflowRunTerminalTimingRecordedEvent
                {
                    RunId = RunId,
                    CompletedAtUtc = completedAt,
                },
            ]);
        DisableExecutionModules();
        await EnsureTerminalNotificationAsync(CancellationToken.None);
        await CleanupTerminalToolCallStateAsync(CancellationToken.None);
    }

    private WorkflowCompletedEvent NormalizeAdoptedCompleted(WorkflowCompletedEvent evt)
    {
        var normalized = evt.Clone();
        normalized.RunId = RunId;
        if (string.IsNullOrWhiteSpace(normalized.WorkflowName))
            normalized.WorkflowName = State.WorkflowName;
        normalized.CompletedAtUtc = null;
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

    [EventHandler(Priority = 50)]
    public async Task HandleWorkflowStopped(WorkflowStoppedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!TryPrepareStop(evt.RunId, nameof(WorkflowStoppedEvent), out var runId))
        {
            if (IsTerminalStatus(State.Status) && !IsMismatchedRunIdentity(State, evt.RunId))
                await RecoverDuplicateTerminalToolCallCleanupAsync(CancellationToken.None);
            return;
        }

        var persistedEvent = new WorkflowStoppedEvent
        {
            WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? State.WorkflowName : evt.WorkflowName,
            RunId = runId,
            Reason = evt.Reason ?? string.Empty,
            CompletedAtUtc = evt.CompletedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await CompleteStopAsync(
            runId,
            persistedEvent.WorkflowName,
            persistedEvent.Reason,
            ct => PersistDomainEventAsync(persistedEvent, ct),
            CancellationToken.None);
    }

    [EventHandler(Priority = 50)]
    public async Task HandleWorkflowRunStoppedAsync(WorkflowRunStoppedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!TryPrepareStop(evt.RunId, nameof(WorkflowRunStoppedEvent), out var runId))
        {
            if (IsTerminalStatus(State.Status) && !IsMismatchedRunIdentity(State, evt.RunId))
                await RecoverDuplicateTerminalToolCallCleanupAsync(CancellationToken.None);
            return;
        }

        var persistedEvent = new WorkflowRunStoppedEvent
        {
            RunId = runId,
            Reason = evt.Reason ?? string.Empty,
            CompletedAtUtc = evt.CompletedAtUtc ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
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
            CapturedOutputValueId = entry.CapturedOutputValueId ?? string.Empty,
            CapturedOutputProducerStepId = entry.CapturedOutputProducerStepId ?? string.Empty,
            CapturedOutputProducerExecutionId = entry.CapturedOutputProducerExecutionId ?? string.Empty,
            CapturedOutputSourceKind = entry.CapturedOutputSourceKind,
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

        var requireTypedRunId = State.ReusePolicy == WorkflowRunActorReusePolicy.SerialSingleton;
        if (!WorkflowArtifactFactBuilder.TryBuild(
                envelope,
                Id,
                State.RunId,
                requireTypedRunId,
                out var artifactFact))
            return;

        var artifactRunId = ResolveRunOwnedTransitionRunId(artifactFact);
        if ((requireTypedRunId && string.IsNullOrWhiteSpace(artifactRunId)) ||
            (!string.IsNullOrWhiteSpace(artifactRunId) && IsMismatchedRunIdentity(State, artifactRunId)))
        {
            Logger.LogWarning(
                "Ignore fenced workflow artifact. actor={ActorId} currentRun={CurrentRunId} artifactRun={ArtifactRunId} artifactType={ArtifactType}",
                Id,
                State.RunId,
                artifactRunId,
                artifactFact.Descriptor.FullName);
            return;
        }

        var source = ResolveArtifactSource(artifactFact);
        if (source != null &&
            (!IsValidArtifactSource(source) || IsProcessedArtifactSource(State, source)))
        {
            return;
        }

        if (artifactFact is WorkflowRuntimeOperationRecordedEvent
            {
                Kind: WorkflowRuntimeOperationKind.Model,
                Phase: WorkflowRuntimeOperationPhase.Started,
            } modelStarted && !IsMatchingToolCatalogProof(modelStarted))
        {
            Logger.LogWarning(
                "Ignore workflow model-start artifact with mismatched tool catalog proof. run={RunId} policy={PolicyVersion}",
                State.RunId,
                modelStarted.ToolCatalogPolicyVersion);
            return;
        }

        var stepCompletionKey = BuildStepCompletionArtifactKey(artifactFact as StepCompletedEvent);
        if (stepCompletionKey != null && State.ProcessedStepCompletionKeys.ContainsKey(stepCompletionKey))
            return;

        if (artifactFact is StepCompletedEvent normalizedCompletion &&
            TryGetNormalizedKernelState(State, out var normalizedKernelState))
        {
            var selfPublished = string.Equals(
                envelope.Route?.PublisherActorId?.Trim(),
                Id,
                StringComparison.Ordinal);
            if (!selfPublished ||
                !WorkflowExecutionValueStore.IsAcceptedCompletion(normalizedKernelState, normalizedCompletion))
            {
                Logger.LogWarning(
                    "Ignore non-authoritative normalized completion artifact. actor={ActorId} publisher={PublisherActorId} step={StepId} execution={ExecutionId}",
                    Id,
                    envelope.Route?.PublisherActorId ?? string.Empty,
                    normalizedCompletion.StepId,
                    normalizedCompletion.ExecutionId);
                return;
            }
        }

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

    private void LogWorkflowChatRequestStartBoundary(
        WorkflowChatRequestEvent request,
        string runId,
        string commandId,
        string correlationId,
        string? scopeId,
        IReadOnlyList<WorkflowFileRef> inputFileRefs)
    {
        if (_compiledWorkflow == null)
            return;

        var firstInputFileRef = inputFileRefs.FirstOrDefault();
        Logger.LogWarning(
            "Workflow chat request start boundary. workflowName={WorkflowName} runId={RunId} commandId={CommandId} correlationId={CorrelationId} scopeId={ScopeId} requestInputPartCount={RequestInputPartCount} rawInputFileRefCount={RawInputFileRefCount} firstFileId={FirstFileId} firstArtifactId={FirstArtifactId} firstMediaType={FirstMediaType} compiledStepCount={CompiledStepCount} compiledSteps={CompiledSteps} requiredModules={RequiredModules} effectiveRoleCount={EffectiveRoleCount} effectiveRoles={EffectiveRoles}",
            _compiledWorkflow.Name,
            runId,
            commandId,
            correlationId,
            scopeId ?? string.Empty,
            request.InputParts.Count,
            inputFileRefs.Count,
            firstInputFileRef?.FileId ?? string.Empty,
            firstInputFileRef?.ArtifactId ?? string.Empty,
            firstInputFileRef?.MediaType ?? string.Empty,
            _compiledWorkflow.Steps.Count,
            FormatWorkflowSteps(_compiledWorkflow),
            FormatRequiredModules(),
            WorkflowImplicitLlmRolePolicy.GetEffectiveRoles(_compiledWorkflow).Count(),
            FormatWorkflowRoles(_compiledWorkflow));
    }

    private string FormatRequiredModules()
    {
        if (_compiledWorkflow == null)
            return string.Empty;

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expander in _moduleDependencyExpanders)
            expander.Expand(_compiledWorkflow, needed);

        return string.Join(',', needed.Order(StringComparer.OrdinalIgnoreCase));
    }

    private static string FormatWorkflowSteps(WorkflowDefinition workflow) =>
        string.Join(';', workflow.Steps.Select(step =>
        {
            var stepId = NormalizeLogValue(step.Id) ?? "(none)";
            var stepType = NormalizeLogValue(WorkflowPrimitiveCatalog.ToCanonicalType(step.Type)) ?? "(none)";
            var targetRole = NormalizeLogValue(WorkflowImplicitLlmRolePolicy.ResolveEffectiveTargetRole(workflow, step)) ?? "(none)";
            return $"{stepId}:{stepType}:{targetRole}";
        }));

    private static string FormatWorkflowRoles(WorkflowDefinition workflow) =>
        string.Join(',', WorkflowImplicitLlmRolePolicy.GetEffectiveRoles(workflow)
            .Select(static role => NormalizeLogValue(role.Id) ?? "(none)"));

    private static string? NormalizeLogValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task EnsureAgentTreeAsync()
    {
        if (_childAgentIds.Count > 0 || _compiledWorkflow == null)
            return;

        foreach (var role in WorkflowImplicitLlmRolePolicy.GetEffectiveRoles(_compiledWorkflow))
        {
            var roleId = role.Id;
            var childActorId = BuildChildActorId(roleId);
            Logger.LogWarning(
                "Workflow role actor initialization starting. workflowName={WorkflowName} runActorId={RunActorId} roleId={RoleId} childActorId={ChildActorId}",
                _compiledWorkflow.Name,
                Id,
                roleId,
                childActorId);
            var actor = await _runtime.GetAsync(childActorId)
                        ?? await CreateRoleActorAsync(role, childActorId);
            await _runtime.LinkAsync(Id, actor.Id);

            await DispatchRoleInitializationAsync(actor.Id, WorkflowRoleAgentEnvelopeFactory.CreateInitializeEnvelope(role, Id, actor.Id));
            Logger.LogWarning(
                "Workflow role actor initialization dispatched. workflowName={WorkflowName} runActorId={RunActorId} roleId={RoleId} childActorId={ChildActorId}",
                _compiledWorkflow.Name,
                Id,
                roleId,
                actor.Id);
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

        Logger.LogWarning(
            "Workflow run actor tree created. workflowName={WorkflowName} runActorId={RunActorId} linkedRoleActorCount={LinkedRoleActorCount}",
            _compiledWorkflow.Name,
            Id,
            _childAgentIds.Count);
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
        CancelWorkflowExecutionBackgroundWork();
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

    private void DisableExecutionModules()
    {
        CancelWorkflowExecutionBackgroundWork();
        SetModules([]);
    }

    private void CancelWorkflowExecutionBackgroundWork()
    {
        foreach (var bridge in GetModules().OfType<WorkflowExecutionBridgeModule>())
            bridge.CancelBackgroundWork();
    }

    private void ConfigureModule(IEventModule<IWorkflowExecutionContext> module)
    {
        if (_compiledWorkflow == null)
            return;

        foreach (var configurator in _moduleConfigurators)
            configurator.Configure(module, _compiledWorkflow);
    }

    protected override WorkflowRunState TransitionState(WorkflowRunState current, IMessage evt)
    {
        var eventRunId = ResolveRunOwnedTransitionRunId(evt);
        if (eventRunId is not null && IsMismatchedRunIdentity(current, eventRunId))
            return current;

        return StateTransitionMatcher
            .Match(current, evt)
            .On<BindWorkflowRunDefinitionEvent>(ApplyBindWorkflowRunDefinition)
            .On<WorkflowRunDefinitionBindingContinuationRegisteredEvent>(
                ApplyDefinitionBindingContinuationRegistered)
            .On<WorkflowRunDefinitionBindingCleanupCompletedEvent>(
                ApplyDefinitionBindingCleanupCompleted)
            .On<WorkflowRunDefinitionBindingContinuationClearedEvent>(
                ApplyDefinitionBindingContinuationCleared)
            .On<WorkflowRunCompletionNotificationTargetAdoptedEvent>(ApplyWorkflowRunCompletionNotificationTargetAdopted)
            .On<WorkflowCommandObservedEvent>(ApplyWorkflowCommandObserved)
            .On<WorkflowRunExecutionStartedEvent>(ApplyWorkflowRunExecutionStarted)
            .On<WorkflowRunExecutionContextUpdatedEvent>(ApplyWorkflowRunExecutionContextUpdated)
            .On<WorkflowRunExecutionContextClearedEvent>(ApplyWorkflowRunExecutionContextCleared)
            .On<WorkflowExecutionStateUpsertedEvent>(ApplyWorkflowExecutionStateUpserted)
            .On<WorkflowExecutionStateClearedEvent>(ApplyWorkflowExecutionStateCleared)
            .On<WorkflowRunLineageRecordedEvent>(ApplyWorkflowRunLineageRecorded)
            .On<CompensableStepDispatchedEvent>(ApplyCompensableStepDispatched)
            .On<CompensableStepOutputCapturedEvent>(ApplyCompensableStepOutputCaptured)
            .On<StepCompletedEvent>(ApplyStepCompleted)
            .On<CompensationRequestEvent>(ApplyCompensationRequest)
            .On<CompensationStepCompletedEvent>(ApplyCompensationStepCompleted)
            .On<WorkflowCompensationCompletedEvent>(ApplyWorkflowCompensationCompleted)
            .On<WorkflowCompensationFailedEvent>(ApplyWorkflowCompensationFailed)
            .On<WorkflowCompensationRetryRequestedEvent>(ApplyWorkflowCompensationRetryRequested)
            .On<WorkflowStoppedEvent>(ApplyWorkflowStopped)
            .On<WorkflowCompletedEvent>(ApplyWorkflowCompleted)
            .On<WorkflowRunTerminalTimingRecordedEvent>(ApplyWorkflowRunTerminalTimingRecorded)
            .On<WorkflowRunStoppedEvent>(ApplyWorkflowRunStopped)
            .On<WorkflowRunTerminalNotificationPreparedEvent>(ApplyWorkflowRunTerminalNotificationPrepared)
            .On<WorkflowRunTerminalNotificationRetryScheduledEvent>(ApplyWorkflowRunTerminalNotificationRetryScheduled)
            .On<WorkflowRunTerminalNotificationDispatchedEvent>(ApplyWorkflowRunTerminalNotificationDispatched)
            .On<WorkflowRunTerminalNotificationExpiredEvent>(ApplyWorkflowRunTerminalNotificationExpired)
            .On<WorkflowRunTerminalNotificationRetryFiredEvent>(KeepCurrentState)
            .On<WorkflowToolCallTerminalCleanupRetryFiredEvent>(KeepCurrentState)
            .On<WorkflowRoleReplyRecordedEvent>(ApplyWorkflowRoleReplyRecorded)
            .On<WorkflowRuntimeOperationRecordedEvent>(ApplyWorkflowRuntimeOperationRecorded)
            .On<WorkflowInteractiveActionHandoffDispatchedEvent>(ApplyInteractiveActionHandoffDispatched)
            .On<WorkflowInteractiveActionContinuationDispatchedEvent>(ApplyInteractiveActionContinuationDispatched)
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
    }

    private static WorkflowRunState ApplyInteractiveActionHandoffDispatched(
        WorkflowRunState current,
        WorkflowInteractiveActionHandoffDispatchedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.HandoffId) ||
            evt.Request is null ||
            evt.TerminalContinuation is null)
        {
            return current;
        }

        var existing = current.InteractiveActionHandoffs.FirstOrDefault(candidate =>
            string.Equals(candidate.HandoffId, evt.HandoffId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (!existing.Request.ToByteString().Equals(evt.Request.ToByteString()) ||
                !existing.TerminalContinuation.ToByteString().Equals(
                    evt.TerminalContinuation.ToByteString()))
            {
                throw new InvalidOperationException(
                    "An interactive action handoff identity was reused with different content.");
            }

            return current;
        }

        var next = current.Clone();
        next.InteractiveActionHandoffs.Add(new WorkflowInteractiveActionHandoffState
        {
            HandoffId = evt.HandoffId,
            Request = evt.Request.Clone(),
            TerminalContinuation = evt.TerminalContinuation.Clone(),
        });
        while (next.InteractiveActionHandoffs.Count > InteractiveActionHandoffLimit)
            next.InteractiveActionHandoffs.RemoveAt(0);
        return next;
    }

    private static WorkflowRunState ApplyInteractiveActionContinuationDispatched(
        WorkflowRunState current,
        WorkflowInteractiveActionContinuationDispatchedEvent evt)
    {
        var index = -1;
        for (var candidate = 0; candidate < current.InteractiveActionHandoffs.Count; candidate++)
        {
            if (string.Equals(
                    current.InteractiveActionHandoffs[candidate].HandoffId,
                    evt.HandoffId,
                    StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0 ||
            current.InteractiveActionHandoffs[index].ContinuationDispatched)
        {
            return current;
        }

        var next = current.Clone();
        next.InteractiveActionHandoffs[index].ContinuationDispatched = true;
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRoleReplyRecorded(
        WorkflowRunState current,
        WorkflowRoleReplyRecordedEvent evt)
    {
        if (!IsValidArtifactSource(evt.Source) || IsProcessedArtifactSource(current, evt.Source))
            return current;

        var next = current.Clone();
        RecordProcessedArtifactSource(next, evt.Source);
        return next;
    }

    private bool IsMatchingToolCatalogProof(WorkflowRuntimeOperationRecordedEvent operation)
    {
        if (!WorkflowToolCatalogPolicies.IsCurrent(State.ToolCatalogPolicyVersion))
        {
            return string.IsNullOrWhiteSpace(operation.ToolCatalogPolicyVersion) ||
                   string.Equals(
                       operation.ToolCatalogPolicyVersion,
                       State.ToolCatalogPolicyVersion,
                       StringComparison.Ordinal);
        }

        var proof = operation.ToolCatalogProof;
        return string.Equals(
                   operation.ToolCatalogPolicyVersion,
                   State.ToolCatalogPolicyVersion,
                   StringComparison.Ordinal) &&
               proof?.Budget is
               {
                   MaximumToolCount: WorkflowToolCatalogPolicies.MaximumWorkflowToolCount,
                   MaximumSchemaBytes: WorkflowToolCatalogPolicies.MaximumWorkflowSchemaBytes,
               } &&
               proof.SchemaBytes <= WorkflowToolCatalogPolicies.MaximumWorkflowSchemaBytes;
    }

    private static WorkflowRunState ApplyWorkflowRuntimeOperationRecorded(
        WorkflowRunState current,
        WorkflowRuntimeOperationRecordedEvent evt)
    {
        if (!IsValidArtifactSource(evt.Source) || IsProcessedArtifactSource(current, evt.Source))
            return current;

        var next = current.Clone();
        RecordProcessedArtifactSource(next, evt.Source);
        if (evt.Kind == WorkflowRuntimeOperationKind.Model &&
            evt.Phase == WorkflowRuntimeOperationPhase.Started &&
            evt.ToolCatalogProof != null)
        {
            next.LastToolCatalogProof = evt.ToolCatalogProof.Clone();
        }
        return next;
    }

    private static void RecordProcessedArtifactSource(
        WorkflowRunState state,
        WorkflowArtifactSourceIdentity source)
    {
        state.ProcessedArtifactSources.Add(source.Clone());

        var publisherActorId = source.PublisherActorId;
        if (!state.ProcessedArtifactStateVersionsByPublisher.TryGetValue(publisherActorId, out var version) ||
            source.CommittedStateVersion > version)
        {
            state.ProcessedArtifactStateVersionsByPublisher[publisherActorId] = source.CommittedStateVersion;
        }
    }

    private static WorkflowArtifactSourceIdentity? ResolveArtifactSource(IMessage artifactFact) =>
        artifactFact switch
        {
            WorkflowRoleReplyRecordedEvent roleReply => roleReply.Source,
            WorkflowRuntimeOperationRecordedEvent operation => operation.Source,
            _ => null,
        };

    private static string? BuildStepCompletionArtifactKey(StepCompletedEvent? completion)
    {
        // Legacy and composite-module completions without an execution identity cannot be
        // safely deduplicated because the same step can legitimately run more than once.
        if (completion == null ||
            string.IsNullOrWhiteSpace(completion.RunId) ||
            string.IsNullOrWhiteSpace(completion.StepId) ||
            string.IsNullOrWhiteSpace(completion.ExecutionId))
        {
            return null;
        }

        return RuntimeCallbackKeyComposer.BuildKey(
            '|',
            WorkflowRunIdNormalizer.Normalize(completion.RunId),
            completion.StepId.Trim(),
            completion.ExecutionId.Trim());
    }

    private static bool IsProcessedArtifactSource(
        WorkflowRunState state,
        WorkflowArtifactSourceIdentity? source) =>
        IsValidArtifactSource(source) &&
        state.ProcessedArtifactSources.Any(candidate =>
            string.Equals(candidate.PublisherActorId, source!.PublisherActorId, StringComparison.Ordinal) &&
            string.Equals(candidate.CommittedEventId, source.CommittedEventId, StringComparison.Ordinal) &&
            candidate.CommittedStateVersion == source.CommittedStateVersion);

    private static bool IsValidArtifactSource(WorkflowArtifactSourceIdentity? source) =>
        source is
        {
            PublisherActorId.Length: > 0,
            CommittedEventId.Length: > 0,
            CommittedStateVersion: > 0,
        };

    private WorkflowRunState ApplyBindWorkflowRunDefinition(WorkflowRunState current, BindWorkflowRunDefinitionEvent evt)
    {
        if (GetPendingWorkflowCompletion(current) != null)
            return current;

        var decision = EvaluateRunDefinitionBind(current, evt, publisherActorId: null, verifyPublisher: false);
        if (decision.Disposition != RunDefinitionBindDisposition.Accept)
            return current;

        var eventRunId = ResolveRequestedBindRunId(current, evt.RunId);
        var next = current.Clone();
        next.DefinitionActorId = evt.DefinitionActorId?.Trim() ?? string.Empty;
        next.WorkflowYaml = evt.WorkflowYaml ?? string.Empty;
        next.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName)
            ? current.WorkflowName
            : evt.WorkflowName.Trim();
        next.RunId = eventRunId;
        next.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId)
            ? current.ScopeId
            : evt.ScopeId.Trim();
        next.RunOrigin = string.IsNullOrWhiteSpace(evt.RunOrigin)
            ? current.RunOrigin
            : evt.RunOrigin.Trim();
        next.ScheduleId = string.IsNullOrWhiteSpace(evt.ScheduleId)
            ? current.ScheduleId
            : evt.ScheduleId.Trim();
        next.WorkflowId = string.IsNullOrWhiteSpace(evt.WorkflowId)
            ? current.WorkflowId
            : evt.WorkflowId.Trim();
        next.RevisionId = string.IsNullOrWhiteSpace(evt.RevisionId)
            ? current.RevisionId
            : evt.RevisionId.Trim();
        next.DefinitionVersion = evt.DefinitionVersion <= 0
            ? current.DefinitionVersion
            : evt.DefinitionVersion;
        next.CapabilityAdmissionPlan = evt.CapabilityAdmissionPlan?.Clone();
        next.ExpectedExecutionMode = evt.ExpectedExecutionMode;
        next.ReusePolicy = evt.ReusePolicy;
        next.BindingGeneration = evt.BindingGeneration;
        next.ReuseAuthorityActorId = evt.ReuseAuthorityActorId?.Trim() ?? string.Empty;
        next.ToolCatalogPolicyVersion = evt.ToolCatalogPolicyVersion ?? string.Empty;
        next.Status = BoundStatus;
        next.Input = string.Empty;
        next.FinalOutput = string.Empty;
        next.FinalError = string.Empty;
        next.TerminalRecoveryFailureKind = WorkflowRecoveryFailureKind.Unspecified;
        next.TerminalValueLifecycleFailureKind = WorkflowValueLifecycleFailureKind.Unspecified;
        next.CompletedAtUtc = null;
        next.StartedAtUtc = null;
        next.ClearDurationMs();
        next.Initiator = null;
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
        next.PendingCompensationCompletion = null;
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
        next.CurrentTurnId = string.Empty;
        next.InteractiveActionHandoffs.Clear();
        next.ProcessedArtifactSources.Clear();
        next.ProcessedArtifactStateVersionsByPublisher.Clear();
        next.ProcessedStepCompletionKeys.Clear();
        next.Lineage = evt.InitialLineage == null
            ? CreateUnavailableLineage("Run lineage is unavailable for this run.")
            : EnsureLineage(evt.InitialLineage);
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

        var compileResult = EvaluateWorkflowCompilation(
            next.WorkflowYaml,
            next.ToolCatalogPolicyVersion);
        next.Compiled = compileResult.Compiled;
        next.CompilationError = compileResult.CompilationError;
        return next;
    }

    private static WorkflowRunState ApplyDefinitionBindingContinuationRegistered(
        WorkflowRunState current,
        WorkflowRunDefinitionBindingContinuationRegisteredEvent evt)
    {
        if (GetPendingWorkflowCompletion(current) != null ||
            evt.Continuation == null ||
            string.IsNullOrWhiteSpace(evt.Continuation.ContinuationId))
        {
            return current;
        }

        var next = current.Clone();
        next.PendingDefinitionBindingContinuation = evt.Continuation.Clone();
        return next;
    }

    private static WorkflowRunState ApplyDefinitionBindingCleanupCompleted(
        WorkflowRunState current,
        WorkflowRunDefinitionBindingCleanupCompletedEvent evt)
    {
        if (current.PendingDefinitionBindingContinuation == null ||
            !string.Equals(
                current.PendingDefinitionBindingContinuation.ContinuationId,
                evt.ContinuationId?.Trim(),
                StringComparison.Ordinal))
        {
            return current;
        }

        var next = current.Clone();
        next.PendingDefinitionBindingContinuation.CleanupCompleted = true;
        return next;
    }

    private static WorkflowRunState ApplyDefinitionBindingContinuationCleared(
        WorkflowRunState current,
        WorkflowRunDefinitionBindingContinuationClearedEvent evt)
    {
        if (current.PendingDefinitionBindingContinuation == null ||
            !string.Equals(
                current.PendingDefinitionBindingContinuation.ContinuationId,
                evt.ContinuationId?.Trim(),
                StringComparison.Ordinal))
        {
            return current;
        }

        var next = current.Clone();
        next.PendingDefinitionBindingContinuation = null;
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
        // A run actor owns one run. A start observed after that run reaches a
        // terminal state is stale/redelivered and must not reopen it.
        if (IsTerminalStatus(current.Status))
            return current;

        var next = current.Clone();
        next.RunId = string.IsNullOrWhiteSpace(evt.RunId) ? current.RunId : WorkflowRunIdNormalizer.Normalize(evt.RunId);
        next.WorkflowName = string.IsNullOrWhiteSpace(evt.WorkflowName) ? current.WorkflowName : evt.WorkflowName.Trim();
        next.Input = evt.Input ?? string.Empty;
        next.Status = RunningStatus;
        next.FinalOutput = string.Empty;
        next.FinalError = string.Empty;
        next.TerminalRecoveryFailureKind = WorkflowRecoveryFailureKind.Unspecified;
        next.TerminalValueLifecycleFailureKind = WorkflowValueLifecycleFailureKind.Unspecified;
        next.CompletedAtUtc = null;
        next.ClearDurationMs();
        next.Initiator = BuildInitiator(
            evt.ExecutionContextDelta?.CallerCredential?.NyxIdAuthority,
            current.RunOrigin);
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
        next.PendingCompensationCompletion = null;
        next.PendingStartWorkflow = evt.PendingStartWorkflow?.Clone();
        next.CompletionNotificationTarget = evt.CompletionNotificationTarget?.Clone();
        next.LastCommandId = string.IsNullOrWhiteSpace(evt.WorkflowCommandId)
            ? current.LastCommandId
            : evt.WorkflowCommandId.Trim();
        next.WorkflowCorrelationId = evt.WorkflowCorrelationId?.Trim() ?? string.Empty;
        next.PendingTerminalNotification = null;
        next.TerminalNotificationAttempt = 0;
        next.TerminalNotificationDeliveryStatus = WorkflowRunTerminalNotificationDeliveryStatus.Unspecified;
        next.TerminalNotificationRetryCallbackId = string.Empty;
        next.CurrentTurnId = evt.CurrentTurnId?.Trim() ?? string.Empty;
        next.Lineage = evt.Lineage?.Clone() ?? current.Lineage?.Clone() ?? CreateUnavailableLineage("Run lineage is unavailable for this run.");
        next.InteractiveActionHandoffs.Clear();
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

    [EventHandler]
    public async Task HandleWorkflowRunLineageRecorded(WorkflowRunLineageRecordedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var sourceRunId = WorkflowRunIdNormalizer.Normalize(evt.SourceRunId);
        var currentRunId = WorkflowRunIdNormalizer.Normalize(RunId);
        if (string.IsNullOrWhiteSpace(sourceRunId) ||
            string.IsNullOrWhiteSpace(currentRunId) ||
            !string.Equals(sourceRunId, currentRunId, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "Reject workflow lineage record for mismatched source run. actor={ActorId} currentRun={CurrentRunId} sourceRun={SourceRunId}",
                Id,
                currentRunId,
                sourceRunId);
            return;
        }

        if (evt.RelationKind != WorkflowRunLineageRelationKind.RetryFork ||
            string.IsNullOrWhiteSpace(evt.ChildRunId))
        {
            Logger.LogWarning(
                "Reject workflow lineage record with unsupported or missing relation. sourceRun={SourceRunId} childRun={ChildRunId} relation={RelationKind}",
                sourceRunId,
                evt.ChildRunId,
                evt.RelationKind);
            return;
        }

        // Implement (issue #3252):
        //   Behavior: source/original runs expose retried or forked child run identities from committed facts.
        //   Why this shape: the source actor records its own lineage instead of queries deriving children from actor IDs or graph topology.
        await PersistDomainEventAsync(new WorkflowRunLineageRecordedEvent
        {
            SourceRunId = sourceRunId,
            ChildRunId = WorkflowRunIdNormalizer.Normalize(evt.ChildRunId),
            ChildActorId = evt.ChildActorId?.Trim() ?? string.Empty,
            StartAtStepId = evt.StartAtStepId?.Trim() ?? string.Empty,
            Attempt = Math.Max(0, evt.Attempt),
            RelationKind = WorkflowRunLineageRelationKind.RetryFork,
            OriginalRunId = WorkflowRunIdNormalizer.Normalize(evt.OriginalRunId),
        });
    }

    private static WorkflowRunState ApplyWorkflowRunLineageRecorded(
        WorkflowRunState current,
        WorkflowRunLineageRecordedEvent evt)
    {
        if (evt.RelationKind != WorkflowRunLineageRelationKind.RetryFork)
            return current;

        var childRunId = WorkflowRunIdNormalizer.Normalize(evt.ChildRunId);
        if (string.IsNullOrWhiteSpace(childRunId))
            return current;

        var next = current.Clone();
        next.Lineage = EnsureLineage(next.Lineage);
        MarkLineageAvailable(next.Lineage);
        next.Lineage.RetryFork ??= new WorkflowRunRetryForkLineage();
        next.Lineage.RetryFork.Availability = WorkflowRunLineageAvailability.Available;
        if (string.IsNullOrWhiteSpace(next.Lineage.RetryFork.SourceRunId))
            next.Lineage.RetryFork.SourceRunId = WorkflowRunIdNormalizer.Normalize(evt.SourceRunId);
        if (string.IsNullOrWhiteSpace(next.Lineage.RetryFork.OriginalRunId))
        {
            var originalRunId = WorkflowRunIdNormalizer.Normalize(evt.OriginalRunId);
            next.Lineage.RetryFork.OriginalRunId = string.IsNullOrWhiteSpace(originalRunId)
                ? next.Lineage.RetryFork.SourceRunId
                : originalRunId;
        }

        UpsertLineageChild(
            next.Lineage.RetryFork.ChildRuns,
            childRunId,
            evt.ChildActorId,
            relationshipId: string.Empty,
            stepId: evt.StartAtStepId,
            Math.Max(0, evt.Attempt),
            WorkflowRunLineageRelationKind.RetryFork);
        return next;
    }

    internal static WorkflowRunLineage BuildExecutionStartLineage(
        WorkflowRunForkSeed? forkSeed,
        WorkflowRunLineage? currentLineage,
        string runId)
    {
        if (forkSeed == null || string.IsNullOrWhiteSpace(forkSeed.SourceRunId))
            return currentLineage?.Clone() ?? CreateUnavailableLineage("Run lineage is unavailable for this run.");

        var sourceRunId = WorkflowRunIdNormalizer.Normalize(forkSeed.SourceRunId);
        var originalRunId = WorkflowRunIdNormalizer.Normalize(forkSeed.OriginalRunId);
        if (string.IsNullOrWhiteSpace(originalRunId))
            originalRunId = sourceRunId;

        // Implement (issue #3252):
        //   Behavior: retried and forked child runs carry source and original run IDs as typed lineage.
        //   Why this shape: fork lineage is stamped from the accepted fork seed instead of inferred from routes or run ID strings.
        return new WorkflowRunLineage
        {
            Availability = WorkflowRunLineageAvailability.Available,
            RetryFork = new WorkflowRunRetryForkLineage
            {
                Availability = WorkflowRunLineageAvailability.Available,
                SourceRunId = sourceRunId,
                OriginalRunId = originalRunId,
                Attempt = Math.Max(0, forkSeed.Attempt),
                StartAtStepId = forkSeed.StartAtStepId?.Trim() ?? string.Empty,
            },
            SubWorkflow = currentLineage?.SubWorkflow?.Clone() ?? new WorkflowRunSubWorkflowLineage
            {
                Availability = WorkflowRunLineageAvailability.Unavailable,
            },
        };
    }

    internal static void MarkLineageAvailable(WorkflowRunLineage lineage)
    {
        lineage.Availability = WorkflowRunLineageAvailability.Available;
        lineage.UnavailableReason = string.Empty;
    }

    internal static WorkflowRunLineage CreateUnavailableLineage(string reason) =>
        new()
        {
            Availability = WorkflowRunLineageAvailability.Unavailable,
            UnavailableReason = string.IsNullOrWhiteSpace(reason)
                ? "Run lineage is unavailable for this run."
                : reason,
            RetryFork = new WorkflowRunRetryForkLineage
            {
                Availability = WorkflowRunLineageAvailability.Unavailable,
            },
            SubWorkflow = new WorkflowRunSubWorkflowLineage
            {
                Availability = WorkflowRunLineageAvailability.Unavailable,
            },
        };

    internal static WorkflowRunLineage EnsureLineage(WorkflowRunLineage? lineage)
    {
        var next = lineage?.Clone() ?? CreateUnavailableLineage("Run lineage is unavailable for this run.");
        next.RetryFork ??= new WorkflowRunRetryForkLineage
        {
            Availability = WorkflowRunLineageAvailability.Unavailable,
        };
        next.SubWorkflow ??= new WorkflowRunSubWorkflowLineage
        {
            Availability = WorkflowRunLineageAvailability.Unavailable,
        };
        if (next.Availability == WorkflowRunLineageAvailability.Available)
            next.UnavailableReason = string.Empty;
        return next;
    }

    internal static void UpsertLineageChild(
        RepeatedField<WorkflowRunLineageRunRef> childRuns,
        string runId,
        string? actorId,
        string relationshipId,
        string? stepId,
        int attempt,
        WorkflowRunLineageRelationKind relationKind)
    {
        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        if (string.IsNullOrWhiteSpace(normalizedRunId))
            return;

        var normalizedActorId = actorId?.Trim() ?? string.Empty;
        for (var i = 0; i < childRuns.Count; i++)
        {
            if (!string.Equals(childRuns[i].RunId, normalizedRunId, StringComparison.Ordinal) ||
                childRuns[i].RelationKind != relationKind)
            {
                continue;
            }

            childRuns[i] = new WorkflowRunLineageRunRef
            {
                RunId = normalizedRunId,
                ActorId = string.IsNullOrWhiteSpace(normalizedActorId) ? childRuns[i].ActorId ?? string.Empty : normalizedActorId,
                RelationshipId = string.IsNullOrWhiteSpace(relationshipId) ? childRuns[i].RelationshipId : relationshipId,
                StepId = string.IsNullOrWhiteSpace(stepId) ? childRuns[i].StepId : stepId.Trim(),
                Attempt = Math.Max(0, attempt),
                RelationKind = relationKind,
            };
            return;
        }

        childRuns.Add(new WorkflowRunLineageRunRef
        {
            RunId = normalizedRunId,
            ActorId = normalizedActorId,
            RelationshipId = relationshipId ?? string.Empty,
            StepId = stepId?.Trim() ?? string.Empty,
            Attempt = Math.Max(0, attempt),
            RelationKind = relationKind,
        });
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
            if (delta.ClearUnattendedEffectAuthorization)
                merged.ClearUnattendedEffectAuthorization = true;
            if (delta.Llm != null)
                merged.Llm = delta.Llm.Clone();
            if (delta.CallerCredential != null)
                merged.CallerCredential = delta.CallerCredential.Clone();
            if (delta.UnattendedEffectAuthorization != null)
                merged.UnattendedEffectAuthorization = delta.UnattendedEffectAuthorization.Clone();
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
        if (delta.ClearUnattendedEffectAuthorization)
            state.UnattendedEffectAuthorization = null;
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
                DurableCredentialCleanupResponsibility =
                    delta.CallerCredential.DurableCredentialCleanupResponsibility,
            };
        }

        if (delta.UnattendedEffectAuthorization != null)
            state.UnattendedEffectAuthorization = delta.UnattendedEffectAuthorization.Clone();

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
        if (string.Equals(scopeKey, WorkflowExecutionKernel.ModuleStateKey, StringComparison.Ordinal))
            next.PendingStartWorkflow = null;
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

        var completionKey = BuildStepCompletionArtifactKey(evt);
        if (completionKey != null)
            next.ProcessedStepCompletionKeys[completionKey] = true;

        // Normalized completions are admitted by the execution kernel against
        // the exact dispatch lease. Only the follow-up typed outcome event may
        // mutate the compensation ledger; the raw completion is not authority.
        if (HasNormalizedExecutionValues(current))
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
            CapturedOutputValueId = evt.CapturedOutputValueId ?? string.Empty,
            CapturedOutputProducerStepId = evt.CapturedOutputProducerStepId ?? string.Empty,
            CapturedOutputProducerExecutionId = evt.CapturedOutputProducerExecutionId ?? string.Empty,
            CapturedOutputSourceKind = evt.CapturedOutputSourceKind,
            LedgerStatus = CompensableLedgerEntryStatus.Provisional,
        });
        return next;
    }

    private static WorkflowRunState ApplyCompensableStepOutputCaptured(
        WorkflowRunState current,
        CompensableStepOutputCapturedEvent evt)
    {
        var next = current.Clone();
        var stepId = evt.StepId?.Trim() ?? string.Empty;
        var compensationStepId = evt.CompensationStepId?.Trim() ?? string.Empty;
        var idempotencyKey = evt.IdempotencyKey ?? string.Empty;
        if (stepId.Length == 0 || compensationStepId.Length == 0)
            return next;

        if (!evt.Success)
        {
            if (NormalizeFailureOutcome(evt.FailureOutcome) == WorkflowStepFailureOutcome.CalleeConfirmed)
                RemoveMatchingProvisionalLedgerEntries(next, stepId, compensationStepId, idempotencyKey);
            return next;
        }

        var valueId = evt.CapturedOutputValueId?.Trim() ?? string.Empty;
        if (valueId.Length == 0 ||
            evt.CapturedOutputSourceKind == WorkflowCanonicalValueSourceKind.Unspecified)
        {
            throw new InvalidOperationException(
                $"Normalized compensable step '{stepId}' has no exact canonical output identity.");
        }

        var matching = next.CompensableLedger.FirstOrDefault(entry =>
            string.Equals(entry.StepId, stepId, StringComparison.Ordinal) &&
            string.Equals(entry.CompensationStepId, compensationStepId, StringComparison.Ordinal) &&
            string.Equals(entry.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
        if (matching == null)
        {
            matching = new CompletedStepLedgerEntry
            {
                StepId = stepId,
                CompensationStepId = compensationStepId,
                IdempotencyKey = idempotencyKey,
            };
            next.CompensableLedger.Add(matching);
        }
        else if (matching.LedgerStatus == CompensableLedgerEntryStatus.Confirmed &&
                 (!string.Equals(matching.CapturedOutputValueId, valueId, StringComparison.Ordinal) ||
                  !string.Equals(matching.CapturedOutputProducerStepId, evt.CapturedOutputProducerStepId, StringComparison.Ordinal) ||
                  !string.Equals(matching.CapturedOutputProducerExecutionId, evt.CapturedOutputProducerExecutionId, StringComparison.Ordinal) ||
                  matching.CapturedOutputSourceKind != evt.CapturedOutputSourceKind))
        {
            throw new InvalidOperationException(
                $"Compensable step '{stepId}' canonical output identity changed after confirmation.");
        }

        matching.CapturedOutput = string.Empty;
        matching.CapturedOutputValueId = valueId;
        matching.CapturedOutputProducerStepId = evt.CapturedOutputProducerStepId ?? string.Empty;
        matching.CapturedOutputProducerExecutionId = evt.CapturedOutputProducerExecutionId ?? string.Empty;
        matching.CapturedOutputSourceKind = evt.CapturedOutputSourceKind;
        matching.LedgerStatus = CompensableLedgerEntryStatus.Confirmed;
        return next;
    }

    private static WorkflowRunState ApplyCompensationRequest(WorkflowRunState current, CompensationRequestEvent evt)
    {
        var next = current.Clone();
        var compensationStepId = evt.CompensationStepId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(compensationStepId))
            return next;

        next.PendingCompensationCompletion = null;
        next.SagaStatus = WorkflowSagaStatus.Compensating;
        next.CompensationExecutionId = evt.ExecutionId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.FailedStepId))
            next.CompensationOriginFailedStepId = evt.FailedStepId.Trim();
        for (var i = next.CompensableLedger.Count - 1; i >= 0; i--)
        {
            var ledgerEntry = next.CompensableLedger[i];
            var canonicalIdentityMatches = !string.IsNullOrWhiteSpace(evt.CapturedOutputValueId)
                ? string.Equals(ledgerEntry.CapturedOutputValueId, evt.CapturedOutputValueId, StringComparison.Ordinal) &&
                  string.Equals(ledgerEntry.CapturedOutputProducerStepId, evt.CapturedOutputProducerStepId, StringComparison.Ordinal) &&
                  string.Equals(ledgerEntry.CapturedOutputProducerExecutionId, evt.CapturedOutputProducerExecutionId, StringComparison.Ordinal) &&
                  ledgerEntry.CapturedOutputSourceKind == evt.CapturedOutputSourceKind
                : string.IsNullOrWhiteSpace(ledgerEntry.CapturedOutputValueId) &&
                  string.Equals(ledgerEntry.CapturedOutput ?? string.Empty, evt.CapturedOutput ?? string.Empty, StringComparison.Ordinal);
            if (string.Equals(ledgerEntry.CompensationStepId, compensationStepId, StringComparison.Ordinal) &&
                string.Equals(ledgerEntry.IdempotencyKey ?? string.Empty, evt.IdempotencyKey ?? string.Empty, StringComparison.Ordinal) &&
                canonicalIdentityMatches)
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

        next.PendingCompensationCompletion = evt.Clone();
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
        next.TerminalRecoveryFailureKind = WorkflowRecoveryFailureKind.Unspecified;
        next.TerminalValueLifecycleFailureKind = WorkflowValueLifecycleFailureKind.Unspecified;
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
        if (IsTerminalStatus(current.Status))
            return current;

        var next = current.Clone();
        next.Status = StoppedStatus;
        next.FinalOutput = string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Reason))
            next.FinalError = evt.Reason;
        next.TerminalRecoveryFailureKind = WorkflowRecoveryFailureKind.Unspecified;
        next.TerminalValueLifecycleFailureKind = WorkflowValueLifecycleFailureKind.Unspecified;
        ApplyTerminalTiming(next, evt.CompletedAtUtc);
        ClearExecutionStatesPreservingToolCallCleanup(next);
        next.ExecutionContext = new WorkflowRunExecutionContextState();
        next.PendingStartWorkflow = null;
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        next.PendingSubWorkflowInvocations.Clear();
        next.PendingSubWorkflowInvocationIndexByChildRunId.Clear();
        next.PendingChildRunIdsByParentRunId.Clear();
        return next;
    }

    private static WorkflowRunState ApplyWorkflowCompleted(WorkflowRunState current, WorkflowCompletedEvent evt)
    {
        if (ShouldIgnoreWorkflowCompleted(current))
            return current;

        var next = current.Clone();
        next.Status = evt.Success ? CompletedStatus : FailedStatus;
        next.FinalOutput = evt.Output ?? string.Empty;
        next.FinalError = evt.Error ?? string.Empty;
        next.TerminalRecoveryFailureKind = evt.Success
            ? WorkflowRecoveryFailureKind.Unspecified
            : evt.RecoveryFailureKind;
        next.TerminalValueLifecycleFailureKind = evt.Success
            ? WorkflowValueLifecycleFailureKind.Unspecified
            : evt.ValueLifecycleFailureKind;
        ApplyTerminalTiming(next, evt.CompletedAtUtc);
        next.TerminalWorkflowCompletionRecorded = true;
        next.PendingCompensationCompletion = null;
        next.ExecutionContext = new WorkflowRunExecutionContextState();
        next.PendingStartWorkflow = null;
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        NormalizeTerminalWorkflowExecutionState(next);
        return next;
    }

    private static WorkflowRunState ApplyWorkflowRunTerminalTimingRecorded(
        WorkflowRunState current,
        WorkflowRunTerminalTimingRecordedEvent evt)
    {
        if (current.CompletedAtUtc != null)
            return current;

        var next = current.Clone();
        ApplyTerminalTiming(next, evt.CompletedAtUtc);
        return next;
    }

    private static void NormalizeTerminalWorkflowExecutionState(WorkflowRunState state)
    {
        if (!state.ExecutionStates.TryGetValue(WorkflowExecutionKernel.ModuleStateKey, out var packed) ||
            !packed.Is(WorkflowExecutionKernelState.Descriptor))
        {
            return;
        }

        var kernelState = packed.Unpack<WorkflowExecutionKernelState>();
        if (WorkflowExecutionKernel.NormalizeTerminalState(kernelState))
        {
            state.ExecutionStates.Remove(WorkflowExecutionKernel.ModuleStateKey);
            return;
        }

        state.ExecutionStates[WorkflowExecutionKernel.ModuleStateKey] = Any.Pack(kernelState);
    }

    private static WorkflowRunState ApplyWorkflowRunStopped(WorkflowRunState current, WorkflowRunStoppedEvent evt)
    {
        if (IsTerminalStatus(current.Status))
            return current;

        var next = current.Clone();
        next.Status = StoppedStatus;
        next.FinalOutput = string.Empty;
        if (!string.IsNullOrWhiteSpace(evt.Reason))
            next.FinalError = evt.Reason;
        ApplyTerminalTiming(next, evt.CompletedAtUtc);
        ClearExecutionStatesPreservingToolCallCleanup(next);
        next.ExecutionContext = new WorkflowRunExecutionContextState();
        next.PendingStartWorkflow = null;
        next.PendingSubWorkflowDefinitionResolutions.Clear();
        next.PendingSubWorkflowDefinitionResolutionIndexByInvocationId.Clear();
        next.PendingSubWorkflowInvocations.Clear();
        next.PendingSubWorkflowInvocationIndexByChildRunId.Clear();
        next.PendingChildRunIdsByParentRunId.Clear();
        return next;
    }

    private static void ClearExecutionStatesPreservingToolCallCleanup(WorkflowRunState state)
    {
        Any? toolCallState = null;
        if (state.ExecutionStates.TryGetValue(ToolCallModule.ModuleStateKey, out var packed) &&
            packed.Is(ToolCallModuleState.Descriptor))
        {
            toolCallState = packed.Clone();
        }

        state.ExecutionStates.Clear();
        if (toolCallState != null)
            state.ExecutionStates[ToolCallModule.ModuleStateKey] = toolCallState;
    }

    private static void ApplyTerminalTiming(WorkflowRunState state, Timestamp? completedAtUtc)
    {
        if (completedAtUtc == null)
            return;

        state.CompletedAtUtc = completedAtUtc.Clone();
        // Fix (review round 1, F1):
        //   Missing startedAt was encoded as duration 0 for terminal Activity rows.
        //   Preserve unavailable duration by only setting the optional field when both timestamps exist.
        if (state.StartedAtUtc == null)
        {
            state.ClearDurationMs();
            return;
        }

        state.DurationMs = Math.Max(
            0,
            (completedAtUtc.ToDateTimeOffset() - state.StartedAtUtc.ToDateTimeOffset()).TotalMilliseconds);
    }

    private static WorkflowRunInitiatorState BuildInitiator(
        WorkflowCallerNyxIdAuthority? authority,
        string? runOrigin)
    {
        if (string.Equals(runOrigin, WorkflowRunOrigins.Webhook, StringComparison.Ordinal))
        {
            return new WorkflowRunInitiatorState
            {
                Platform = "webhook",
                DisplayValue = "Webhook",
                Availability = "available",
            };
        }

        if (authority == null)
        {
            return new WorkflowRunInitiatorState
            {
                Availability = "unavailable",
                DisplayValue = "Unknown",
            };
        }

        var platform = authority.Platform?.Trim() ?? string.Empty;
        var tenant = authority.Tenant?.Trim() ?? string.Empty;
        var externalUserId = authority.ExternalUserId?.Trim() ?? string.Empty;
        var scope = authority.Scope?.Trim() ?? string.Empty;
        var displayValue = ResolveInitiatorDisplayValue(platform, tenant, externalUserId, scope);
        return new WorkflowRunInitiatorState
        {
            Platform = platform,
            Tenant = tenant,
            ExternalUserId = externalUserId,
            Scope = scope,
            // Binding ids are credential-capable opaque handles. They remain
            // actor-private for JIT token exchange and never enter activity
            // or Observatory initiator projections.
            BindingId = string.Empty,
            DisplayValue = string.IsNullOrWhiteSpace(displayValue) ? "Unknown" : displayValue,
            Availability = string.IsNullOrWhiteSpace(displayValue) ? "unavailable" : "available",
        };
    }

    private static string ResolveInitiatorDisplayValue(
        string platform,
        string tenant,
        string externalUserId,
        string scope)
    {
        if (!string.IsNullOrWhiteSpace(platform) && !string.IsNullOrWhiteSpace(externalUserId))
            return string.IsNullOrWhiteSpace(tenant)
                ? $"{platform}:{externalUserId}"
                : $"{platform}:{tenant}:{externalUserId}";
        if (!string.IsNullOrWhiteSpace(scope))
            return $"scope:{scope}";
        return string.Empty;
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

    private static WorkflowRunState KeepCurrentState(
        WorkflowRunState current,
        WorkflowToolCallTerminalCleanupRetryFiredEvent _) => current;

    private static bool IsTerminalStatus(string? status) =>
        string.Equals(status, CompletedStatus, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, FailedStatus, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, StoppedStatus, StringComparison.OrdinalIgnoreCase);

    private static bool HasPendingToolCallState(WorkflowRunState state)
    {
        if (!state.ExecutionStates.TryGetValue(ToolCallModule.ModuleStateKey, out var packed) ||
            !packed.Is(ToolCallModuleState.Descriptor))
        {
            return false;
        }

        var toolState = packed.Unpack<ToolCallModuleState>();
        return toolState.PendingApprovals.Count > 0 ||
               toolState.PendingExecutions.Count > 0 ||
               toolState.PendingOperations.Count > 0 ||
               toolState.Completions.Count > 0 ||
               toolState.StopCancellation != null;
    }

    private string ResolveRequestedBindRunId(WorkflowRunState state, string? requestedRunId)
    {
        if (!string.IsNullOrWhiteSpace(requestedRunId))
            return WorkflowRunIdNormalizer.Normalize(requestedRunId);

        return string.IsNullOrWhiteSpace(state.RunId)
            ? WorkflowRunIdNormalizer.Normalize(Id)
            : WorkflowRunIdNormalizer.Normalize(state.RunId);
    }

    private RunDefinitionBindDecision EvaluateRunDefinitionBind(
        WorkflowRunState current,
        BindWorkflowRunDefinitionEvent requested,
        string? publisherActorId,
        bool verifyPublisher)
    {
        if (!System.Enum.IsDefined(requested.ReusePolicy))
            return RejectRunDefinitionBind("workflow Run reuse policy is invalid.");

        var requestedRunId = ResolveRequestedBindRunId(current, requested.RunId);
        var requestedAuthority = requested.ReuseAuthorityActorId?.Trim() ?? string.Empty;
        var requestedPolicy = requested.ReusePolicy == WorkflowRunActorReusePolicy.Unspecified
            ? WorkflowRunActorReusePolicy.SingleRun
            : requested.ReusePolicy;
        var requestedIsSingleton = requestedPolicy == WorkflowRunActorReusePolicy.SerialSingleton;
        if (requestedIsSingleton)
        {
            if (requested.BindingGeneration <= 0 || string.IsNullOrWhiteSpace(requestedAuthority))
            {
                return RejectRunDefinitionBind(
                    "workflow_call singleton bind requires a positive generation and reuse authority.");
            }
        }
        else if (requested.BindingGeneration != 0 || !string.IsNullOrWhiteSpace(requestedAuthority))
        {
            return RejectRunDefinitionBind(
                "single-run workflow bind cannot declare a reuse generation or authority.");
        }

        var currentIsSingleton = current.ReusePolicy == WorkflowRunActorReusePolicy.SerialSingleton;
        var expectedPublisher = requestedIsSingleton
            ? requestedAuthority
            : currentIsSingleton
                ? current.ReuseAuthorityActorId?.Trim() ?? string.Empty
                : string.Empty;
        if (verifyPublisher &&
            !string.IsNullOrWhiteSpace(expectedPublisher) &&
            !string.Equals(expectedPublisher, publisherActorId?.Trim(), StringComparison.Ordinal))
        {
            return RejectRunDefinitionBind("workflow Run reuse authority does not match the bind publisher.");
        }

        if (string.IsNullOrWhiteSpace(current.RunId))
        {
            if (requestedIsSingleton && requested.BindingGeneration != 1)
                return RejectRunDefinitionBind("workflow_call singleton initial binding generation must be 1.");
            return AcceptRunDefinitionBind();
        }

        if (!IsValidCommittedReuseBinding(current))
            return RejectRunDefinitionBind("workflow Run has an invalid committed reuse binding.");

        if (!verifyPublisher &&
            current.ReusePolicy == WorkflowRunActorReusePolicy.Unspecified &&
            requested.ReusePolicy == WorkflowRunActorReusePolicy.Unspecified)
        {
            // Historical streams predate actor reuse policy and may contain
            // multiple binds on one actor. Replay their original semantics;
            // live commands are normalized to SingleRun before persistence.
            return AcceptRunDefinitionBind();
        }

        var currentRunId = WorkflowRunIdNormalizer.Normalize(current.RunId);
        var sameRun = string.Equals(currentRunId, requestedRunId, StringComparison.Ordinal);
        if (!currentIsSingleton)
        {
            if (requestedIsSingleton)
                return RejectRunDefinitionBind("single-run workflow actor cannot be upgraded to singleton reuse.");
            if (!sameRun)
            {
                if (IsTerminalStatus(current.Status))
                    return IgnoreRunDefinitionBind();
                return RejectRunDefinitionBind(
                    $"workflow Run '{Id}' is already bound to a different run identity.");
            }
            if (IsTerminalStatus(current.Status))
                return IgnoreRunDefinitionBind();
            if (verifyPublisher)
            {
                return SameCanonicalRunBinding(current, requested)
                    ? IgnoreRunDefinitionBind()
                    : RejectRunDefinitionBind(
                        "workflow Run is already bound to a different definition or identity; binding payload cannot change within a binding generation.");
            }

            // Internal dynamic_workflow replacement commits its authoritative
            // bind event directly. Historical replay must continue to apply it;
            // externally handled binds already passed the canonical equality gate.
            return IsTerminalStatus(current.Status)
                ? IgnoreRunDefinitionBind()
                : AcceptRunDefinitionBind();
        }

        var currentAuthority = current.ReuseAuthorityActorId?.Trim() ?? string.Empty;
        if (!requestedIsSingleton)
        {
            // A pre-generation bind from the same parent can arrive after a rolling
            // deployment. It cannot mutate a generation-fenced actor.
            return requested.BindingGeneration == 0
                ? IgnoreRunDefinitionBind()
                : RejectRunDefinitionBind("workflow_call singleton reuse policy cannot change.");
        }
        if (!string.Equals(currentAuthority, requestedAuthority, StringComparison.Ordinal))
            return RejectRunDefinitionBind("workflow Run reuse authority cannot change.");

        if (requested.BindingGeneration < current.BindingGeneration ||
            (requested.BindingGeneration == current.BindingGeneration && !sameRun))
        {
            return IgnoreRunDefinitionBind();
        }
        if (requested.BindingGeneration == current.BindingGeneration)
        {
            if (IsTerminalStatus(current.Status))
                return IgnoreRunDefinitionBind();
            if (verifyPublisher)
            {
                return SameCanonicalRunBinding(current, requested)
                    ? IgnoreRunDefinitionBind()
                    : RejectRunDefinitionBind(
                        "workflow Run is already bound to a different definition or identity; binding payload cannot change within a binding generation.");
            }

            return IsTerminalStatus(current.Status)
                ? IgnoreRunDefinitionBind()
                : AcceptRunDefinitionBind();
        }
        if (requested.BindingGeneration != current.BindingGeneration + 1)
            return RejectRunDefinitionBind("workflow_call singleton binding generation must advance by exactly one.");
        if (sameRun)
            return RejectRunDefinitionBind("workflow_call singleton next generation requires a new run identity.");
        if (!IsTerminalStatus(current.Status))
            return RejectRunDefinitionBind("workflow_call singleton cannot advance while the current run is active.");
        if (!SameDefinitionAuthority(current, requested))
            return RejectRunDefinitionBind("workflow Run definition authority cannot change across singleton generations.");

        return AcceptRunDefinitionBind();
    }

    private static bool SameDefinitionAuthority(
        WorkflowRunState current,
        BindWorkflowRunDefinitionEvent requested)
    {
        var currentDefinitionActorId = current.DefinitionActorId?.Trim() ?? string.Empty;
        var requestedDefinitionActorId = requested.DefinitionActorId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentDefinitionActorId) ||
            !string.IsNullOrWhiteSpace(requestedDefinitionActorId))
        {
            return string.Equals(currentDefinitionActorId, requestedDefinitionActorId, StringComparison.Ordinal);
        }

        return string.Equals(
            WorkflowRunIdNormalizer.NormalizeWorkflowName(current.WorkflowName),
            WorkflowRunIdNormalizer.NormalizeWorkflowName(requested.WorkflowName),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool SameCanonicalRunBinding(
        WorkflowRunState current,
        BindWorkflowRunDefinitionEvent requested)
    {
        var requestedPolicy = requested.ReusePolicy == WorkflowRunActorReusePolicy.Unspecified
            ? WorkflowRunActorReusePolicy.SingleRun
            : requested.ReusePolicy;
        var expectedLineage = requested.InitialLineage == null
            ? CreateUnavailableLineage("Run lineage is unavailable for this run.")
            : EnsureLineage(requested.InitialLineage);

        return string.Equals(
                   WorkflowRunIdNormalizer.Normalize(current.RunId),
                   ResolveRequestedBindRunId(current, requested.RunId),
                   StringComparison.Ordinal) &&
               string.Equals(
                   current.DefinitionActorId?.Trim(),
                   requested.DefinitionActorId?.Trim(),
                   StringComparison.Ordinal) &&
               string.Equals(
                   WorkflowRunIdNormalizer.NormalizeWorkflowName(current.WorkflowName),
                   WorkflowRunIdNormalizer.NormalizeWorkflowName(requested.WorkflowName),
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(current.WorkflowYaml, requested.WorkflowYaml, StringComparison.Ordinal) &&
               InlineWorkflowYamlsEqual(current.InlineWorkflowYamls, requested.InlineWorkflowYamls) &&
               string.Equals(current.ScopeId?.Trim(), requested.ScopeId?.Trim(), StringComparison.Ordinal) &&
               string.Equals(current.RunOrigin?.Trim(), requested.RunOrigin?.Trim(), StringComparison.Ordinal) &&
               string.Equals(current.ScheduleId?.Trim(), requested.ScheduleId?.Trim(), StringComparison.Ordinal) &&
               string.Equals(current.WorkflowId?.Trim(), requested.WorkflowId?.Trim(), StringComparison.Ordinal) &&
               string.Equals(current.RevisionId?.Trim(), requested.RevisionId?.Trim(), StringComparison.Ordinal) &&
               string.Equals(
                   current.ToolCatalogPolicyVersion?.Trim(),
                   requested.ToolCatalogPolicyVersion?.Trim(),
                   StringComparison.Ordinal) &&
               Math.Max(0, current.DefinitionVersion) == Math.Max(0, requested.DefinitionVersion) &&
               current.ExpectedExecutionMode == requested.ExpectedExecutionMode &&
               NormalizeLiveReusePolicy(current.ReusePolicy) == requestedPolicy &&
               current.BindingGeneration == requested.BindingGeneration &&
               string.Equals(
                   current.ReuseAuthorityActorId?.Trim(),
                   requested.ReuseAuthorityActorId?.Trim(),
                   StringComparison.Ordinal) &&
               Equals(current.CapabilityAdmissionPlan, requested.CapabilityAdmissionPlan) &&
               Equals(EnsureLineage(current.Lineage), expectedLineage);
    }

    private static bool IsValidCommittedReuseBinding(WorkflowRunState state) =>
        state.ReusePolicy switch
        {
            WorkflowRunActorReusePolicy.Unspecified or WorkflowRunActorReusePolicy.SingleRun =>
                state.BindingGeneration == 0 && string.IsNullOrWhiteSpace(state.ReuseAuthorityActorId),
            WorkflowRunActorReusePolicy.SerialSingleton =>
                state.BindingGeneration > 0 && !string.IsNullOrWhiteSpace(state.ReuseAuthorityActorId),
            _ => false,
        };

    private static RunDefinitionBindDecision AcceptRunDefinitionBind() =>
        new(RunDefinitionBindDisposition.Accept, string.Empty);

    private static RunDefinitionBindDecision IgnoreRunDefinitionBind() =>
        new(RunDefinitionBindDisposition.Ignore, string.Empty);

    private static RunDefinitionBindDecision RejectRunDefinitionBind(string error) =>
        new(RunDefinitionBindDisposition.Reject, error);

    private enum RunDefinitionBindDisposition
    {
        Accept,
        Ignore,
        Reject,
    }

    private readonly record struct RunDefinitionBindDecision(
        RunDefinitionBindDisposition Disposition,
        string Error);

    private static bool IsMismatchedRunIdentity(WorkflowRunState state, string? eventRunId)
    {
        if (string.IsNullOrWhiteSpace(state.RunId) || string.IsNullOrWhiteSpace(eventRunId))
            return false;

        return !string.Equals(
            WorkflowRunIdNormalizer.Normalize(state.RunId),
            WorkflowRunIdNormalizer.Normalize(eventRunId),
            StringComparison.Ordinal);
    }

    private static string? ResolveRunOwnedTransitionRunId(IMessage evt)
    {
        if (StateTransitionMatcher.TryExtract<WorkflowRunCompletionNotificationTargetAdoptedEvent>(evt, out var target))
            return target.WorkflowRunId;
        if (StateTransitionMatcher.TryExtract<WorkflowRunExecutionStartedEvent>(evt, out var started))
            return started.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowRunExecutionContextUpdatedEvent>(evt, out var contextUpdated))
            return contextUpdated.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowRunExecutionContextClearedEvent>(evt, out var contextCleared))
            return contextCleared.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowRunLineageRecordedEvent>(evt, out var lineage))
            return lineage.SourceRunId;
        if (StateTransitionMatcher.TryExtract<CompensableStepDispatchedEvent>(evt, out var compensable))
            return compensable.RunId;
        if (StateTransitionMatcher.TryExtract<CompensableStepOutputCapturedEvent>(evt, out var compensableOutput))
            return compensableOutput.RunId;
        if (StateTransitionMatcher.TryExtract<StepRequestEvent>(evt, out var stepRequest))
            return stepRequest.RunId;
        if (StateTransitionMatcher.TryExtract<StepCompletedEvent>(evt, out var stepCompleted))
            return stepCompleted.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowSuspendedEvent>(evt, out var suspended))
            return suspended.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowToolApprovalResumeRejectedEvent>(evt, out var resumeRejected))
            return resumeRejected.RunId;
        if (StateTransitionMatcher.TryExtract<WaitingForSignalEvent>(evt, out var waitingForSignal))
            return waitingForSignal.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowSignalBufferedEvent>(evt, out var signalBuffered))
            return signalBuffered.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowExternalApprovalContinuationRegisteredEvent>(evt, out var approvalRegistered))
            return approvalRegistered.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowExternalApprovalContinuationClearedEvent>(evt, out var approvalCleared))
            return approvalCleared.RunId;
        if (StateTransitionMatcher.TryExtract<CompensationRequestEvent>(evt, out var compensationRequested))
            return compensationRequested.RunId;
        if (StateTransitionMatcher.TryExtract<CompensationStepCompletedEvent>(evt, out var compensationStepCompleted))
            return compensationStepCompleted.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowCompensationCompletedEvent>(evt, out var compensationCompleted))
            return compensationCompleted.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowCompensationFailedEvent>(evt, out var compensationFailed))
            return compensationFailed.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowCompensationRetryRequestedEvent>(evt, out var compensationRetry))
            return compensationRetry.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowStoppedEvent>(evt, out var stopped))
            return stopped.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowCompletedEvent>(evt, out var completed))
            return completed.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowRunTerminalTimingRecordedEvent>(evt, out var terminalTiming))
            return terminalTiming.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowRunStoppedEvent>(evt, out var runStopped))
            return runStopped.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowRunTerminalNotificationPreparedEvent>(evt, out var notificationPrepared))
            return notificationPrepared.Notification?.WorkflowRunId;
        if (StateTransitionMatcher.TryExtract<WorkflowRoleReplyRecordedEvent>(evt, out var roleReply))
            return roleReply.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowRuntimeOperationRecordedEvent>(evt, out var runtimeOperation))
            return runtimeOperation.RunId;
        if (StateTransitionMatcher.TryExtract<WorkflowInteractiveActionHandoffDispatchedEvent>(evt, out var handoff))
            return handoff.TerminalContinuation?.RunId;
        if (StateTransitionMatcher.TryExtract<SubWorkflowDefinitionResolutionRegisteredEvent>(evt, out var resolution))
            return resolution.ParentRunId;
        if (StateTransitionMatcher.TryExtract<SubWorkflowInvocationRegisteredEvent>(evt, out var invocation))
            return invocation.ParentRunId;
        return null;
    }

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

    private static bool HasToolApprovalIdentity(WorkflowSuspendedEvent suspended) =>
        !string.IsNullOrWhiteSpace(suspended.StepId) &&
        suspended.ToolApproval != null &&
        !string.IsNullOrWhiteSpace(suspended.ToolApproval.ExecutionId) &&
        !string.IsNullOrWhiteSpace(suspended.ToolApproval.ToolName) &&
        !string.IsNullOrWhiteSpace(suspended.ToolApproval.ToolCallId) &&
        !string.IsNullOrWhiteSpace(suspended.ToolApproval.ApprovalRequestId);

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

    private static EventEnvelopePublishOptions BuildToolApprovalNotificationDispatchOptions(
        WorkflowRunToolApprovalNotification notification) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId(
                    ToolApprovalNotificationDispatchOperationPrefix,
                    notification.DeliveryId,
                    notification.WorkflowCommandId,
                    notification.ApprovalRequestId),
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
        if (HasPendingWorkflowCompensationOutcome(State) ||
            !IsCompensating(State) ||
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
            CapturedOutputValueId = entry.CapturedOutputValueId,
            CapturedOutputProducerStepId = entry.CapturedOutputProducerStepId,
            CapturedOutputProducerExecutionId = entry.CapturedOutputProducerExecutionId,
            CapturedOutputSourceKind = entry.CapturedOutputSourceKind,
            ExecutionId = State.CompensationExecutionId ?? string.Empty,
        }, TopologyAudience.Self, ct);
    }

    private static bool HasPendingWorkflowCompensationOutcome(WorkflowRunState state)
    {
        if (!state.ExecutionStates.TryGetValue(
                WorkflowExecutionKernel.ModuleStateKey,
                out var packed) ||
            packed?.Is(WorkflowExecutionKernelState.Descriptor) != true)
        {
            return false;
        }

        return packed.Unpack<WorkflowExecutionKernelState>()
                   .PendingCompensationOutcome?.Completion != null;
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
            entry.CapturedOutputValueId ?? string.Empty,
            entry.CapturedOutputProducerStepId ?? string.Empty,
            entry.CapturedOutputProducerExecutionId ?? string.Empty,
            entry.CapturedOutputSourceKind,
            executionId ?? string.Empty);

    private static WorkflowCompensationTransitionResult EmptyCompensationResult(
        WorkflowCompensationTransitionStatus status) =>
        new(
            status,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            WorkflowCanonicalValueSourceKind.Unspecified,
            string.Empty);

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

    private static bool HasNormalizedExecutionValues(WorkflowRunState state)
    {
        foreach (var packedState in state.ExecutionStates.Values)
        {
            if (packedState?.Is(WorkflowExecutionKernelState.Descriptor) == true &&
                packedState.Unpack<WorkflowExecutionKernelState>().NormalizedValues != null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetNormalizedKernelState(
        WorkflowRunState state,
        out WorkflowExecutionKernelState kernelState)
    {
        foreach (var packedState in state.ExecutionStates.Values)
        {
            if (packedState?.Is(WorkflowExecutionKernelState.Descriptor) != true)
                continue;

            var candidate = packedState.Unpack<WorkflowExecutionKernelState>();
            if (candidate.NormalizedValues == null)
                continue;

            kernelState = candidate;
            return true;
        }

        kernelState = new WorkflowExecutionKernelState();
        return false;
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
            var variableView = WorkflowExecutionValueStore.CreateVariableView(kernelState);
            if (!variableView.TryGetValue("workflow_call.invocation_id", out var invocationId))
                return string.Empty;

            return invocationId.Trim();
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
        DisableExecutionModules();
        await TryRevokeScheduledCallerCredentialAsync(
            stateBeforeStop,
            "workflow-run-stopped",
            CancellationToken.None);
        await _subWorkflowOrchestrator.CancelPendingDefinitionResolutionTimeoutsAsync(stateBeforeStop, CancellationToken.None);
        await _subWorkflowOrchestrator.CleanupPendingInvocationsForRunAsync(runId, stateBeforeStop, CancellationToken.None);
        await CleanupRoleAgentTreeAsync(CancellationToken.None);
        _runtimeContext.Clear();

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
        await CleanupTerminalToolCallStateAsync(CancellationToken.None);
    }

    private async Task RecoverDuplicateTerminalToolCallCleanupAsync(CancellationToken ct)
    {
        DisableExecutionModules();
        await EnsureTerminalNotificationAsync(ct);
        await CleanupTerminalToolCallStateAsync(
            ct,
            allowImmediateFallback: false);
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
            stateBeforeTerminal.ExecutionContext!.CallerCredential!
                .DurableCredentialCleanupResponsibility ==
                WorkflowCallerCredentialCleanupResponsibility.Borrowed ||
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

    private WorkflowCompilationResult EvaluateWorkflowCompilation(
        string yaml,
        string? toolCatalogPolicyVersion = null)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return WorkflowCompilationResult.Invalid("workflow yaml is empty");

        try
        {
            var workflow = _parser.Parse(yaml);
            var errors = ValidateWorkflowDefinition(
                workflow,
                WorkflowToolCatalogPolicies.IsCurrent(
                    toolCatalogPolicyVersion ?? State.ToolCatalogPolicyVersion));
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
        string executionInput,
        CancellationToken ct = default)
    {
        if (State.PendingDefinitionBindingContinuation != null)
        {
            return WorkflowCompilationResult.Invalid(
                "Workflow Run cannot replace its definition while definition binding cleanup is pending.");
        }

        if (HasPendingToolCallState(State))
        {
            return WorkflowCompilationResult.Invalid(
                "Workflow Run cannot replace its definition while actor-local tool call cleanup is pending.");
        }

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

        var validationErrors = ValidateWorkflowDefinition(
            parsed,
            WorkflowToolCatalogPolicies.IsCurrent(State.ToolCatalogPolicyVersion));
        if (validationErrors.Count > 0)
            return WorkflowCompilationResult.Invalid(string.Join("; ", validationErrors));
        if (WorkflowValueLifecyclePolicy.HasDeclarations(parsed))
        {
            return WorkflowCompilationResult.Invalid(
                "Dynamic workflow definition replacement cannot opt into value_lifecycle.");
        }

        // Select the new logical run representation before binding cleanup,
        // actor-tree mutation, execution-start persistence, or start dispatch.
        var valueRepresentation = await SelectWorkflowRunRepresentationAsync(
            forkSeed: null,
            ct);

        var workflowName = parsed.Name ?? string.Empty;
        var bind = new BindWorkflowRunDefinitionEvent
        {
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            WorkflowName = workflowName,
            WorkflowYaml = workflowYaml,
            RunId = string.IsNullOrWhiteSpace(State.RunId) ? Id : State.RunId,
            ScopeId = State.ScopeId ?? string.Empty,
            RunOrigin = State.RunOrigin ?? string.Empty,
            ScheduleId = State.ScheduleId ?? string.Empty,
            WorkflowId = State.WorkflowId ?? string.Empty,
            RevisionId = State.RevisionId ?? string.Empty,
            DefinitionVersion = Math.Max(0, State.DefinitionVersion),
            CapabilityAdmissionPlan = State.CapabilityAdmissionPlan?.Clone(),
            ExpectedExecutionMode = State.ExpectedExecutionMode,
            InitialLineage = State.Lineage?.Clone(),
            InlineWorkflowYamls = { State.InlineWorkflowYamls },
            ReusePolicy = State.ReusePolicy,
            BindingGeneration = State.BindingGeneration,
            ReuseAuthorityActorId = State.ReuseAuthorityActorId ?? string.Empty,
            ToolCatalogPolicyVersion = State.ToolCatalogPolicyVersion ?? string.Empty,
        };
        var continuation = BuildDefinitionBindingContinuation(
            executeAfterBind: true,
            executionInput,
            valueRepresentation);
        await PersistDomainEventsAsync(
            [
                bind,
                new WorkflowRunDefinitionBindingContinuationRegisteredEvent
                {
                    Continuation = continuation,
                },
            ],
            ct);
        await DrainPendingDefinitionBindingContinuationAsync(ct);
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

    private WorkflowRunDefinitionBindingContinuationState BuildDefinitionBindingContinuation(
        bool executeAfterBind,
        string executionInput,
        WorkflowExecutionValueRepresentation valueRepresentation =
            WorkflowExecutionValueRepresentation.Unspecified)
    {
        var continuation = new WorkflowRunDefinitionBindingContinuationState
        {
            ContinuationId = Guid.NewGuid().ToString("N"),
            ExecuteAfterBind = executeAfterBind,
            ExecutionInput = executionInput ?? string.Empty,
            ValueRepresentation = valueRepresentation,
        };
        continuation.DefinitionResolutionTimeoutLeases.Add(
            State.PendingSubWorkflowDefinitionResolutions
                .Where(static pending => pending.TimeoutLease != null)
                .Select(static pending => pending.TimeoutLease.Clone()));
        continuation.DerivedChildActorIds.Add(
            CaptureDerivedChildActorIdsForReset()
                .Where(static actorId => !string.IsNullOrWhiteSpace(actorId))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static actorId => actorId, StringComparer.Ordinal));
        return continuation;
    }

    private async Task<bool> DrainPendingDefinitionBindingContinuationAsync(CancellationToken ct)
    {
        var continuation = State.PendingDefinitionBindingContinuation?.Clone();
        if (continuation == null || string.IsNullOrWhiteSpace(continuation.ContinuationId))
            return false;

        if (!continuation.CleanupCompleted)
        {
            CancelWorkflowExecutionBackgroundWork();
            foreach (var leaseState in continuation.DefinitionResolutionTimeoutLeases)
            {
                await WorkflowRuntimeCallbackLeaseSupport.TryCancelAsync(
                    CancelDurableCallbackAsync,
                    Logger,
                    WorkflowRuntimeCallbackLeaseStateCodec.ToRuntime(leaseState),
                    "workflow definition binding resolution timeout cleanup",
                    ct);
            }

            await ResetDerivedRuntimeStateAsync(continuation.DerivedChildActorIds, ct);
            await PersistDomainEventAsync(new WorkflowRunDefinitionBindingCleanupCompletedEvent
            {
                ContinuationId = continuation.ContinuationId,
            }, ct);
            continuation = State.PendingDefinitionBindingContinuation?.Clone();
            if (continuation == null)
                return false;
        }

        RebuildCompiledWorkflowCache();
        InstallCognitiveModules();
        var startDispatched = false;
        if (continuation.ExecuteAfterBind)
            startDispatched = await DrainDynamicDefinitionStartAsync(continuation, ct);

        await PersistDomainEventAsync(new WorkflowRunDefinitionBindingContinuationClearedEvent
        {
            ContinuationId = continuation.ContinuationId,
        }, ct);
        return startDispatched;
    }

    private async Task<bool> DrainDynamicDefinitionStartAsync(
        WorkflowRunDefinitionBindingContinuationState continuation,
        CancellationToken ct)
    {
        if (_compiledWorkflow == null)
            throw new InvalidOperationException("Dynamic workflow definition is not compiled.");

        if (!string.Equals(State.Status, BoundStatus, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(State.Status, RunningStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await EnsureAgentTreeAsync();
        var runId = string.IsNullOrWhiteSpace(State.RunId)
            ? WorkflowRunIdNormalizer.Normalize(Id)
            : WorkflowRunIdNormalizer.Normalize(State.RunId);
        var start = State.PendingStartWorkflow?.Clone() ?? new StartWorkflowEvent
        {
            WorkflowName = _compiledWorkflow.Name,
            Input = continuation.ExecutionInput,
            RunId = runId,
            BindingGeneration = State.BindingGeneration,
            ValueRepresentation = NormalizePersistedValueRepresentation(
                continuation.ValueRepresentation),
        };
        if (string.Equals(State.Status, BoundStatus, StringComparison.OrdinalIgnoreCase))
        {
            var replacementExecutionContext =
                WorkflowRunExecutionContextStateAccess.ClearWorkflowRuntimeDelta();
            replacementExecutionContext.ClearCallerCredential = true;
            replacementExecutionContext.ClearUnattendedEffectAuthorization = true;
            await PersistDomainEventAsync(new WorkflowRunExecutionStartedEvent
            {
                RunId = runId,
                WorkflowName = _compiledWorkflow.Name,
                Input = continuation.ExecutionInput,
                DefinitionActorId = State.DefinitionActorId ?? string.Empty,
                ScopeId = State.ScopeId ?? string.Empty,
                ExecutionContextDelta = replacementExecutionContext,
                Attempt = State.ForkAttempt,
                StartedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                CurrentTurnId = State.CurrentTurnId,
                PendingStartWorkflow = start.Clone(),
            }, ct);
        }

        if (!string.Equals(State.Status, RunningStatus, StringComparison.OrdinalIgnoreCase))
            return false;

        await PublishStartWorkflowOrTerminalFailureAsync(
            start,
            sessionId: string.Empty,
            ct);
        return true;
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

    private List<string> ValidateWorkflowDefinition(
        WorkflowDefinition workflow,
        bool requireCurrentToolCatalogPolicy = false) =>
        WorkflowRunDefinitionValidationSupport.Validate(
            workflow,
            _knownModuleStepTypes,
            _stepExecutorFactory,
            requireCurrentToolCatalogPolicy);

    private static string NormalizeNewToolCatalogPolicyVersion(string? policyVersion)
    {
        var normalized = string.IsNullOrWhiteSpace(policyVersion)
            ? WorkflowToolCatalogPolicies.LegacyV0
            : policyVersion.Trim();
        if (!WorkflowToolCatalogPolicies.IsCurrent(normalized) &&
            !string.Equals(normalized, WorkflowToolCatalogPolicies.LegacyV0, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Workflow tool catalog policy version is unsupported.");
        }
        return normalized;
    }
}
