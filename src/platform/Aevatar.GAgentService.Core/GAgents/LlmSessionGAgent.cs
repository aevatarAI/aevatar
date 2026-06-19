using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Core.GAgents;

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
// Refactor (iter355/issue1438-first): Old pattern: durable LlmSession tool runtime contracts persisted arguments, schemas, hints, and results as *_json strings New principle: typed Struct/Value fields are authoritative for new writes; legacy *_json fields are read fallback only
[GAgent("gagent.service.llm-session")]
public sealed class LlmSessionGAgent : GAgentBase<LlmSessionState>
{
    private static readonly Duration DefaultTtl = Duration.FromTimeSpan(TimeSpan.FromHours(24));
    private const int RunningStatus = 1;
    private const int CompletedStatus = 2;
    private const int FailedStatus = 3;
    private const int CancelledStatus = 4;

    public LlmSessionGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleRegisterAsync(RegisterResponseSessionRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Record);

        var record = NormalizeRecord(command.Record.Clone());
        ValidateRecord(record);

        var existing = State.Record;
        if (existing != null && !string.IsNullOrWhiteSpace(existing.ResponseId))
        {
            EnsureExistingMatches(existing, record);
            return;
        }

        await PersistDomainEventAsync(new LlmSessionRegisteredEvent
        {
            Record = record,
        });
        await ScheduleTtlExpiryAsync(record);
    }

    [EventHandler]
    public async Task HandleUpdateStatusAsync(UpdateResponseSessionStatusRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = State.Record;
        if (existing == null || string.IsNullOrWhiteSpace(existing.ResponseId))
        {
            throw new InvalidOperationException(
                $"Response session actor '{Id}' has no registered response; status update rejected.");
        }

        var responseId = NormalizeRequired(command.ResponseId);
        if (!string.Equals(existing.ResponseId, responseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Response session actor '{Id}' is bound to response '{existing.ResponseId}' and cannot update response '{responseId}'.");
        }

        if (command.Status == LlmSessionStatus.Unspecified)
            return;

        if (existing.Status == command.Status)
            return;

        if (IsTerminal(existing.Status))
        {
            // Terminal states are authoritative and final — a late status update must NOT
            // overwrite them (e.g. a streaming-observation timeout marking Failed after the run
            // already Completed on a long turn, or a late Completed after /cancel). Treat it as an
            // idempotent no-op rather than throwing: this is an actor event handler, so throwing
            // only burns the runtime retry budget and logs noise ("is Completed and cannot
            // transition to Failed") without changing the outcome.
            Logger.LogDebug(
                "Ignoring status update to {RequestedStatus} for response session {ResponseId}: already terminal ({TerminalStatus}).",
                command.Status,
                existing.ResponseId,
                existing.Status);
            return;
        }

        await PersistDomainEventAsync(new LlmSessionStatusUpdatedEvent
        {
            ResponseId = existing.ResponseId,
            Status = command.Status,
            UpdatedAt = command.UpdatedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    // Refactor (iter75/cluster-075-responses-agui-host-completion-state):
    //   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
    //   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
    [EventHandler]
    public async Task HandleRecordCompletionAsync(RecordResponseSessionCompletionRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Completion);

        var existing = EnsureRegisteredSession(command.ResponseId);
        var completion = NormalizeCompletion(command.Completion.Clone());
        ValidateCompletion(completion);

        if (State.Completion is { CompletedAt: not null } current)
        {
            EnsureExistingCompletionMatches(current, completion);
            return;
        }

        if (IsTerminal(existing.Status) &&
            existing.Status is not (LlmSessionStatus.Completed or LlmSessionStatus.Failed))
        {
            throw new InvalidOperationException(
                $"Response session '{existing.ResponseId}' is {existing.Status} and cannot record completion.");
        }

        await PersistDomainEventAsync(new LlmSessionCompletionRecordedEvent
        {
            ResponseId = existing.ResponseId,
            Completion = completion,
        });
    }

    [EventHandler]
    public async Task HandleExpireResponseSessionAsync(ExpireResponseSessionRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = EnsureRegisteredSession(command.ResponseId);
        if (IsTerminal(existing.Status))
            return;

        var observedAt = command.ObservedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        var expiresAt = ResolveExpiry(existing);
        if (expiresAt > observedAt)
        {
            await ScheduleTtlExpiryAsync(existing, observedAt);
            return;
        }

        await PersistDomainEventAsync(new LlmSessionStatusUpdatedEvent
        {
            ResponseId = existing.ResponseId,
            Status = LlmSessionStatus.Expired,
            UpdatedAt = Timestamp.FromDateTimeOffset(observedAt),
        });
    }

    [EventHandler]
    public async Task HandleRecordForwardedToolCallAsync(RecordForwardedToolCallRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Call);

        var existing = EnsureRegisteredSession(command.ResponseId);
        if (IsTerminal(existing.Status))
        {
            throw new InvalidOperationException(
                $"Response session '{existing.ResponseId}' is {existing.Status} and cannot record new forwarded tool calls.");
        }

        var call = NormalizeToolCall(command.Call.Clone());
        ValidateToolCall(call);

        var existingCall = State.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, call.CallId, StringComparison.Ordinal));
        if (existingCall != null)
        {
            EnsureExistingToolCallMatches(existingCall, call);
            return;
        }

        await PersistDomainEventAsync(new LlmSessionForwardedToolCallEmittedEvent
        {
            ResponseId = existing.ResponseId,
            Call = call,
        });
    }

    [EventHandler]
    public async Task HandleReceiveForwardedToolResultAsync(ReceiveForwardedToolResultRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = EnsureRegisteredSession(command.ResponseId);
        var callId = NormalizeRequired(command.CallId);
        var schemaHash = NormalizeRequired(command.SchemaHash);
        if (string.IsNullOrWhiteSpace(callId))
            throw new InvalidOperationException("call_id is required.");
        if (string.IsNullOrWhiteSpace(schemaHash))
            throw new InvalidOperationException("schema_hash is required.");

        var existingCall = State.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, callId, StringComparison.Ordinal));
        if (existingCall == null)
        {
            throw new InvalidOperationException(
                $"Response session '{existing.ResponseId}' has no forwarded tool call '{callId}'.");
        }

        if (!string.Equals(existingCall.SchemaHash, schemaHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{callId}' schema hash mismatch.");
        }

        if (existingCall.Status is LlmSessionForwardedToolCallStatus.Received
            or LlmSessionForwardedToolCallStatus.Resolved)
        {
            return;
        }

        if (existingCall.Status is LlmSessionForwardedToolCallStatus.Cancelled
            or LlmSessionForwardedToolCallStatus.Expired)
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{callId}' is {existingCall.Status} and cannot receive a result.");
        }

        await PersistDomainEventAsync(new LlmSessionForwardedToolResultReceivedEvent
        {
            ResponseId = existing.ResponseId,
            CallId = callId,
            SchemaHash = schemaHash,
            Result = command.Result?.Clone(),
            ReceivedAt = command.ReceivedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleResolveForwardedToolResultAsync(ResolveForwardedToolResultRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = EnsureRegisteredSession(command.ResponseId);
        var callId = NormalizeRequired(command.CallId);
        var existingCall = State.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, callId, StringComparison.Ordinal));
        if (existingCall == null)
        {
            throw new InvalidOperationException(
                $"Response session '{existing.ResponseId}' has no forwarded tool call '{callId}'.");
        }

        if (existingCall.Status == LlmSessionForwardedToolCallStatus.Resolved)
            return;

        if (existingCall.Status != LlmSessionForwardedToolCallStatus.Received)
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{callId}' is {existingCall.Status} and cannot be resolved.");
        }

        await PersistDomainEventAsync(new LlmSessionForwardedToolCallResolvedEvent
        {
            ResponseId = existing.ResponseId,
            CallId = callId,
            ResolvedAt = command.ResolvedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleLlmRunRequestedAsync(LlmRunRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = EnsureRegisteredSession(command.ResponseId);
        if (IsTerminal(existing.Status))
            return;

        var runId = NormalizeRequired(command.RunId);
        if (string.IsNullOrWhiteSpace(runId))
            runId = $"{existing.ResponseId}:run";

        if (State.ActiveRun is { Status: RunningStatus } active)
        {
            if (string.Equals(active.RunId, runId, StringComparison.Ordinal))
                return;

            throw new InvalidOperationException(
                $"Response session '{existing.ResponseId}' already has active run '{active.RunId}'.");
        }

        if (State.Completion is { CompletedAt: not null })
            return;

        var startedAt = command.RequestedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        if (State.ActiveRun == null ||
            !string.Equals(State.ActiveRun.RunId, runId, StringComparison.Ordinal) ||
            State.ActiveRun.LastAppliedSequence <= 0)
        {
            await PersistDomainEventAsync(new LlmRunStartedEvent
            {
                ResponseId = existing.ResponseId,
                RunId = runId,
                Sequence = 1,
                StartedAt = startedAt,
            });
        }

        var runCore = Services.GetRequiredService<ILlmRunCore>();
        var request = new LlmRunCoreRequest(command.Clone(), runId, existing.OriginKind.ToString());
        if (Services.GetService<IActorDispatchPort>() is not { } dispatchPort)
        {
            await runCore.RunAsync(
                request,
                new InActorLlmRunSink(this),
                CancellationToken.None);
            return;
        }

        var initialSequence = State.ActiveRun?.LastAppliedSequence ?? 1;
        _ = Task.Run(
            () => ConsumeRunOffActorTurnAsync(
                runCore,
                request,
                new SelfDispatchingLlmRunSink(Id, dispatchPort, initialSequence),
                Logger),
            CancellationToken.None);
    }

    [EventHandler]
    public Task HandleLlmStreamChunkObservedAsync(LlmStreamChunkObserved observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        var accepted = CanPersistRunFact(observed.ResponseId, observed.RunId, observed.Sequence, terminal: false);
        return accepted
            ? PersistStreamChunkObservedAsync(observed.Clone(), CancellationToken.None)
            : Task.CompletedTask;
    }

    [EventHandler]
    public Task HandleLlmToolCallObservedAsync(LlmToolCallObserved observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        var accepted = CanPersistRunFact(observed.ResponseId, observed.RunId, observed.Sequence, terminal: false);
        return accepted
            ? PersistToolCallObservedAsync(observed.Clone(), CancellationToken.None)
            : Task.CompletedTask;
    }

    [EventHandler]
    public Task HandleLlmRunCompletedAsync(LlmRunCompleted completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var accepted = CanPersistRunFact(completed.ResponseId, completed.RunId, completed.Sequence, terminal: true);
        return accepted
            ? PersistRunCompletedAsync(completed.Clone(), CancellationToken.None)
            : Task.CompletedTask;
    }

    [EventHandler]
    public Task HandleLlmRunFailedAsync(LlmRunFailed failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        var accepted = CanPersistRunFact(failed.ResponseId, failed.RunId, failed.Sequence, terminal: true);
        return accepted
            ? PersistRunFailedAsync(failed.Clone(), CancellationToken.None)
            : Task.CompletedTask;
    }

    [EventHandler]
    public Task HandleLlmRunCancelledAsync(LlmRunCancelled cancelled)
    {
        ArgumentNullException.ThrowIfNull(cancelled);
        var accepted = CanPersistRunFact(cancelled.ResponseId, cancelled.RunId, cancelled.Sequence, terminal: true);
        return accepted
            ? PersistRunCancelledAsync(cancelled.Clone(), CancellationToken.None)
            : Task.CompletedTask;
    }

    [EventHandler]
    public async Task HandleLlmSessionForwardedToolCallEmittedAsync(LlmSessionForwardedToolCallEmittedEvent emitted)
    {
        ArgumentNullException.ThrowIfNull(emitted);
        ArgumentNullException.ThrowIfNull(emitted.Call);

        var existing = EnsureRegisteredSession(emitted.ResponseId);
        if (IsTerminal(existing.Status))
            return;

        var call = NormalizeToolCall(emitted.Call.Clone());
        ValidateToolCall(call);
        var existingCall = State.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, call.CallId, StringComparison.Ordinal));
        if (existingCall != null)
        {
            EnsureExistingToolCallMatches(existingCall, call);
            return;
        }

        await PersistDomainEventAsync(new LlmSessionForwardedToolCallEmittedEvent
        {
            ResponseId = existing.ResponseId,
            Call = call,
        });
    }

    private static async Task ConsumeRunOffActorTurnAsync(
        ILlmRunCore runCore,
        LlmRunCoreRequest request,
        ILlmRunSink sink,
        ILogger logger)
    {
        try
        {
            await runCore.RunAsync(request, sink, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Off-turn LLM run consumption failed for response {ResponseId} run {RunId}.",
                request.Command.ResponseId,
                request.RunId);
        }
    }

    protected override LlmSessionState TransitionState(LlmSessionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<LlmSessionRegisteredEvent>(ApplyRegistered)
            .On<LlmSessionStatusUpdatedEvent>(ApplyStatusUpdated)
            .On<LlmSessionCompletionRecordedEvent>(ApplyCompletionRecorded)
            .On<LlmSessionForwardedToolCallEmittedEvent>(ApplyForwardedToolCallEmitted)
            .On<LlmSessionForwardedToolResultReceivedEvent>(ApplyForwardedToolResultReceived)
            .On<LlmSessionForwardedToolCallResolvedEvent>(ApplyForwardedToolCallResolved)
            .On<LlmRunStartedEvent>(ApplyRunStarted)
            .On<LlmStreamChunkObserved>(ApplyStreamChunkObserved)
            .On<LlmToolCallObserved>(ApplyToolCallObserved)
            .On<LlmRunCompleted>(ApplyRunCompleted)
            .On<LlmRunFailed>(ApplyRunFailed)
            .On<LlmRunCancelled>(ApplyRunCancelled)
            .OrCurrent();

    private static LlmSessionState ApplyRegistered(
        LlmSessionState state,
        LlmSessionRegisteredEvent evt)
    {
        var next = state.Clone();
        next.Record = evt.Record?.Clone() ?? new LlmSessionRecord();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.ResponseId}:registered";
        return next;
    }

    private static LlmSessionState ApplyCompletionRecorded(
        LlmSessionState state,
        LlmSessionCompletionRecordedEvent evt)
    {
        var next = state.Clone();
        if (next.Record == null)
            next.Record = new LlmSessionRecord();

        next.Completion = evt.Completion?.Clone() ?? new LlmSessionCompletion();
        next.Record.Status = string.IsNullOrWhiteSpace(next.Completion.FailureCode)
            ? LlmSessionStatus.Completed
            : LlmSessionStatus.Failed;
        next.Record.UpdatedAt = next.Completion.CompletedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:completion";
        return next;
    }

    private static LlmSessionState ApplyStatusUpdated(
        LlmSessionState state,
        LlmSessionStatusUpdatedEvent evt)
    {
        var next = state.Clone();
        if (next.Record == null)
            next.Record = new LlmSessionRecord();

        next.Record.Status = evt.Status;
        next.Record.UpdatedAt = evt.UpdatedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        if (evt.Status == LlmSessionStatus.Cancelled)
        {
            next.Record.CancelledAt = next.Record.UpdatedAt.Clone();
            MarkOpenToolCalls(next, LlmSessionForwardedToolCallStatus.Cancelled);
        }
        else if (evt.Status == LlmSessionStatus.Expired)
        {
            MarkOpenToolCalls(next, LlmSessionForwardedToolCallStatus.Expired);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.ResponseId}:status:{(int)evt.Status}";
        return next;
    }

    private static LlmSessionState ApplyForwardedToolCallEmitted(
        LlmSessionState state,
        LlmSessionForwardedToolCallEmittedEvent evt)
    {
        var next = state.Clone();
        if (evt.Call != null)
        {
            var existing = next.ForwardedToolCalls
                .FirstOrDefault(x => string.Equals(x.CallId, evt.Call.CallId, StringComparison.Ordinal));
            if (existing != null)
            {
                EnsureExistingToolCallMatches(existing, evt.Call);
                return state;
            }

            next.ForwardedToolCalls.Add(evt.Call.Clone());
        }
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:tool:{evt.Call?.CallId}:emitted";
        return next;
    }

    private static LlmSessionState ApplyForwardedToolResultReceived(
        LlmSessionState state,
        LlmSessionForwardedToolResultReceivedEvent evt)
    {
        var next = state.Clone();
        var call = next.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, evt.CallId, StringComparison.Ordinal));
        if (call != null)
        {
            call.Status = LlmSessionForwardedToolCallStatus.Received;
            call.Result = evt.Result?.Clone();
            call.ReceivedAt = evt.ReceivedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:tool:{evt.CallId}:received";
        return next;
    }

    private static LlmSessionState ApplyForwardedToolCallResolved(
        LlmSessionState state,
        LlmSessionForwardedToolCallResolvedEvent evt)
    {
        var next = state.Clone();
        var call = next.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, evt.CallId, StringComparison.Ordinal));
        if (call != null)
        {
            call.Status = LlmSessionForwardedToolCallStatus.Resolved;
            call.ResolvedAt = evt.ResolvedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:tool:{evt.CallId}:resolved";
        return next;
    }

    private static LlmSessionState ApplyRunStarted(
        LlmSessionState state,
        LlmRunStartedEvent evt)
    {
        var next = state.Clone();
        var startedAt = evt.StartedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        var run = EnsureRun(next, evt.RunId, evt.ResponseId, startedAt);
        if (!TryAcceptRunSequence(run, evt.Sequence))
            return state;

        run.Status = RunningStatus;
        run.StartedAt = startedAt.Clone();
        TouchRecord(next, startedAt);
        Bump(next, $"{evt.ResponseId}:run:{evt.RunId}:started");
        return next;
    }

    private static LlmSessionState ApplyStreamChunkObserved(
        LlmSessionState state,
        LlmStreamChunkObserved evt)
    {
        var next = state.Clone();
        var run = EnsureRun(next, evt.RunId, evt.ResponseId, evt.ObservedAt);
        if (!TryAcceptRunSequence(run, evt.Sequence))
            return state;

        run.Round = Math.Max(run.Round, evt.Round);
        if (!string.IsNullOrEmpty(evt.DeltaText))
            run.OutputText += evt.DeltaText;
        if (evt.Usage is not null)
            run.Usage = evt.Usage.Clone();
        TouchRecord(next, evt.ObservedAt);
        Bump(next, $"{evt.ResponseId}:run:{evt.RunId}:chunk");
        return next;
    }

    private static LlmSessionState ApplyToolCallObserved(
        LlmSessionState state,
        LlmToolCallObserved evt)
    {
        var next = state.Clone();
        var run = EnsureRun(next, evt.RunId, evt.ResponseId, evt.ObservedAt);
        if (!TryAcceptRunSequence(run, evt.Sequence))
            return state;

        run.Round = Math.Max(run.Round, evt.Round);
        if (evt.ToolCall != null)
            UpsertRuntimeToolCall(run, evt.ToolCall);
        TouchRecord(next, evt.ObservedAt);
        Bump(next, $"{evt.ResponseId}:run:{evt.RunId}:tool:{evt.ToolCall?.CallId}");
        return next;
    }

    private static LlmSessionState ApplyRunCompleted(
        LlmSessionState state,
        LlmRunCompleted evt)
    {
        var next = state.Clone();
        var completedAt = evt.CompletedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        var run = EnsureRun(next, evt.RunId, evt.ResponseId, completedAt);
        if (!CanAcceptRunTerminalSequence(run, evt.Sequence))
            return state;

        run.Status = CompletedStatus;
        run.OutputText = evt.OutputText ?? string.Empty;
        run.CompletedAt = completedAt.Clone();
        if (evt.Usage is not null)
            run.Usage = evt.Usage.Clone();
        run.ObservedToolCalls.Clear();
        run.ObservedToolCalls.AddRange(evt.ForwardedToolCalls.Select(static call => call.Clone()));

        if (next.Record == null)
            next.Record = new LlmSessionRecord();
        next.Record.Status = LlmSessionStatus.Completed;
        next.Record.UpdatedAt = completedAt.Clone();
        next.Completion = new LlmSessionCompletion
        {
            OutputText = evt.OutputText ?? string.Empty,
            CompletedAt = completedAt.Clone(),
            Usage = evt.Usage?.Clone(),
        };
        foreach (var call in evt.ForwardedToolCalls)
        {
            next.Completion.ToolCalls.Add(new LlmSessionCompletedToolCall
            {
                CallId = call.CallId,
                ToolName = call.ToolName,
                Result = RuntimeToolArgumentsValue(call),
            });
        }

        Bump(next, $"{evt.ResponseId}:run:{evt.RunId}:completed");
        return next;
    }

    private static LlmSessionState ApplyRunFailed(
        LlmSessionState state,
        LlmRunFailed evt)
    {
        var next = state.Clone();
        var failedAt = evt.FailedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        var run = EnsureRun(next, evt.RunId, evt.ResponseId, failedAt);
        if (!CanAcceptRunTerminalSequence(run, evt.Sequence))
            return state;

        run.Status = FailedStatus;
        run.CompletedAt = failedAt.Clone();
        run.FailureCode = NormalizeOptional(evt.FailureCode) ?? "execution_failed";
        run.FailureMessage = NormalizeOptional(evt.FailureMessage) ?? "LLM run failed.";
        if (next.Record == null)
            next.Record = new LlmSessionRecord();
        next.Record.Status = LlmSessionStatus.Failed;
        next.Record.UpdatedAt = failedAt.Clone();
        next.Completion = new LlmSessionCompletion
        {
            OutputText = run.OutputText ?? string.Empty,
            FailureCode = run.FailureCode,
            FailureMessage = run.FailureMessage,
            CompletedAt = failedAt.Clone(),
            Usage = run.Usage?.Clone(),
        };
        Bump(next, $"{evt.ResponseId}:run:{evt.RunId}:failed");
        return next;
    }

    private static LlmSessionState ApplyRunCancelled(
        LlmSessionState state,
        LlmRunCancelled evt)
    {
        var next = state.Clone();
        var cancelledAt = evt.CancelledAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        var run = EnsureRun(next, evt.RunId, evt.ResponseId, cancelledAt);
        if (!CanAcceptRunTerminalSequence(run, evt.Sequence))
            return state;

        run.Status = CancelledStatus;
        run.CompletedAt = cancelledAt.Clone();
        if (next.Record == null)
            next.Record = new LlmSessionRecord();
        next.Record.Status = LlmSessionStatus.Cancelled;
        next.Record.CancelledAt = cancelledAt.Clone();
        next.Record.UpdatedAt = cancelledAt.Clone();
        next.Completion = new LlmSessionCompletion
        {
            OutputText = run.OutputText ?? string.Empty,
            FailureCode = "request_cancelled",
            FailureMessage = "LLM run was cancelled.",
            CompletedAt = cancelledAt.Clone(),
            Usage = run.Usage?.Clone(),
        };
        MarkOpenToolCalls(next, LlmSessionForwardedToolCallStatus.Cancelled);
        Bump(next, $"{evt.ResponseId}:run:{evt.RunId}:cancelled");
        return next;
    }

    private static Value RuntimeToolArgumentsValue(LlmSessionRuntimeToolCall call)
    {
        if (call.Arguments is { Fields.Count: > 0 })
            return Value.ForStruct(call.Arguments.Clone());
        return ResponsesJsonValues.ParseBoundaryPayload(
            string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
    }

    private static Struct MergeStruct(Struct? existing, Struct incoming)
    {
        var merged = existing?.Clone() ?? new Struct();
        foreach (var (key, value) in incoming.Fields)
            merged.Fields[key] = value.Clone();
        return merged;
    }

    private static LlmSessionRunScope EnsureRun(
        LlmSessionState state,
        string runId,
        string responseId,
        Timestamp? observedAt)
    {
        if (state.ActiveRun == null ||
            !string.Equals(state.ActiveRun.RunId, runId, StringComparison.Ordinal))
        {
            state.ActiveRun = new LlmSessionRunScope
            {
                RunId = runId,
                Status = RunningStatus,
                StartedAt = observedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
            };
        }

        if (state.Record == null)
            state.Record = new LlmSessionRecord { ResponseId = responseId };
        return state.ActiveRun;
    }

    private long NextRunSequence(string runId)
    {
        if (State.ActiveRun == null ||
            !string.Equals(State.ActiveRun.RunId, runId, StringComparison.Ordinal))
        {
            return 1;
        }

        return State.ActiveRun.LastAppliedSequence + 1;
    }

    private static bool TryAcceptRunSequence(LlmSessionRunScope run, long sequence)
    {
        if (IsRunTerminal(run.Status))
            return false;
        if (sequence <= 0)
            return true;
        if (sequence != run.LastAppliedSequence + 1)
            return false;

        run.LastAppliedSequence = sequence;
        return true;
    }

    private static bool CanAcceptRunTerminalSequence(LlmSessionRunScope run, long sequence)
    {
        if (IsRunTerminal(run.Status))
            return false;
        if (sequence <= 0)
            return true;
        if (sequence < run.LastAppliedSequence + 1)
            return false;

        run.LastAppliedSequence = sequence;
        return true;
    }

    private static void TouchRecord(LlmSessionState state, Timestamp? updatedAt)
    {
        if (state.Record == null)
            state.Record = new LlmSessionRecord();
        state.Record.UpdatedAt = updatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
    }

    private static void Bump(LlmSessionState state, string eventId)
    {
        state.LastAppliedEventVersion++;
        state.LastEventId = eventId;
    }

    private static bool IsRunTerminal(int status) =>
        status is CompletedStatus or FailedStatus or CancelledStatus;

    private bool CanPersistRunFact(string responseId, string runId, long sequence, bool terminal)
    {
        var existing = EnsureRegisteredSession(responseId);
        if (IsTerminal(existing.Status))
            return false;

        if (State.ActiveRun == null ||
            !string.Equals(State.ActiveRun.RunId, runId, StringComparison.Ordinal))
        {
            return true;
        }

        if (IsRunTerminal(State.ActiveRun.Status))
            return false;

        if (sequence <= 0)
            return true;

        var nextSequence = State.ActiveRun.LastAppliedSequence + 1;
        return terminal
            ? sequence >= nextSequence
            : sequence == nextSequence;
    }

    private static void UpsertRuntimeToolCall(
        LlmSessionRunScope run,
        LlmSessionRuntimeToolCall incoming)
    {
        var existing = run.ObservedToolCalls
            .FirstOrDefault(call => string.Equals(call.CallId, incoming.CallId, StringComparison.Ordinal));
        if (existing is null)
        {
            run.ObservedToolCalls.Add(incoming.Clone());
            return;
        }

        if (!string.IsNullOrWhiteSpace(incoming.ToolName))
            existing.ToolName = incoming.ToolName;
        if (incoming.Arguments is { Fields.Count: > 0 })
            existing.Arguments = MergeStruct(existing.Arguments, incoming.Arguments);
        if (!string.IsNullOrEmpty(incoming.ArgumentsJson))
            existing.ArgumentsJson += incoming.ArgumentsJson;
    }

    private Task PersistStreamChunkObservedAsync(LlmStreamChunkObserved observed, CancellationToken ct)
    {
        if (observed.Sequence <= 0)
            observed.Sequence = NextRunSequence(observed.RunId);
        return PersistDomainEventAsync(observed, ct);
    }

    private Task PersistToolCallObservedAsync(LlmToolCallObserved observed, CancellationToken ct)
    {
        if (observed.Sequence <= 0)
            observed.Sequence = NextRunSequence(observed.RunId);
        return PersistDomainEventAsync(observed, ct);
    }

    private Task PersistRunCompletedAsync(LlmRunCompleted completed, CancellationToken ct)
    {
        if (completed.Sequence <= 0)
            completed.Sequence = NextRunSequence(completed.RunId);
        if (CompletionAlreadyRecorded(completed) || RunTerminalAlreadyRecorded(completed.RunId))
            return Task.CompletedTask;

        return PersistDomainEventAsync(
            completed,
            conflict => Task.FromResult(CompletionAlreadyRecorded(completed) || RunTerminalAlreadyRecorded(completed.RunId)),
            ct);
    }

    private Task PersistRunFailedAsync(LlmRunFailed failed, CancellationToken ct)
    {
        if (failed.Sequence <= 0)
            failed.Sequence = NextRunSequence(failed.RunId);
        if (RunTerminalAlreadyRecorded(failed.RunId))
            return Task.CompletedTask;

        return PersistDomainEventAsync(
            failed,
            conflict => Task.FromResult(RunTerminalAlreadyRecorded(failed.RunId)),
            ct);
    }

    private Task PersistRunCancelledAsync(LlmRunCancelled cancelled, CancellationToken ct)
    {
        if (cancelled.Sequence <= 0)
            cancelled.Sequence = NextRunSequence(cancelled.RunId);
        if (RunTerminalAlreadyRecorded(cancelled.RunId))
            return Task.CompletedTask;

        return PersistDomainEventAsync(
            cancelled,
            conflict => Task.FromResult(RunTerminalAlreadyRecorded(cancelled.RunId)),
            ct);
    }

    private bool CompletionAlreadyRecorded(LlmRunCompleted completed)
    {
        if (State.Completion is not { CompletedAt: not null } existingCompletion)
            return false;
        if (!string.IsNullOrWhiteSpace(existingCompletion.FailureCode))
            return false;
        if (!string.Equals(existingCompletion.OutputText ?? string.Empty, completed.OutputText ?? string.Empty, StringComparison.Ordinal))
            return false;
        if (!UsageEquals(existingCompletion.Usage, completed.Usage))
            return false;

        var run = State.ActiveRun;
        return run == null ||
            !string.Equals(run.RunId, completed.RunId, StringComparison.Ordinal) ||
            run.Status == CompletedStatus;
    }

    private bool RunTerminalAlreadyRecorded(string runId) =>
        State.ActiveRun is { } run &&
        string.Equals(run.RunId, runId, StringComparison.Ordinal) &&
        IsRunTerminal(run.Status);

    private sealed class InActorLlmRunSink(LlmSessionGAgent actor) : ILlmRunSink
    {
        public Task RecordStreamChunkObservedAsync(
            LlmStreamChunkObserved observed,
            CancellationToken ct = default) =>
            actor.PersistStreamChunkObservedAsync(observed, ct);

        public Task RecordToolCallObservedAsync(
            LlmToolCallObserved observed,
            CancellationToken ct = default) =>
            actor.PersistToolCallObservedAsync(observed, ct);

        public Task RecordForwardedToolCallEmittedAsync(
            LlmSessionForwardedToolCallEmittedEvent emitted,
            CancellationToken ct = default) =>
            actor.PersistDomainEventAsync(emitted, ct);

        public Task RecordRunCompletedAsync(
            LlmRunCompleted completed,
            CancellationToken ct = default) =>
            actor.PersistRunCompletedAsync(completed, ct);

        public Task RecordRunFailedAsync(
            LlmRunFailed failed,
            CancellationToken ct = default) =>
            actor.PersistRunFailedAsync(failed, ct);

        public Task RecordRunCancelledAsync(
            LlmRunCancelled cancelled,
            CancellationToken ct = default) =>
            actor.PersistRunCancelledAsync(cancelled, ct);
    }

    private sealed class SelfDispatchingLlmRunSink(
        string actorId,
        IActorDispatchPort dispatchPort,
        long initialSequence) : ILlmRunSink
    {
        private long _lastSequence = initialSequence;

        public Task RecordStreamChunkObservedAsync(
            LlmStreamChunkObserved observed,
            CancellationToken ct = default)
        {
            var payload = observed.Clone();
            payload.Sequence = NextSequence();
            return DispatchAsync(payload, ct);
        }

        public Task RecordToolCallObservedAsync(
            LlmToolCallObserved observed,
            CancellationToken ct = default)
        {
            var payload = observed.Clone();
            payload.Sequence = NextSequence();
            return DispatchAsync(payload, ct);
        }

        public Task RecordForwardedToolCallEmittedAsync(
            LlmSessionForwardedToolCallEmittedEvent emitted,
            CancellationToken ct = default) =>
            DispatchAsync(emitted.Clone(), ct);

        public Task RecordRunCompletedAsync(
            LlmRunCompleted completed,
            CancellationToken ct = default)
        {
            var payload = completed.Clone();
            payload.Sequence = NextSequence();
            return DispatchAsync(payload, ct);
        }

        public Task RecordRunFailedAsync(
            LlmRunFailed failed,
            CancellationToken ct = default)
        {
            var payload = failed.Clone();
            payload.Sequence = NextSequence();
            return DispatchAsync(payload, ct);
        }

        public Task RecordRunCancelledAsync(
            LlmRunCancelled cancelled,
            CancellationToken ct = default)
        {
            var payload = cancelled.Clone();
            payload.Sequence = NextSequence();
            return DispatchAsync(payload, ct);
        }

        private long NextSequence() => Interlocked.Increment(ref _lastSequence);

        private async Task DispatchAsync(IMessage payload, CancellationToken ct)
        {
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(payload),
            };
            await dispatchPort.DispatchAsync(actorId, envelope, ct).ConfigureAwait(false);
        }
    }

    private static LlmSessionRecord NormalizeRecord(LlmSessionRecord record)
    {
        record.ResponseId = NormalizeRequired(record.ResponseId);
        record.ScopeId = NormalizeRequired(record.ScopeId);
        record.OwnerSubject = NormalizeRequired(record.OwnerSubject);
        record.PreviousResponseId = NormalizeOptional(record.PreviousResponseId) ?? string.Empty;
        if (record.CreatedAt == null)
            record.CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        if (record.UpdatedAt == null)
            record.UpdatedAt = record.CreatedAt.Clone();
        if (record.Ttl == null)
            record.Ttl = DefaultTtl.Clone();
        if (record.Status == LlmSessionStatus.Unspecified)
            record.Status = LlmSessionStatus.Accepted;
        return record;
    }

    private static void ValidateRecord(LlmSessionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ResponseId))
            throw new InvalidOperationException("response_id is required.");
        if (string.IsNullOrWhiteSpace(record.ScopeId))
            throw new InvalidOperationException("scope_id is required.");
        if (string.IsNullOrWhiteSpace(record.OwnerSubject))
            throw new InvalidOperationException("owner_subject is required.");
        if (record.OriginKind == LlmSessionOriginKind.Unspecified)
            throw new InvalidOperationException("origin_kind is required.");
        if (record.Ttl == null || record.Ttl.ToTimeSpan() <= TimeSpan.Zero)
            throw new InvalidOperationException("ttl must be greater than zero.");
    }

    private LlmSessionRecord EnsureRegisteredSession(string? responseId)
    {
        var existing = State.Record;
        if (existing == null || string.IsNullOrWhiteSpace(existing.ResponseId))
        {
            throw new InvalidOperationException(
                $"Response session actor '{Id}' has no registered response.");
        }

        var normalizedResponseId = NormalizeRequired(responseId);
        if (!string.Equals(existing.ResponseId, normalizedResponseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Response session actor '{Id}' is bound to response '{existing.ResponseId}' and cannot handle response '{normalizedResponseId}'.");
        }

        return existing;
    }

    private static LlmSessionForwardedToolCall NormalizeToolCall(LlmSessionForwardedToolCall call)
    {
        call.CallId = NormalizeRequired(call.CallId);
        call.ToolName = NormalizeRequired(call.ToolName);
        call.SchemaHash = NormalizeRequired(call.SchemaHash);
        if (call.Status == LlmSessionForwardedToolCallStatus.Unspecified)
            call.Status = LlmSessionForwardedToolCallStatus.Pending;
        if (call.EmittedAt == null)
            call.EmittedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        if (call.Expiry == null)
            call.Expiry = Timestamp.FromDateTime(DateTime.UtcNow.Add(DefaultTtl.ToTimeSpan()));
        return call;
    }

    private static void ValidateToolCall(LlmSessionForwardedToolCall call)
    {
        if (string.IsNullOrWhiteSpace(call.CallId))
            throw new InvalidOperationException("call_id is required.");
        if (string.IsNullOrWhiteSpace(call.ToolName))
            throw new InvalidOperationException("tool_name is required.");
        if (string.IsNullOrWhiteSpace(call.SchemaHash))
            throw new InvalidOperationException("schema_hash is required.");
        if (call.Status != LlmSessionForwardedToolCallStatus.Pending)
            throw new InvalidOperationException("forwarded tool calls must start as pending.");
        if (call.Expiry == null)
            throw new InvalidOperationException("expiry is required.");
    }

    private static LlmSessionCompletion NormalizeCompletion(LlmSessionCompletion completion)
    {
        completion.OutputText ??= string.Empty;
        completion.FailureCode = NormalizeOptional(completion.FailureCode) ?? string.Empty;
        completion.FailureMessage = NormalizeOptional(completion.FailureMessage) ?? string.Empty;
        if (completion.CompletedAt == null)
            completion.CompletedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        if (completion.Usage is not null)
        {
            completion.Usage.PromptTokens = Math.Max(0, completion.Usage.PromptTokens);
            completion.Usage.CompletionTokens = Math.Max(0, completion.Usage.CompletionTokens);
            completion.Usage.TotalTokens = Math.Max(0, completion.Usage.TotalTokens);
        }

        foreach (var toolCall in completion.ToolCalls)
        {
            toolCall.CallId = NormalizeRequired(toolCall.CallId);
            toolCall.ToolName = NormalizeRequired(toolCall.ToolName);
        }

        return completion;
    }

    private static void ValidateCompletion(LlmSessionCompletion completion)
    {
        if (completion.CompletedAt == null)
            throw new InvalidOperationException("completed_at is required.");
        if (!string.IsNullOrWhiteSpace(completion.FailureCode) &&
            string.IsNullOrWhiteSpace(completion.FailureMessage))
        {
            throw new InvalidOperationException("failure_message is required when failure_code is present.");
        }

        foreach (var toolCall in completion.ToolCalls)
        {
            if (string.IsNullOrWhiteSpace(toolCall.CallId))
                throw new InvalidOperationException("completion tool call_id is required.");
            if (string.IsNullOrWhiteSpace(toolCall.ToolName))
                throw new InvalidOperationException("completion tool tool_name is required.");
        }
    }

    private static void EnsureExistingMatches(
        LlmSessionRecord existing,
        LlmSessionRecord incoming)
    {
        if (!string.Equals(existing.ResponseId, incoming.ResponseId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' cannot be rebound to response '{incoming.ResponseId}'.");

        if (!string.Equals(existing.ScopeId, incoming.ScopeId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' is bound to scope '{existing.ScopeId}' and cannot rebind to scope '{incoming.ScopeId}'.");

        if (!string.Equals(existing.OwnerSubject, incoming.OwnerSubject, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' is bound to owner '{existing.OwnerSubject}' and cannot rebind to owner '{incoming.OwnerSubject}'.");

        if (existing.OriginKind != incoming.OriginKind)
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' is bound to origin '{existing.OriginKind}' and cannot rebind to origin '{incoming.OriginKind}'.");

        if (!string.Equals(
                NormalizeOptional(existing.PreviousResponseId),
                NormalizeOptional(incoming.PreviousResponseId),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' is bound to previous_response_id '{existing.PreviousResponseId}' and cannot rebind to '{incoming.PreviousResponseId}'.");
        }

        if (!DurationEquals(existing.Ttl, incoming.Ttl))
        {
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' is bound to ttl '{existing.Ttl}' and cannot rebind to '{incoming.Ttl}'.");
        }
    }

    private static void EnsureExistingToolCallMatches(
        LlmSessionForwardedToolCall existing,
        LlmSessionForwardedToolCall incoming)
    {
        if (!string.Equals(existing.ToolName, incoming.ToolName, StringComparison.Ordinal) ||
            !string.Equals(existing.SchemaHash, incoming.SchemaHash, StringComparison.Ordinal) ||
            !Equals(existing.Arguments, incoming.Arguments))
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{existing.CallId}' cannot be rebound to different tool call facts.");
        }
    }

    private static void EnsureExistingCompletionMatches(
        LlmSessionCompletion existing,
        LlmSessionCompletion incoming)
    {
        if (!string.Equals(existing.OutputText, incoming.OutputText, StringComparison.Ordinal) ||
            !string.Equals(existing.FailureCode, incoming.FailureCode, StringComparison.Ordinal) ||
            !string.Equals(existing.FailureMessage, incoming.FailureMessage, StringComparison.Ordinal) ||
            !UsageEquals(existing.Usage, incoming.Usage) ||
            existing.ToolCalls.Count != incoming.ToolCalls.Count)
        {
            throw new InvalidOperationException("Response session completion cannot be rebound to different facts.");
        }

        for (var i = 0; i < existing.ToolCalls.Count; i++)
        {
            var existingTool = existing.ToolCalls[i];
            var incomingTool = incoming.ToolCalls[i];
            if (!string.Equals(existingTool.CallId, incomingTool.CallId, StringComparison.Ordinal) ||
                !string.Equals(existingTool.ToolName, incomingTool.ToolName, StringComparison.Ordinal) ||
                !Equals(existingTool.Result, incomingTool.Result))
            {
                throw new InvalidOperationException("Response session completion cannot be rebound to different tool call facts.");
            }
        }
    }

    private static void MarkOpenToolCalls(
        LlmSessionState state,
        LlmSessionForwardedToolCallStatus status)
    {
        foreach (var call in state.ForwardedToolCalls)
        {
            if (call.Status is LlmSessionForwardedToolCallStatus.Pending
                or LlmSessionForwardedToolCallStatus.Received)
            {
                call.Status = status;
                if (status == LlmSessionForwardedToolCallStatus.Expired)
                {
                    // Mark received timestamp so downstream snapshots know when
                    // expiry happened. The result value stays empty —
                    // adapters/readers synthesize the "tool_call_expired" surface
                    // when shaping the response back to the client.
                    call.ReceivedAt ??= state.Record?.UpdatedAt?.Clone()
                                        ?? Timestamp.FromDateTime(DateTime.UtcNow);
                }
            }
        }
    }

    private Task ScheduleTtlExpiryAsync(
        LlmSessionRecord record,
        DateTimeOffset? observedAt = null)
    {
        var expiresAt = ResolveExpiry(record);
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var dueTime = expiresAt - now;
        if (dueTime <= TimeSpan.Zero)
            dueTime = TimeSpan.FromMilliseconds(1);

        return ScheduleSelfDurableTimeoutAsync(
            $"response-session:{record.ResponseId}:ttl",
            dueTime,
            new ExpireResponseSessionRequested
            {
                ResponseId = record.ResponseId,
                ObservedAt = Timestamp.FromDateTimeOffset(expiresAt),
            });
    }

    private static DateTimeOffset ResolveExpiry(LlmSessionRecord record)
    {
        var createdAt = record.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        var ttl = record.Ttl?.ToTimeSpan() ?? DefaultTtl.ToTimeSpan();
        return createdAt.Add(ttl);
    }

    private static bool IsTerminal(LlmSessionStatus status) =>
        status is LlmSessionStatus.Completed
            or LlmSessionStatus.Failed
            or LlmSessionStatus.Cancelled
            or LlmSessionStatus.Expired;

    private static bool DurationEquals(Duration? left, Duration? right) =>
        left?.ToTimeSpan() == right?.ToTimeSpan();

    private static bool UsageEquals(LlmSessionTokenUsage? left, LlmSessionTokenUsage? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return left.PromptTokens == right.PromptTokens &&
               left.CompletionTokens == right.CompletionTokens &&
               left.TotalTokens == right.TotalTokens;
    }

    private static string NormalizeRequired(string? value) =>
        NormalizeOptional(value) ?? string.Empty;

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
