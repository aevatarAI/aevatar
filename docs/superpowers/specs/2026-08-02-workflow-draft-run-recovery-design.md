# Workflow Draft-Run Recovery Design

## Context

`ChannelWorkflowDraftRunGAgent` currently commits `ChannelWorkflowDraftRunStartedEvent` and then starts a transient interaction observer. The observer runs in process memory and reports frames and completion back to the run actor. A process restart after the `Started` commit loses that observer. The actor rehydrates as `Started`, ignores the deterministic start command as a duplicate, and has no durable continuation or terminal deadline.

The durable workflow run actor remains the authority for workflow execution. The missing authority is the channel draft-run handoff: no durable owner decides how a stranded `Started` draft-run reaches a terminal state.

## Constraints

- Raw relay reply tokens and NyxID user access tokens remain runtime-only and must not enter event store, actor state, or durable callback payloads.
- Restart recovery must not redispatch the workflow command. A deterministic command ID alone does not prove that every downstream effect is safe to repeat.
- All state transitions occur in the draft-run actor turn. Background interaction code may only publish typed observations back to the actor.
- Timeout recovery must use the runtime's durable callback scheduler and a typed protobuf signal.
- A recovery deadline that depends on a relay reply token must leave time to hand the terminal reply off before that token expires.
- Terminal output must be committed before actor-inbox admission. A stable operation ID preserves delivery lineage across retries; it neither suppresses retransmission nor replaces a durable outbox.
- Normal completion and timeout recovery must produce one immutable terminal payload and may retry its idempotent handoff.

## Considered Approaches

### 1. Durable recovery deadline with fail-closed timeout

Persist a scrubbed recovery request and an absolute recovery deadline in the `Started` event/state. Schedule a deterministic durable self-timeout after the commit. On activation and duplicate start, reconcile the same deadline and schedule it again if necessary. When the timeout fires, the actor emits the existing typed terminal failure carrier and commits `Failed`. Normal completion and timeout are serialized by actor state, so only the first terminal path performs a handoff.

This is the selected approach. It closes the restart hole without replaying a workflow or persisting credentials. The transient observer is no longer the sole terminal owner: it can only publish frames/completion, while the actor-owned durable deadline guarantees a terminal decision.

### 2. Resume observation from a persisted workflow receipt

Persist the accepted workflow actor ID and command receipt, then add an observe-only application port that can attach to an existing workflow run or read its terminal current-state model. This can recover a successful result after restart, but it requires a new cross-layer observation contract and careful projection freshness semantics. It is a valid future enhancement, but it is larger than the blocker and unnecessary for guaranteed termination.

### 3. Re-run the deterministic start command

Persist or recover the original request and call `ExecuteAsync` again. This would require durable secret references for both relay credentials and still cannot prove that every workflow side effect is idempotent. This approach is rejected.

## State And Messages

`ChannelWorkflowDraftRunStartedEvent` and `ChannelWorkflowDraftRunGAgentState` gain:

- `recovery_request`: a cloned `NeedsWorkflowDraftRunEvent` with `reply_token`, `reply_token_expires_at_unix_ms`, `nyx_user_access_token`, and `activity.transport_extras.nyx_user_access_token` cleared.
- `recovery_deadline_unix_ms`: an absolute UTC deadline derived once when `Started` is committed. It is the earlier of the 30-minute recovery limit and one minute before reply-token expiry; an already elapsed credential bound is clamped to the start time for immediate durable timeout handling.
- encrypted runtime-secret references for reply and user credentials; raw credentials remain cleared.

Terminal delivery adds a second durable phase:

- `ChannelWorkflowDraftRunTerminalProducedEvent` commits the complete scrubbed `LlmReplyReadyEvent` and stable operation ID before dispatch.
- `ChannelWorkflowDraftRunStatus.TerminalProduced` means that the immutable terminal payload is pending handoff or final-state persistence.
- `ChannelWorkflowDraftRunTerminalHandoffRetryElapsed` carries only run, correlation, and operation IDs. Activation and duplicate starts re-arm this callback and replay the committed payload.
- `ReplyHandedOff` or `Failed` is committed only after the target actor inbox accepts the terminal envelope.

