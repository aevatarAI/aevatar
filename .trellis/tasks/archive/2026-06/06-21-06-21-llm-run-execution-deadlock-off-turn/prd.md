# Run off-actor LLM execution truly off grain turn (fix self-deadlock)

## Problem

On deployed `feature/integrate@45c1bd20`, both `/v1/chat/completions` and `/v1/responses`
hang ~60s and return no usable reply. The off-actor LLM execution grain
`LlmRunExecutionGAgent` (kind `gagent.service.llm-run-execution`, introduced by #2298)
self-deadlocks:

- `HandleExecuteAsync` (`[EventHandler]`) does `return _executionService.ExecuteAsync(...)`
  → holds the grain's single, non-reentrant turn for the entire ~60s run.
- Inside the run, `LlmRunExecutor.DispatchAsync` dispatches each `Record*` then
  `await foreach sink.ReadAllAsync(...)` — waiting for the record to be delivered back via
  the Orleans stream whose consumer is the **same** grain.
- `DeliverBatch` can never get a turn → 30s Orleans timeout
  (`Failed to deliver message to consumer` / `Response did not arrive on time in 00:00:30`)
  → no terminal record → the already-flushed `200 text/event-stream` never completes.

Evidence (live logs, pod `45c1bd20`, 2026-06-21): the only `/v1/chat/completions` in 2h =
`Request finished … 200 … text/event-stream … 59979ms`, plus `Failed to deliver message to
consumer … :chatcmpl_…:llm-run` and the 30s `TimeoutException` on `DeliverBatch`; 4
concurrent runs all stuck. Related: epic #2271 (OPEN), bug #2268 (OPEN). The on/off
feature flag (`OffActorLlmRunExecutorEnabled`) is a no-op — both facade paths funnel into
this grain.

## Goal

Execute the LLM run truly off any grain turn so the executing entity never blocks the turn
that must deliver its own records / stream signals — restoring terminal delivery and
end-to-end replies on both ingresses, while keeping the session actor the single authority
for run lifecycle and state.

## Requirements

- **R1 — Break the cycle.** The LLM run loop must not execute inside a single Orleans grain
  turn that is also the consumer of the run's record/observation stream.
- **R2 — Records without self-delivery.** `Record*` persistence (started / chunk / completed
  / failed / cancelled) must reach the session actor (the fact owner) without the executor
  awaiting delivery back into an occupied grain turn.
- **R3 — Actor stays authority.** The session actor remains the owner of run lifecycle and
  state; the off-turn worker only signals via events/commands. No direct state mutation from
  background callbacks (CLAUDE.md: "回调只发信号", "单线程事实源").
- **R4 — Streaming preserved.** Per-chunk signals continue to drive the SSE accumulator so
  the client receives incremental output and a terminal (`response.completed` /
  `finish_reason=stop`).
- **R5 — Crash / timeout / cancel safety.** If the off-turn run dies or times out, a terminal
  failure record must still be produced (no silent hang / lost terminal). Client disconnect
  cancels the run.
- **R6 — Both ingresses, no non-streaming regression.** `/v1/chat/completions`, `/v1/responses`
  (and `/v1/messages` if it shares the path) all go through the fixed path; the non-streaming
  path is unaffected.
- **R7 — Verifiable.** Behavior change covered by xUnit + FluentAssertions tests; no
  `Task.Delay`/`WaitUntil` hacks outside the allowlist; architecture/test guards pass.

## Constraints

- Hot path (per-request execution model) → **design-first, codex review before
  implementation** (explicit user directive).
- Honor CLAUDE.md actor/execution rules: single-threaded fact source; self-continuation
  eventized; delays/timeouts eventized (async wait → publish internal event → actor consumes);
  callbacks only signal; no middle-tier process-state-as-fact; Protobuf for state/events/
  commands.
- Prefer the original design intent (run as a DI-scoped service, not a per-run grain that
  blocks its own turn) where it fits the architecture.
- Upgrade-forward is acceptable: old in-flight runs may keep the old path; no hot state
  migration required.

## Acceptance Criteria

- [ ] **AC1** — A streamed `/v1/chat/completions` request on the fixed build returns
  incremental chunks and a terminal completion; no `DeliverBatch` / 30s-timeout in logs for
  the run's grain.
- [ ] **AC2** — Same for `/v1/responses`.
- [ ] **AC3** — No grain turn is held for the duration of the LLM run (verified by design
  review + a test asserting the executor returns its turn promptly and records flow without
  the self-delivery deadlock).
- [ ] **AC4** — A simulated run crash/timeout still yields a terminal failure record (no
  infinite hang).
- [ ] **AC5** — Client disconnect cancels the run (no orphaned work).
- [ ] **AC6** — Non-streaming path unaffected; existing `LlmRunExecutor` / `LlmRunCore` /
  session-actor tests stay green; guards pass.
- [ ] **AC7** — `design.md` reviewed by codex with no blocking gaps before implementation.

## Out of scope

- Token/usage accuracy and pricing (separate workstream).
- Broader projection/readmodel redesign.
- Scheduled-dispatch issues #2284/#2285 in the rollup worktree (unrelated).
