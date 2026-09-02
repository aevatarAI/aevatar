# Research: Off-actor LLM run execution architecture & self-deadlock map

- **Query**: Map the exact deployed architecture around the `LlmRunExecutionGAgent` self-deadlock so a fix can be designed.
- **Scope**: internal (code map, no external)
- **Date**: 2026-06-21
- **Deployed/broken build**: committed `HEAD = 45c1bd208` on `feature/integrate`.

> ⚠️ **SURPRISING CONSTRAINT — read first.** The deadlock fix is **already partially
> implemented in the uncommitted working tree** and is NOT what is deployed. The deployed
> (broken) architecture lives in the **committed** `45c1bd20`; the working tree has the
> in-progress off-grain worker. See [§0 Working-tree vs deployed](#0-working-tree-vs-deployed-critical).
> All `file:line` anchors below are tagged **[HEAD]** (deployed) or **[worktree]** (the WIP fix).

---

## Deadlock loop in 6 bullets (deployed `45c1bd20`, all `[HEAD]`)

1. Session actor commits `RunStarted` then hands off:
   `LlmSessionGAgent.TryDispatchTransientExecutionCommandAsync` →
   `_executionScheduler.ScheduleAsync(...)` — `LlmSessionGAgent.cs:425,429-443` **[worktree line; HEAD identical contract]**.
2. Scheduler provisions a **per-run grain** and dispatches a command to it:
   `LlmRunExecutionScheduler.ScheduleAsync` → `executionTargetProvisioner.EnsureExecutionTargetAsync` (`CreateByKindAsync(LlmRunExecutionGAgent.Kind, actorId)`) → `dispatchPort.DispatchAsync(executionActorId, ExecuteLlmRunRequested)` — committed `LlmRunExecutionScheduler.cs` (HEAD) lines 24-48; actorId = `gagent-service:llm-run-execution:<sessionActorId>:<runId>`, `LlmRunExecutionTargetProvisioner.cs:24` (HEAD).
3. The grain runs the **whole ~60s LLM loop inside one non-reentrant event-handler turn**:
   `LlmRunExecutionGAgent.HandleExecuteAsync` is `[EventHandler]` and does
   `return _executionService.ExecuteAsync(...)` — `LlmRunExecutionGAgent.cs:22-36` (HEAD). It never returns the turn until the run ends.
4. Inside the run, for **each** record, the sink dispatches the `Record*` to the session actor and then **awaits the committed event to come back over an Orleans stream**:
   `LlmRunExecutor.DispatchingLlmRunSink.DispatchAsync` — dispatch at `LlmRunExecutor.cs:305`, then `await foreach sink.ReadAllAsync(observeCt)` at `:306`. The sink it reads is attached at `:298` via `observationProjectionPort.AttachExistingResponseProjectionAsync`.
5. That attach subscribes the **execution grain itself** as an Orleans stream consumer:
   `LlmSessionObservationProjectionPort.AttachExistingResponseProjectionAsync` → `AttachLiveSinkAsync` (`EventSinkProjectionLifecyclePortBase.cs:35-60`) → `_sessionEventHub.SubscribeAsync` → `ProjectionSessionEventHub.SubscribeAsync` → `stream.SubscribeAsync(...)` (`ProjectionSessionEventHub.cs:67-117`). The subscription binds to the **calling grain's context** (the execution grain).
6. **Collapse**: the session actor commits the record → projector republishes it to the hub stream → Orleans must `DeliverBatch` into the execution grain's subscription callback → but that grain's single turn is blocked at step 4 awaiting exactly that delivery → 30s stream-delivery timeout → log `Failed to deliver message to consumer …:<responseId>:llm-run` + `Response did not arrive on time in 00:00:30` → no record decision → no terminal → the already-flushed `200 text/event-stream` never completes → client truncates.

The producer (session actor committing facts) and the consumer (execution grain awaiting them) are decoupled in principle, but the **execution grain is simultaneously the run driver AND a stream subscriber for the same session**, so its blocked turn starves its own delivery.

---

## 0. Working-tree vs deployed (CRITICAL)

`git status --porcelain` (relevant subset) shows the deadlock fix is mid-flight and **uncommitted**:

```
 M src/platform/Aevatar.GAgentService.Abstractions/Responses/LlmRunCoreContracts.cs
 M src/platform/Aevatar.GAgentService.Application/Responses/LlmRunExecutionScheduler.cs
D  src/platform/Aevatar.GAgentService.Core/GAgents/LlmRunExecutionGAgent.cs          <- deadlocking grain DELETED
 M src/platform/Aevatar.GAgentService.Core/GAgents/LlmSessionGAgent.cs
 M src/platform/Aevatar.GAgentService.Hosting/DependencyInjection/ServiceCollectionExtensions.cs
D  src/platform/Aevatar.GAgentService.Infrastructure/Activation/LlmRunExecutionTargetProvisioner.cs  <- grain provisioner DELETED
?? src/platform/Aevatar.GAgentService.Hosting/Responses/   <- NEW: LlmRunExecutionQueue.cs, LlmRunExecutionWorker.cs, LlmRunExecutionWorkerOptions.cs
```

- **Deployed (`45c1bd20`, committed)** = the per-run grain path that deadlocks (mapped above).
- **Working tree (uncommitted)** = the off-grain `BackgroundService` worker that the
  `06-21-offgrain-llm-run-executor` design (codex GREEN) prescribes. The session actor still
  calls the same `ILlmRunExecutionScheduler.ScheduleAsync` seam; only the **scheduler impl**
  changed (enqueue to a bounded in-process `Channel` drained by `LlmRunExecutionWorker`), and
  the grain + provisioner are deleted.
- The **new task's** `task.json` sets `base_branch = fix/2026-06-21_offgrain-llm-run-executor`
  — i.e. this task is meant to continue/validate that WIP, not start from scratch.

**Implication for the fix:** most of the work already exists in the working tree. The open
risk is NOT "design the off-turn model" (done + reviewed) but: (a) does the worktree change
actually remove the deadlock given the sink still does `await foreach sink.ReadAllAsync` per
record (§2), and (b) is the WIP complete/tested/correct per the codex GREEN design (§6)?

When reading non-deadlock files (observation ports, `LlmRunCore`, `LlmRunExecutor`,
endpoints, facade) note: **those are NOT in the uncommitted set, so worktree == deployed for
them.** Only the 6 files above differ between deployed and worktree.

---

## 1. Execution service — `ILlmRunExecutionService` / `ExecuteAsync`

- **Interface**: `ILlmRunExecutionService.ExecuteAsync(LlmRunExecutionRequest, ct)` —
  `LlmRunCoreContracts.cs:55-60` (both HEAD and worktree).
- **Request type**: `LlmRunExecutionRequest(SessionActorId, ResponseId, RunId, LlmRunRequested Command, string? OriginPlatform)` — `LlmRunCoreContracts.cs:10-15`.
- **Implementation**: `LlmRunExecutor` (it implements BOTH `ILlmRunExecutor` and
  `ILlmRunExecutionService`) — `LlmRunExecutor.cs:13-19`. `ExecuteAsync` is at `:63-110`.
- **What `ExecuteAsync` does start→finish** (`LlmRunExecutor.cs:63-110`):
  1. Validates + trims the request (`:67-78`).
  2. `await runCore.RunAsync(new LlmRunCoreRequest(...), new DispatchingLlmRunSink(...), ct)`
     (`:82-99`) — drives the full LLM loop, feeding records through the sink.
  3. On any exception, `DispatchExecutorFailureAsync` dispatches a `RecordLlmRunFailed`
     (`failure_code = "executor_failed"`) to the session actor so a terminal is still produced
     (`:101-143`). This is the executor's own crash safety net (gap #6 in the design).
- **Plain DI service or grain-coupled?** Plain DI service. It owns **no** actor/grain state;
  it only does I/O + tool exec (via `ILlmRunCore`) and dispatches commands via
  `IActorDispatchPort` + attaches observation leases via the two projection ports. It is the
  intended "DI service, not a per-run grain" of the original design. **The grain is merely a
  wrapper that calls it on a blocked turn — that wrapper is the bug.**
- **DI registration** (`ServiceCollectionExtensions.cs`, GAgentService Hosting):
  - `TryAddSingleton<LlmRunExecutor>()` — `:87`
  - `TryAddSingleton<ILlmRunExecutor>(sp => sp.GetRequiredService<LlmRunExecutor>())` — `:88`
  - `TryAddSingleton<ILlmRunExecutionService>(sp => sp.GetRequiredService<LlmRunExecutor>())` — `:89`
  - **All singletons.** `[worktree]` adds the queue/worker at `:94-99`; `[HEAD]` instead had
    `TryAddSingleton<ILlmRunExecutionTargetProvisioner, LlmRunExecutionTargetProvisioner>()`
    (removed in worktree, per the DI diff).

## 2. Executor + sink — the deadlock crux

File: `LlmRunExecutor.cs` (worktree == deployed; not in uncommitted set).

### The loop it drives (`LlmRunCore`)
`runCore.RunAsync` → `LlmRunCore.RunLlmLoopAsync` (`LlmRunCore.cs:57-193`):
- `provider.ChatStreamAsync(roundRequest, ct)` streamed per round (≤ `MaxToolRounds = 8`,
  `LlmRunCore.cs:20`), `await foreach (var chunk ...)` at `:101`.
- Each chunk → builds `LlmStreamChunkObserved` and **`await sink.RecordStreamChunkObservedAsync(...)`** (`:120`); `ShouldStop` on the returned decision ends the loop.
- Forwarded tool calls → `sink.RecordRunCompletedAsync` (`:147`); no tool calls →
  `RecordRunCompletedAsync` (`:154`); local tool calls executed then `RecordToolCallObservedAsync`
  (`:317`); round exhaustion → `RecordRunFailedAsync(max_tool_rounds_exhausted)` (`:185`).
- Cancellation/exception wrappers dispatch `RecordRunCancelled`/`RecordRunFailed` (`:35-54`).
- **Key**: the core treats `sink` as fire-and-await; ordering is preserved because each
  `Record*` is awaited before the next.

### The sink — `DispatchingLlmRunSink` (`LlmRunExecutor.cs:165-400`)
Every `Record*` method (`RecordStreamChunkObservedAsync` `:176`, `RecordToolCallObservedAsync`
`:198`, `RecordRunCompletedAsync` `:221`, `RecordRunFailedAsync` `:241`,
`RecordRunCancelledAsync` `:261`) builds the proto `RecordLlm*` and calls the private
`DispatchAsync(recordId, command, ct)` (`:279-345`). `DispatchAsync` is the heart of the
deadlock:

```
279  private async Task<LlmRunRecordDecision> DispatchAsync(string recordId, IMessage command, CancellationToken ct)
281      using var timeoutCts = CreateLinkedTokenSource(ct);
282-283  if (recordObservationTimeout > 0) timeoutCts.CancelAfter(recordObservationTimeout);
286      await using var sink = new EventChannel<EventEnvelope>(SinkCapacity);   // SinkCapacity = 64 (:23 const? -> :23 DefaultRecordObservationTimeout; SinkCapacity const :22 = 64)
291-293  preparation = await observationScopeLeasePreparationPort.PrepareAsync(sessionActorId, responseId, observeCt);  // ensures projection scope
298-300  attachment = await observationProjectionPort.AttachExistingResponseProjectionAsync(sessionActorId, responseId, sink, observeCt);  // SUBSCRIBE THIS GRAIN to the session's hub stream
305      await dispatch(recordId, command, observeCt);   // dispatch RecordLlm* to the SESSION actor
306      await foreach (var envelope in sink.ReadAllAsync(observeCt))   // WAIT for our own record to come back
308-316     ... if committed payload's RecordId == recordId -> return decision (terminal sets StopDispatching)
319      throw "observation ended before record '{recordId}' was committed"
322-325  catch OperationCanceledException (timeout) -> throw TimeoutException("Timed out waiting for LLM run record ...")
327-344  finally: DetachLiveSinkAsync + ReleaseActorProjectionAsync + scope ReleaseAsync
```

- **`dispatch` delegate** (used at `:305`): wired at `LlmRunExecutor.cs:91-95` as
  `(recordId, command, token) => DispatchCommandAsync(sessionActorId, recordId, command, token)`.
  `DispatchCommandAsync` (`:145-163`) builds an `EventEnvelope` (`Route =
  CreateDirect(PublisherId="gagent-service.llm-run-executor", sessionActorId)`,
  `Propagation.CorrelationId = recordId`) and calls
  **`dispatchPort.DispatchAsync(sessionActorId, envelope, ct)`** — i.e. the `Record*` goes to
  the **session actor** (`LlmSessionGAgent`), which is the correct fact owner. **Not** to the
  execution grain.
- **`observationScopeLeasePreparationPort.PrepareAsync` (`:291`)**: ensures the
  `LlmSessionObservation` projection scope is active for `(sessionActorId, responseId)` —
  `LlmSessionObservationScopeLeasePreparationPort.cs:20-54` → `_sessionActivationService.EnsureAsync(ProjectionScopeStartRequest{ RootActorId=actorId, SessionId=responseId, Mode=SessionObservation })`.
- **`observationProjectionPort.AttachExistingResponseProjectionAsync` (`:298`)**: attaches a
  **live sink** to that scope —
  `LlmSessionObservationProjectionPort.cs:29-61` → `_attachExistingLeaseLookup.TryGetAsync(...)`
  then `AttachLiveSinkAsync(lease, sink)` → `EventSinkProjectionLifecyclePortBase.AttachLiveSinkAsync`
  (`:35-60`) → `_sessionEventHub.SubscribeAsync(RootActorId, SessionId, evt => sink.PushAsync(evt))`.
- **Why the consumer is the execution grain**: `_sessionEventHub.SubscribeAsync` →
  `ProjectionSessionEventHub.SubscribeAsync` (`ProjectionSessionEventHub.cs:67-117`) calls
  `stream.SubscribeAsync(...)` on an **Orleans stream** keyed
  `"{codec.Channel}:{rootActorId}:{sessionId}"` (`:119-120`). An Orleans stream subscription's
  delivery executes on the **subscribing grain's context**. Because `DispatchAsync` runs inside
  `LlmRunExecutionGAgent`'s turn (deployed), the subscription is bound to that grain, so
  `DeliverBatch` for the republished record needs that grain's turn — which is blocked awaiting
  the delivery at `:306`. The log consumer id `…:<responseId>:llm-run` is exactly the execution
  grain's actorId (`gagent-service:llm-run-execution:<sessionActorId>:<responseId>:llm-run`).
- **`recordObservationTimeout`**: `_recordObservationTimeout = ingressOptions?.Value?.ObservationTimeout
  ?? DefaultRecordObservationTimeout` where `DefaultRecordObservationTimeout = 300s`
  (`LlmRunExecutor.cs:23-25`). `ObservationTimeout` defaults to 300s
  (`ResponsesIngressOptions.cs:29-32`). So each record waits up to 300s — but the **Orleans
  stream-delivery promise** breaks at its own 30s first (the deployed symptom), so the per-record
  300s rarely matters in the grain path.

**Crux pinpoint:** producer and consumer collapse at the pair
[`LlmRunExecutor.cs:298` attach-live-sink-from-this-grain] + [`LlmRunExecutor.cs:306`
await-foreach-on-that-sink], executed inside [`LlmRunExecutionGAgent.cs:28` the grain's
event-handler turn]. The off-grain worker (worktree) moves the SAME `:298`/`:306` code onto a
thread-pool thread, so the stream subscription is no longer bound to a single occupied grain
turn — that is the entire fix mechanism (see §6, §7).

## 3. Who dispatches `ExecuteLlmRunRequested` to the grain (deployed path)

- **Facade entry**: `ResponsesCommandFacade.StreamAsync` (`:204-270`) and `ExecuteNonStreamingAsync`
  (`:510-589`) both run via `observationService.ObserveAsync(...)` with a dispatch lambda that
  branches on the flag:
  ```
  221-223 / 523-525:
    var admission = _offActorLlmRunExecutorEnabled
        ? await StartOffActorRunAsync(plan, token)   // off-actor branch
        : await DispatchRunAsync(plan, token);       // legacy branch
  ```
- **Flag**: `_offActorLlmRunExecutorEnabled = ingressOptions?.Value?.OffActorLlmRunExecutorEnabled
  == true && llmRunExecutor is not null` (`ResponsesCommandFacade.cs:51-52`).
  `OffActorLlmRunExecutorEnabled` **defaults to `true`** (`ResponsesIngressOptions.cs:43`,
  baked-in code default per commit `3d47b82cc`).
- **`StartOffActorRunAsync` (`:965-971`)**: builds `LlmRunExecutorRequest` and calls
  `llmRunExecutor!.StartAsync(request, ct)`. ⚠️ **`StartAsync` only dispatches `RecordLlmRunStarted`
  to the session actor** (`LlmRunExecutor.cs:27-61`) — it does **not** run the loop and does
  **not** publish `ExecuteLlmRunRequested`. So the off-actor facade branch's only job is to seed
  the run start on the session actor.
- **`DispatchRunAsync` (`:948-963`, legacy branch)**: builds `LlmRunRequested` and dispatches it
  to the session actor (`dispatchPort.DispatchAsync(plan.Session.ActorId, envelope, ...)`).
- **Where `ExecuteLlmRunRequested` is actually emitted**: NOT from the facade. It is emitted by
  the **session actor → scheduler** chain (deployed), independent of which facade branch ran:
  1. `RecordLlmRunStarted` (off-actor) **or** `LlmRunRequested` (legacy) reaches the session actor.
  2. `LlmSessionGAgent.HandleRecordRunStartedAsync` (`:339`) / `HandleLlmRunRequestedAsync`
     (`:319`) → both call `TryCommitRunStartedAsync` (`:381-427`).
  3. `TryCommitRunStartedAsync` commits `LlmRunStartedEvent` + schedules the durable run-timeout
     + commits `LlmRunExecutionReadyEvent`, then `TryDispatchTransientExecutionCommandAsync`
     (`:425`).
  4. `TryDispatchTransientExecutionCommandAsync` (`:429-465`) → `_executionScheduler.ScheduleAsync(
     new LlmRunExecutionRequest(Id, responseId, runId, executionRequest.Clone(), origin))`.
  5. **[HEAD] `LlmRunExecutionScheduler.ScheduleAsync`** (committed) →
     `executionTargetProvisioner.EnsureExecutionTargetAsync(request)` (provisions the grain via
     `CreateByKindAsync`) → builds `ExecuteLlmRunRequested{ SessionActorId, ResponseId, RunId,
     Command, OriginPlatform }` → `dispatchPort.DispatchAsync(executionActorId, envelope)` where
     `Route = CreateDirect("gagent-service.llm-run-executor", executionActorId)`,
     `Id = "execute-{responseId}-{runId}"`.
  6. `LlmRunExecutionGAgent.HandleExecuteAsync` (`[EventHandler]`) consumes it →
     `return _executionService.ExecuteAsync(...)` → deadlock.
- **ActorId pattern confirmed**: `LlmRunExecutionTargetProvisioner.BuildActorId` (HEAD `:24`):
  `gagent-service:llm-run-execution:{Uri.EscapeDataString(sessionActorId)}:{Uri.EscapeDataString(runId)}`,
  and `runId = "{responseId}:llm-run"` (`ResponsesCommandFacade.BuildRunRequested` `:999`).
  → matches PRD log evidence `…:chatcmpl_…:llm-run`. **It is published to the grain's inbox**
  (`DispatchAsync` to a distinct execution actorId), not `self`.
- **Proto**: `ExecuteLlmRunRequested` defined at
  `src/platform/Aevatar.GAgentService.Abstractions/Protos/llm_sessions.proto:259`. The **only**
  non-generated source reference is the proto file itself (the C# producer = scheduler and
  consumer = grain are both deleted in the worktree). Safe-to-delete consideration handled in
  the design (keep proto for one rolling-deploy window).

## 4. Session actor — the fact owner (`LlmSessionGAgent`)

File `LlmSessionGAgent.cs` (worktree differs from HEAD only by the run-timeout default; the
`Record*` handlers are identical between HEAD and worktree).

- **Ctor dependency**: injects ONLY `ILlmRunExecutionScheduler` (`:34-40`). It has **no**
  `ILlmRunCore`/provider/executor reference — it cannot and does not run the loop itself. (This
  corrects the old "legacy = in-actor loop" assumption: both flag branches converge on the
  scheduler; the legacy branch only differs in WHICH command seeds the start.)
- **`Record*` command handlers** (the off-turn executor dispatches these in; they persist the
  durable run facts):
  - `HandleRecordRunStartedAsync(RecordLlmRunStarted)` `:339-358` → `TryCommitRunStartedAsync`.
  - `HandleRecordStreamChunkObservedAsync(RecordLlmStreamChunkObserved)` `:361-379` →
    `PersistDomainEventAsync(LlmStreamChunkObserved{... Sequence = NextRunSequence(runId), RecordId})`.
  - `HandleRecordToolCallObservedAsync(RecordLlmToolCallObserved)` `:476-495`.
  - `HandleRecordRunCompletedAsync(RecordLlmRunCompleted)` `:498-517`.
  - `HandleRecordRunFailedAsync(RecordLlmRunFailed)` `:520-536`.
  - `HandleRecordRunCancelledAsync(RecordLlmRunCancelled)` `:539-553`.
  - All gate via `TryPrepareRunRecord` (`:1281-1313+`) for active-run / record-id idempotency.
- **Run-state persistence**: the reducer (`TransitionState` `:667-683`) applies
  `LlmRunStartedEvent`/`LlmStreamChunkObserved`/`LlmToolCallObserved`/`LlmRunCompleted`/
  `LlmRunFailed`/`LlmRunCancelled` onto `State.ActiveRun` (`LlmSessionRunScope`), accumulating
  `OutputText`, `Usage`, `Status`, `LastAppliedSequence`, `AppliedRecordIds` (`:805-961`).
  Terminal events set `State.Record.Status` + `State.Completion` (`:858-961`). Idempotency:
  `TryAcceptRunRecord`/`TryAcceptRunSequence`/`CanPersistRunFact` (`:1012-1095`).
- **Drives the observation/streaming projection the HTTP SSE reads?** Indirectly: the session
  actor `PersistDomainEventAsync`s the committed run events; those committed events flow into the
  `LlmSessionObservation` projection (projector `LlmSessionObservationSessionEventProjector`,
  §5), which republishes them on the per-session hub stream that both the SSE observer and the
  executor's record-confirmation sink subscribe to. The session actor does not push to SSE
  directly.
- **Is the session actor the intended consumer of `Record*`?** **Yes** — `Record*` commands are
  dispatched to the session actor (`LlmRunExecutor.DispatchCommandAsync` targets
  `sessionActorId`, §2). The session actor is the single authority.
- **Then why does the sink's stream deliver to the execution grain instead?** Because the sink's
  `await foreach` is a SECOND, separate subscription. The `Record*` **write** goes to the session
  actor (correct); but the sink also **subscribes** to the session's observation hub stream
  **from the execution grain's context** to *confirm* the write came back, and THAT subscription's
  delivery lands on the execution grain (deadlock). The session actor and SSE path are innocent;
  the executor's self-confirmation subscription is what binds delivery to the blocked grain.
- **Durable crash finalizer (kept by the fix)**: `TryScheduleRunTimeoutAsync` (called at
  `:416`) → `FinalizeLlmRunTimedOut` → `HandleFinalizeLlmRunTimedOutAsync` (`:555-588`) commits a
  terminal `LlmRunFailed{run_timeout}` if no terminal arrives. **[HEAD]** the timeout falls back
  to `record.Ttl` (24h default) — effectively no fast safety net; **[worktree]** changes
  `ResolveRunTimeout` to a `DefaultRunExecutionTimeout = 10min` constant (`:29`, diff lines
  1617-1628), decoupled from session TTL. (R5 relevance.)

## 5. SSE / observation read side

- **Endpoint (chat completions)**: `ChatCompletionsApiEndpoints.HandleCreateChatCompletionAsync`
  (`ChatCompletionsEndpoints.cs:29`) → on a stream plan,
  `WriteStreamingChatCompletionAsync` (`:91-158`):
  - `await response.StartAsync` flushes the `200 text/event-stream` headers (`:104`) **before any
    terminal** — this is why a later hang yields a truncated 200 with no body terminal.
  - `await commandFacade.StreamAsync(plan, async (delta, token) => writeContentChunk, ct)` (`:106`).
  - On `delta.TextDelta` → `BuildStreamingContentChunk` + `WriteDataFrameAsync` (`:108-117`).
  - On completion → tool-calls snapshot chunk (if any), then **`BuildStreamingStopChunk` with
    `finish_reason` (`stop`/`tool_calls`)** (`:141-144`), optional usage chunk, then `[DONE]`
    (`:153`). **The terminal the client needs is this stop chunk + `[DONE]`, gated on
    `completion.Completion is not null`.** No terminal ⇒ truncated stream.
- **Facade stream**: `ResponsesCommandFacade.StreamAsync` (`:204-270`) /
  `ChatCompletionsCommandFacade.StreamAsync` delegate to
  `LlmSessionRunObservationService.ObserveAsync` (`LlmSessionRunObservationService.cs:14-152`):
  - Prepares + attaches its OWN `EventChannel` sink to the same `LlmSessionObservation` scope
    (`:36-58`), runs the dispatch lambda (`request.DispatchAsync` → seeds the run start, `:60`),
    then `await foreach (var envelope in sink.ReadAllAsync(observeCt))` (`:62`).
  - Feeds `LlmSessionRunObservationAccumulator` (`:61`): `LlmStreamChunkObserved` →
    `accumulator.ObserveChunk` → returns a `LlmSessionRunObservedDelta` → `onDelta` (`:72-78`)
    → SSE content chunk. `LlmRunCompleted` → `BuildCompletion()` returns the terminal
    `LlmSessionCompletionSnapshot` (`:86-91`). `LlmRunFailed`/`LlmRunCancelled` → error result
    (`:93-114`). Timeout (the configured 300s ingress wait) → `response_timeout` (`:124-132`).
- **Accumulator**: `LlmSessionRunObservationAccumulator` (`LlmSessionRunObservationAccumulator.cs:10-87`)
  accumulates `_outputText`/`_usage`/`_toolCalls`; `ObserveChunk` (`:18-31`) yields incremental
  deltas; `ObserveCompleted` (`:42-52`) snapshots final text+usage; `BuildCompletion` (`:54-66`)
  emits the terminal snapshot.
- **Client stream dependency**:
  - (a) **incremental chunks**: depend on committed `LlmStreamChunkObserved` events reaching the
    SSE observer's sink → `ObserveChunk` non-null delta → `onDelta` → `WriteDataFrameAsync`.
  - (b) **terminal**: depend on a committed `LlmRunCompleted` (→ `finish_reason=stop`/`tool_calls`
    stop chunk + `[DONE]`) or `LlmRunFailed`/`LlmRunCancelled` (→ error frame + `[DONE]`).
  - In the deadlock, the **executor never returns a record decision** (its self-confirmation
    subscription times out), so `LlmRunCore` cannot advance to commit `LlmRunCompleted`; thus the
    SSE observer's `await foreach` never sees a terminal and the client truncates.
- **Two independent subscriptions to one scope** (important for the fix): the SSE observer
  (`LlmSessionRunObservationService`, on the HTTP thread) and the executor's record-confirmation
  sink (`DispatchingLlmRunSink`, on the grain/worker) each `AttachExistingResponseProjectionAsync`
  to the SAME `(sessionActorId, responseId)` scope. Only the **executor's** subscription is bound
  to the blocked grain; the SSE observer's subscription runs on the request thread and is not the
  deadlock source.

## 6. Original design intent (vs what shipped)

- **`06-19-deblock-session-actor-llm-stream/design.md`** (epic #2271, the authoritative intent):
  - §"Executor mechanism" `:6-9`: *"Recommended: `ILlmRunExecutor` as a **DI service**
    (registered in the host), invoked off the request thread, **NOT an Orleans grain**. …
    Alternative (per-run executor grain) **rejected**: a grain blocked ~1m for one run is still a
    blocked grain + adds lifecycle/placement cost for no fact-ownership benefit."* — i.e. the
    shipped grain is **exactly the rejected alternative**.
  - The 14 codex gaps (`:55-69`) include #6 executor-crash finalizer, #4 cancellation must not be
    a mid-tier CTS dict, #12 "extract a shared core runner, don't duplicate" (→ `LlmRunCore` +
    two sinks: in-actor `InActorLlmRunSink` and `DispatchingLlmRunSink`), #14 stream-not-resumable
    rationale (#2276).
- **`06-21-offgrain-llm-run-executor/design.md`** (the realignment, codex verdict **GREEN**):
  - `:1-9`: *"The shipped implementation drifted: it introduced a per-run **execution grain**
    (`LlmRunExecutionGAgent`) that the design explicitly rejected, and runs the whole streaming
    loop inside that grain's event-handler turn. This task removes the grain and makes execution
    run off any grain turn, as the design intended."*
  - Target (`:40-52`): `LlmRunExecutionWorker (BackgroundService, NOT a grain)` dequeues →
    `ILlmRunExecutionService.ExecuteAsync` off ANY grain turn; *"When it blocks on
    `sink.ReadAllAsync`, it occupies a **thread-pool thread, not an Orleans turn**. The session
    actor and the projection pulling agent stay free to interleave the `Record*` turns and
    `DeliverBatch` deliveries."*
  - Final decisions (`:206-272`, supersede first draft): **bounded** non-blocking queue
    (`TryWrite` throws → `execution_dispatch_failed` terminal); short run-execution timeout
    (10min, decoupled from 24h TTL); queue+worker in Hosting, scheduler stays in Application;
    delete the vestigial `OffActorLlmRunExecutorEnabled` flag + collapse the convergent facade
    branches; keep `ExecuteLlmRunRequested` proto DEFINED for one rolling-deploy window.
  - Codex required an **Orleans `TestCluster` integration test** (mocks insufficient) asserting
    `ScheduleAsync` returns while a blocked fake executor runs, and the session actor still
    processes `Record*`/cancel while the worker is parked in `sink.ReadAllAsync` (`:255-266`).
- **Intent vs shipped, one line**: intent = "run is a DI service off any grain turn; session
  actor owns facts; observe committed events"; shipped (`45c1bd20`) = "run is a per-run grain
  whose blocked turn both drives the run and consumes the run's own observation stream" → the
  rejected design → self-deadlock.
- **`06-21-offgrain-llm-run-executor/implement.md`** is the step plan the working tree is
  executing (add queue+worker → short run-timeout → switch scheduler to enqueue → delete grain +
  provisioner → delete flag → tests → verify → merge to `feature/integrate`). Most of steps 1-3
  appear present in the worktree; flag deletion (step 4) is NOT done (flag + both facade branches
  still present in `ResponsesCommandFacade.cs`).

## 7. Off-turn boundary candidates (seams) — enumerate, do not design

Each seam = where the run loop lives, how `Record*` reaches the session actor without awaiting
self-delivery into an occupied turn, how per-chunk streaming stays live, and how
crash/timeout/cancel yields a terminal.

### Seam A — In-process `BackgroundService` worker queue (the WIP / codex-GREEN choice)
- **Run loop**: `LlmRunExecutionWorker : BackgroundService` (worktree
  `Hosting/Responses/LlmRunExecutionWorker.cs`) dequeues from `LlmRunExecutionQueue`
  (bounded `Channel`, non-blocking `TryWrite`) and calls `ILlmRunExecutionService.ExecuteAsync`
  on a thread-pool `Task` gated by a `SemaphoreSlim`.
- **Record* without self-delivery**: UNCHANGED sink code (`DispatchingLlmRunSink.DispatchAsync`)
  — but now `:298` attach + `:306` `await foreach` run on a **thread-pool thread**, so the
  Orleans stream subscription is no longer bound to a single blocked grain turn; `DeliverBatch`
  can interleave with the session actor's `Record*` turns. The `await foreach` blocks a
  thread-pool thread only.
- **Streaming stays live**: SSE observer path is untouched (§5); chunks flow as the session actor
  commits them.
- **Crash/timeout/cancel**: executor exception → `RecordLlmRunFailed(executor_failed)`
  (`LlmRunExecutor.cs:103,112-143`); host death / queued-but-never-run → session actor durable
  run-timeout (now 10min, `LlmSessionGAgent` worktree); queue full → `TryWrite==false` →
  `LlmRunExecutionQueueFullException` → caught in `TryDispatchTransientExecutionCommandAsync` →
  `execution_dispatch_failed` terminal; cancel → `/cancel` records a cancel fact → next
  `DispatchAsync` returns `Stop` → `LlmRunCore.ShouldStop` ends the loop.
- **Tradeoffs**: loses Orleans cross-silo placement/addressability of the run (every silo hosting
  `LlmSessionGAgent` must register the worker; concurrency gate is per-host, not cluster-wide).
  Acceptable per design (executor owns no fact state; cancel is an actor fact; no durable resume).
  **This seam is largely already implemented in the working tree** — the task is to verify/finish
  + test it, not invent it.

### Seam B — Eliminate the per-record self-confirmation subscription entirely
- **Observation**: the deadlock's proximate cause is that `DispatchingLlmRunSink.DispatchAsync`
  **re-attaches a hub-stream subscription per record and waits to see its own write echo back**
  (`LlmRunExecutor.cs:298,306`). Even off-grain (Seam A), this keeps a per-record
  attach/detach + cross-stream round-trip on the hot path (a 64-cap `EventChannel` + lease
  prepare/release **per chunk**).
- **Run loop**: same off-turn worker, but the sink returns its `LlmRunRecordDecision` from the
  session actor's **dispatch ACK / a direct decision contract** instead of by subscribing to the
  observation stream. (The session actor already computes accept/stop in `DecideRunRecord` /
  `CanPersistRunFact` — `LlmSessionGAgent.cs:1073-1095,1213-1251`.)
- **Record* without self-delivery**: the executor dispatches `Record*` and reads the decision
  from the command result, never subscribing to the session's hub stream. Removes the
  producer/consumer collapse at its root rather than relocating it.
- **Streaming stays live**: unchanged SSE observer path.
- **Crash/timeout/cancel**: same as Seam A (executor catch + actor run-timeout + cancel fact).
- **Tradeoffs**: needs a decision-returning dispatch contract (the deployed `IActorDispatchPort`
  returns `DispatchAdmission`, not a per-record decision) — a larger contract change; touches the
  hot path; would obviate the projection ports inside the executor (currently
  `ILlmSessionObservationScopeLeasePreparationPort` / `ILlmSessionObservationProjectionPort` are
  used by both the executor sink AND the SSE observer). Higher blast radius; not what the GREEN
  design chose, but it is the only seam that removes the self-confirm round-trip rather than
  moving it off-turn.

### Seam C — Session-actor self-continuation (eventized small steps), no external executor
- **Run loop**: the session actor drives the loop in small self-messaged steps
  (self-continuation events into its own inbox), each turn doing one provider step + persisting
  one record, then yielding. Explicitly **excluded** by the #2271 design (gap #14,
  `06-19-deblock-session-actor-llm-stream/design.md:69`) because C# `await foreach` streaming
  state cannot be parked/resumed across Orleans turns without re-issuing the upstream call.
- **Record* without self-delivery**: records are persisted in-actor directly (no dispatch, no
  stream wait) — the in-actor `InActorLlmRunSink` already exists (`LlmSessionGAgent.cs:1253-1279`).
- **Streaming stays live**: would require buffering provider chunks across turns or re-streaming.
- **Crash/timeout/cancel**: native to the actor (durable timeout, cancel fact).
- **Tradeoffs**: re-streaming/large-state-across-turns is the reason it was rejected; listed for
  completeness only.

### Seam D — Keep an execution grain but make its handler return the turn (reentrant / fire-then-continuation)
- **Run loop**: still a grain, but `HandleExecuteAsync` must NOT `await` the whole run on the
  turn — e.g. mark the grain `[Reentrant]` (so `DeliverBatch` interleaves) **or** kick the run on
  a background `Task` and self-message continuations. Marking the existing grain reentrant is the
  smallest deployed-shaped change.
- **Record* without self-delivery**: unchanged sink; reentrancy lets the grain's own stream
  delivery interleave with the blocked-ish handler.
- **Streaming stays live**: unchanged SSE observer path.
- **Crash/timeout/cancel**: executor catch + actor run-timeout.
- **Tradeoffs**: contradicts the design's "no per-run grain" decision (`删除优先`, no
  fact-ownership benefit); reentrancy on a grain that runs arbitrary tool code is risky
  (re-entrancy hazards, lifecycle/deactivation mid-run, background-`Task`-in-grain anti-pattern
  per CLAUDE.md "回调只发信号"). Lowest code churn, weakest architectural fit.

