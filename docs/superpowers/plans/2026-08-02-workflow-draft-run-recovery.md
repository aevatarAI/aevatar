# Workflow Draft-Run Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Guarantee that a persisted channel workflow draft-run reaches one terminal state after process restart without persisting credentials or replaying workflow side effects.

**Architecture:** The draft-run actor persists a scrubbed recovery continuation and absolute deadline with `Started`. Timeout and normal completion first commit one immutable terminal outbox payload, then retry actor-inbox admission with a stable operation ID, and only then commit the final state.

**Tech Stack:** .NET 10, C#, protobuf, event-sourced GAgents, `IActorRuntimeCallbackScheduler`, xUnit, FluentAssertions.

## Global Constraints

- Raw relay reply tokens and NyxID user access tokens remain runtime-only.
- Recovery never redispatches the workflow command.
- Recovery reserves a one-minute terminal-handoff window before an explicit relay reply-token expiry.
- Background interaction code only publishes typed messages to the actor.
- Every behavior change follows RED, GREEN, REFACTOR.

---

### Task 1: Prove the restart failure boundary

**Files:**
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelWorkflowDraftRunTests.cs`

**Interfaces:**
- Consumes: `ChannelWorkflowDraftRunGAgent.ActivateAsync`, shared `IEventStore`, `IActorRuntimeCallbackScheduler`.
- Produces: deterministic restart, credential-scrubbing, duplicate-repair, and terminal-race tests.

- [ ] **Step 1: Add a shared-event-store actor factory and recording callback scheduler**

Extend `CreateWorkflowDraftRunAgentAsync` with optional `IEventStore`, `IActorRuntimeCallbackScheduler`, and `TimeProvider` arguments. Register the chosen scheduler in the service provider. The scheduler records `RuntimeCallbackTimeoutRequest` objects and returns monotonically increasing in-memory leases.

- [ ] **Step 2: Write the restart-boundary test**

Start an actor with a recording interaction port, assert `Started`, then create a second actor with the same ID and event store. Assert activation schedules a typed recovery timeout without starting another interaction. Advance a `FakeTimeProvider`, invoke the recorded timeout signal, and assert exactly one failed `LlmReplyReadyEvent` plus `Failed` actor state.

- [ ] **Step 3: Add credential and race assertions**

Assert the persisted recovery request and callback payload contain no reply or NyxID credentials. Deliver a late interaction completion after timeout and assert the dispatch count remains one.

- [ ] **Step 4: Run the focused test and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --filter 'FullyQualifiedName~ChannelWorkflowDraftRunTests'
```

Expected: compile or assertion failure because recovery state, timeout message, activation scheduling, and timeout handler do not exist.

### Task 2: Add the durable recovery contract and actor behavior

**Files:**
- Modify: `agents/Aevatar.GAgents.NyxidChat/protos/agent_run.proto`
- Modify: `agents/Aevatar.GAgents.NyxidChat/WorkflowDraftRun/ChannelWorkflowDraftRunGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/WorkflowDraftRun/ChannelWorkflowDraftRunInteractionPort.cs`

**Interfaces:**
- Consumes: `ScheduleSelfDurableTimeoutAsync`, `IActorRuntimeCallbackScheduler.PurgeActorAsync`, `NeedsWorkflowDraftRunEvent`.
- Produces: `ChannelWorkflowDraftRunRecoveryTimeoutElapsed`, persisted `RecoveryRequest`, and `RecoveryDeadlineUnixMs`.

- [ ] **Step 1: Extend the protobuf contract**

Add `recovery_request` and `recovery_deadline_unix_ms` to the started event and actor state. Add the typed timeout signal with stable identity fields only.

- [ ] **Step 2: Persist a scrubbed recovery continuation**

Clone the request, clear all raw credential fields including nested transport credentials, compute the deadline once, and include both values in `ChannelWorkflowDraftRunStartedEvent`. Use the earlier of the 30-minute recovery limit and one minute before reply-token expiry; clamp an already elapsed credential bound to the start time so the durable callback fires immediately.

- [ ] **Step 3: Reconcile the durable callback**

After the `Started` commit, on duplicate active start, and from `OnActivateAsync`, schedule the deterministic self-timeout for the remaining duration. If the deadline already elapsed, enqueue it with the minimum supported delay so the normal actor handler performs the transition.

- [ ] **Step 4: Implement the serialized timeout transition**

Validate status and stable identity against actor state. Dispatch `workflow_draft_run_recovery_timeout`, commit `Failed`, and purge callbacks best effort. Make normal terminal paths purge callbacks too.

- [ ] **Step 5: Stabilize the terminal envelope identity**

Use one operation ID derived from the run ID for terminal `LlmReplyReadyEvent` dispatch. Keep stream chunks independently identified.

- [ ] **Step 6: Run the focused tests and verify GREEN**

Run the Task 1 test command. Expected: all `ChannelWorkflowDraftRunTests` pass with zero failures.

### Task 3: Verify the change and prepare review

Before verification, close the independent-review blocker with a durable terminal outbox:

- [ ] Add `TerminalProduced`, a persisted scrubbed `LlmReplyReadyEvent`, stable operation ID, and typed terminal-handoff retry signal.
- [ ] Persist the terminal payload before dispatch and arm the retry before admission.
- [ ] On dispatch failure or post-admission final append failure, remain pending and replay the same payload after activation.
- [ ] Store relay credentials only as encrypted `RuntimeSecretReference` values and resolve them in `ConversationGAgent` at delivery time.
- [ ] Add deterministic fault-injection tests for both failure windows and credential-reference tests for the Conversation boundary.
- [ ] Add a fake-clock test that reaches the credential-bounded timeout, produces the terminal reply before token expiry, and resolves its runtime-secret reference at the handoff boundary.

**Files:**
- Verify: all files changed since `origin/feature/integrate`.

**Interfaces:**
- Consumes: repository build/test/guard scripts.
- Produces: reviewable commit with fresh verification evidence.

- [ ] **Step 1: Run mandatory focused guards**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
```

Expected: both commands exit 0.

- [ ] **Step 2: Run architecture and full solution verification**

```bash
bash tools/ci/architecture_guards.sh
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
```

Expected: all commands exit 0 and tests report zero failures.

- [ ] **Step 3: Commit the scoped change**

```bash
git add docs/superpowers/specs/2026-08-02-workflow-draft-run-recovery-design.md \
  docs/superpowers/plans/2026-08-02-workflow-draft-run-recovery.md \
  agents/Aevatar.GAgents.NyxidChat/protos/agent_run.proto \
  agents/Aevatar.GAgents.NyxidChat/WorkflowDraftRun/ChannelWorkflowDraftRunGAgent.cs \
  agents/Aevatar.GAgents.NyxidChat/WorkflowDraftRun/ChannelWorkflowDraftRunInteractionPort.cs \
  test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelWorkflowDraftRunTests.cs
git commit -m 'Recover stranded workflow draft runs'
```

- [ ] **Step 4: Request independent review**

Review the commit range from the original `origin/feature/integrate` baseline to `HEAD` against issue #3043 and the design spec. Fix all Critical and Important findings, then rerun affected checks.

- [ ] **Step 5: Push, open the PR, and wait for CI**

Push `fix/2026-08-02_resume-workflow-draft-runs`, open a ready PR targeting `feature/integrate`, wait for every required GitHub check, and merge only after all checks succeed.