Raw relay credentials are stored in `IRuntimeSecretStore`. The persisted workflow request and terminal reply carry typed `RuntimeSecretReference` fields. `ConversationGAgent` resolves those references only while performing outbound delivery.

The new `ChannelWorkflowDraftRunRecoveryTimeoutElapsed` self-signal carries only `run_id`, `correlation_id`, and `recovery_deadline_unix_ms`. It contains no content or credential fields.

The callback ID is deterministic per run. Rescheduling is safe: an early or duplicate callback rechecks the persisted deadline, and a callback after terminal state is a no-op.

## Runtime Flow

1. Validate the start request and commit `Started` together with the scrubbed recovery request and deadline.
2. Schedule the durable recovery timeout. For relay requests, the deadline reserves a one-minute terminal-handoff window before reply-token expiry; requests without an explicit expiry retain the 30-minute recovery limit.
3. Start the transient workflow interaction observer. It may only dispatch typed frame/completion messages to the run actor.
4. On duplicate start for the same active run, do not start another interaction; reconcile the durable timeout.
5. On activation in `Started`, reconcile the durable timeout. If the deadline is already elapsed, enqueue the typed durable self-timeout with the minimum supported delay so the normal actor handler performs the transition.
6. On timeout, verify status, run ID, correlation ID, and deadline against actor state. If still active, produce `LlmReplyReadyEvent` with error code `workflow_draft_run_recovery_timeout`.
7. Commit the immutable scrubbed terminal reply and operation ID as `TerminalProduced`.
8. Arm the deterministic terminal-handoff retry before attempting actor-inbox admission.
9. Dispatch the committed payload. If admission fails, remain `TerminalProduced`; activation or the typed retry replays the same payload and operation ID.
10. If admission succeeds, commit `ChannelWorkflowDraftRunReplyHandedOffEvent` or `ChannelWorkflowDraftRunFailedEvent`. If this append fails, remain `TerminalProduced` and safely replay the already admitted payload.
11. Only after the final event commits, best-effort purge the actor's durable callbacks.

Terminal conversation envelopes use a stable operation ID derived from the run ID. The durable outbox supplies restart recovery, while the operation ID preserves delivery lineage across retries. The transport may redeliver; `ConversationGAgent` absorbs a replay through its persisted terminal correlation state.

## Error Handling

- Missing recovery context in a legacy `Started` state is treated as an already-stranded run and fails closed with `workflow_draft_run_recovery_context_missing`.
- Durable callback scheduling errors are not hidden. The actor remains `Started`; activation or duplicate start can repair scheduling later.
- Terminal admission failures and post-admission final-append failures leave the committed outbox pending and do not purge callbacks.
- Callback purge is best effort and logged. Persisted final state remains authoritative if cleanup fails.
- Late frames, completion messages, and timeout signals after a terminal transition are ignored.

## Verification

- A deterministic test commits `Started`, recreates the actor from the same event store before interaction completion, fires the re-established durable timeout, and verifies one terminal failure handoff with no second interaction start.
- Tests verify persisted recovery state and durable callback payloads contain no raw credentials.
- Tests verify duplicate active start repairs the timeout without redispatching workflow execution.
- Tests verify a late completion after recovery timeout does not produce a second terminal handoff.
- Fault-injection tests verify dispatch admission failure and post-admission final append failure both rehydrate as `TerminalProduced`, replay byte-identical payloads with the same operation ID, and never restart workflow execution.
- Tests verify terminal outbox state contains secret references rather than raw reply or NyxID credentials, and `ConversationGAgent` resolves those references only at delivery time.
- A fake-clock test advances to the credential-bounded recovery deadline, verifies the timeout fires one minute before reply-token expiry, and resolves the persisted secret reference at the terminal handoff boundary.
- Run the channel runtime test project, test stability guard, workflow binding boundary guard, architecture guards, full solution build, and full solution test.