---

## Files map (anchors)

| File | Role | Key lines |
|---|---|---|
| `src/platform/Aevatar.GAgentService.Core/GAgents/LlmRunExecutionGAgent.cs` **[HEAD; DELETED in worktree]** | The deadlocking per-run grain | `:22-36` `HandleExecuteAsync` returns `ExecuteAsync` on the turn |
| `src/platform/Aevatar.GAgentService.Application/Responses/LlmRunExecutor.cs` | Executor (impl of `ILlmRunExecutionService`/`ILlmRunExecutor`) + `DispatchingLlmRunSink` | `:63-110` `ExecuteAsync`; `:279-345` `DispatchAsync` (attach `:298`, await `:306`); `:27-61` `StartAsync` (only seeds start); `:145-163` `DispatchCommandAsync` (→ session actor) |
| `src/platform/Aevatar.GAgentService.Application/Responses/LlmRunCore.cs` | The shared LLM loop driver | `:57-193` `RunLlmLoopAsync`; `:101-128` stream + `:120` `RecordStreamChunkObservedAsync`; `MaxToolRounds=8` `:20` |
| `src/platform/Aevatar.GAgentService.Abstractions/Responses/LlmRunCoreContracts.cs` | `ILlmRunExecutionService`/`Scheduler`/`Request`/`Sink`/`Decision` (+`Queue` in worktree, `TargetProvisioner` in HEAD) | `:55-67`; worktree `:62-88` adds `ILlmRunExecutionQueue`+`Full` exception; HEAD has `ILlmRunExecutionTargetProvisioner` |
| `src/platform/Aevatar.GAgentService.Application/Responses/LlmRunExecutionScheduler.cs` | Hand-off seam (`ScheduleAsync`) | **[HEAD]** provisions grain + dispatches `ExecuteLlmRunRequested`; **[worktree]** `queue.Enqueue(request)` |
| `src/platform/Aevatar.GAgentService.Infrastructure/Activation/LlmRunExecutionTargetProvisioner.cs` **[HEAD; DELETED in worktree]** | Grain provisioner | `:19-24` `CreateByKindAsync` + actorId pattern |
| `src/platform/Aevatar.GAgentService.Hosting/Responses/LlmRunExecutionQueue.cs` **[worktree, NEW]** | Bounded `Channel` hand-off queue | `:30-38` non-blocking `TryWrite` → throws on full |
| `src/platform/Aevatar.GAgentService.Hosting/Responses/LlmRunExecutionWorker.cs` **[worktree, NEW]** | `BackgroundService` that runs the loop off any grain turn | `:37-59` drain; `:65-80` `RunOneAsync` → `ExecuteAsync` |
| `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCommandFacade.cs` | Ingress facade; flag branch | `:51-52` flag; `:221-223`/`:523-525` branch; `:965-971` `StartOffActorRunAsync`; `:948-963` `DispatchRunAsync`; `:989-1015` `BuildRunRequested` (`runId=…:llm-run`) |
| `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesIngressOptions.cs` | `OffActorLlmRunExecutorEnabled=true` default; `ObservationTimeout=300s` | `:43`, `:29-32` |
| `src/platform/Aevatar.GAgentService.Core/GAgents/LlmSessionGAgent.cs` | Fact owner; `Record*` handlers; hand-off; durable run-timeout; `InActorLlmRunSink` | `:34-40` ctor; `:425,429-465` hand-off; `:339-553` `Record*`; `:555-588` timeout finalize; `:1253-1279` in-actor sink; worktree run-timeout `:29`+diff 1617-1628 |
| `src/platform/Aevatar.GAgentService.Application/Responses/LlmSessionRunObservationService.cs` | SSE-side observer | `:36-58` attach; `:60` dispatch; `:62-122` `await foreach` → accumulator → terminal |
| `src/platform/Aevatar.GAgentService.Application/Responses/LlmSessionRunObservationAccumulator.cs` | SSE delta/terminal accumulation | `:18-31` `ObserveChunk`; `:54-66` `BuildCompletion` |
| `src/platform/Aevatar.GAgentService.Abstractions/Ports/ILlmSessionObservationProjectionPort.cs` | `AttachExistingResponseProjectionAsync` contract | `:13-21` |
| `src/platform/Aevatar.GAgentService.Projection/Orchestration/LlmSessionObservationProjectionPort.cs` | Attaches live sink to session scope | `:29-61` |
| `src/platform/Aevatar.GAgentService.Projection/Orchestration/LlmSessionObservationScopeLeasePreparationPort.cs` | Ensures the projection scope | `:20-54` `EnsureAsync` |
| `src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs` | `AttachLiveSinkAsync` → hub `SubscribeAsync` | `:35-60` |
| `src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs` | Orleans stream pub/sub keyed `{channel}:{rootActorId}:{sessionId}` | `:38-61` publish; `:67-117` subscribe; `:119-120` stream id |
| `src/platform/Aevatar.GAgentService.Projection/Orchestration/LlmSessionObservationSessionEventHub.cs` | Session hub for LLM observation | `:20-32` |
| `src/platform/Aevatar.GAgentService.Projection/Projectors/LlmSessionObservationSessionEventProjector.cs` | Republishes committed run events to the hub (filters by `CorrelationId==SessionId`) | `:19-52` |
| `src/platform/Aevatar.GAgentService.Projection/DependencyInjection/ServiceCollectionExtensions.cs` | Wires the `LlmSessionObservation` event-sink projection runtime (`ProjectionSessionScopeGAgent`) | `:146-157`, `:249-251` |
| `src/platform/Aevatar.GAgentService.Hosting/DependencyInjection/ServiceCollectionExtensions.cs` | DI; **[worktree]** adds queue+worker / drops provisioner | `:85-99` (diff: `-ILlmRunExecutionTargetProvisioner`, `+ILlmRunExecutionQueue`, `+AddHostedService<LlmRunExecutionWorker>`) |
| `src/Aevatar.Mainnet.Host.Api/ChatCompletions/ChatCompletionsEndpoints.cs` | SSE writer (`finish_reason`/`[DONE]`) | `:91-158`; headers flush `:104`; stop chunk `:141-144`; `[DONE]` `:153` |
| `src/platform/Aevatar.GAgentService.Abstractions/Protos/llm_sessions.proto` | `ExecuteLlmRunRequested` (only the proto references it now) | `:259` |

## Related design/intent docs

- `.trellis/tasks/06-19-deblock-session-actor-llm-stream/design.md` — epic #2271 intent: executor
  is a **DI service, not a grain**; per-run grain explicitly rejected (`:6-9`); 14 codex gaps
  (`:55-69`); stream-not-resumable rationale (#2276) at `:69`.
- `.trellis/tasks/06-21-offgrain-llm-run-executor/design.md` — codex **GREEN** realignment to
  remove the grain; bounded non-blocking queue; off-grain `BackgroundService`; short run-timeout;
  rolling-deploy proto note; integration-test requirement (`:206-292`).
- `.trellis/tasks/06-21-offgrain-llm-run-executor/implement.md` — the step plan the working tree
  is executing.
- `docs/canon/llm-streaming.md`, `docs/canon/nyxid-responses-direct.md` — mention the off-grain /
  off-actor executor (not yet re-read in full; flagged for the designer).

## Caveats / Not found

- I did **not** open Orleans' `DeliverBatch` source — it is framework code, not in-repo. The
  "subscription delivery runs on the subscribing grain's context" is inferred from
  `ProjectionSessionEventHub.SubscribeAsync` calling `stream.SubscribeAsync` from the executor
  grain's turn + the PRD's log evidence (consumer id `…:llm-run` = the execution grain). Confirm
  during design if the exact Orleans delivery-context guarantee matters.
- `EventChannel<EventEnvelope>` and `IStreamProvider`/`GetStream`/`SubscribeAsync` internals not
  read; treated as the in-repo stream abstraction.
- The worktree files were read as their working-tree content; committed (deployed) versions of the
  6 changed files were read via `git show HEAD:`. Line numbers tagged **[HEAD]** vs **[worktree]**
  accordingly. A few `LlmSessionGAgent` anchors use worktree line numbers where HEAD/worktree are
  contract-identical (handlers); the only behavioral HEAD/worktree delta in that file is
  `ResolveRunTimeout`.
- The full `LlmSessionGAgent.cs` is 1672 lines; I read `:1-1313` plus the targeted run-timeout
  diff. Helpers below `:1313` (`TryScheduleRunTimeoutAsync`, `ResolveRunTimeout`,
  callback-id builders) were confirmed via the diff, not a full read — re-read if the timeout
  wiring is load-bearing for the fix.
- `docs/canon/llm-streaming.md` / `nyxid-responses-direct.md` were located but not fully read;
  scan them in design for any additional intent/constraints.
