# NyxID Chat First-Turn Orleans Scheduler Recovery Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans and superpowers:verification-before-completion.

**Goal:** Make a newly accepted NyxID conversation link and complete its first turn on the production Orleans runtime, then prove the deployed API returns text `OK` and `RUN_FINISHED`.

**Architecture:** Preserve the existing `NyxIdChatConversationGAgent -> IActorRuntime.CreateAsync/LinkAsync -> NyxIdChatTurnGAgent` trunk. Actor handlers must remain on the Orleans activation scheduler, so the fix removes `ConfigureAwait(false)` from the two NyxID actor classes instead of weakening the shared Orleans runtime. Transcript, SSE, projection, and runtime contracts remain unchanged.

**Tech Stack:** .NET 10, C#, Orleans 10, xUnit, FluentAssertions.

## Constraints

- Keep `Domain / Application / Infrastructure / Host` layering and runtime-neutral ports.
- Do not add actor maps, alternate transcript paths, query-time priming, retries, timeouts, or new abstractions.
- Parent/child topology and both relay bindings must exist before first-operation dispatch.
- Do not change `OrleansActorRuntime` to tolerate off-activation state access.
- Modified tests must pass `bash tools/ci/test_stability_guards.sh`.

## Root-Cause Evidence

- Production image `aevatar-console-backend:efe5249b` committed `NyxIdChatTurnStartedEvent`, created the turn actor, then logged `NYXID_CHAT_TURN_ACTOR_LINK_FAILED` with `InvalidOperationException`; no `NyxIdChatOperationDispatchedEvent` followed.
- A local real-`RuntimeActorGrain` reproduction performed parent event commits, child creation, then `LinkAsync` after `ConfigureAwait(false)`. Orleans threw `Activation access violation. A non-activation thread attempted to access activation services.` from `StateStorageBridge<T>.State` through `OrleansActorRuntime.LinkAsync`.
- The working workflow create/link path does not suppress the Orleans synchronization context.
- Bypassing the link fast path would only hide the first state access; later event/state persistence would still be outside the activation boundary.

## Implementation

### Task 1: Add a real Orleans first-turn regression

**Files:**

- `test/Aevatar.Integration.Tests/NyxIdChatOrleansFirstTurnIntegrationTests.cs`
- `test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj`

- [x] Enter `NyxIdChatConversationGAgent` through a real `RuntimeActorGrain` handler.
- [x] Exercise real conversation/turn actor creation, linking, bidirectional relay, dispatch, and event-store commits.
- [x] Use distinct IDs and a fixed executor returning `OK` after an asynchronous boundary.
- [x] Assert terminal history is `Completed` with `OK`, execution occurs once, topology/relays exist, and both actors commit their dispatch/reconcile/delivery events.

Kafka/Garnet variants were removed: both the new variant and the repository's existing Kafka provider integration test timed out before inbound delivery, so that fixture cannot isolate this change. The retained test uses the same real Orleans activation and persistence bridges required to reproduce the exception; production Kafka/Garnet are covered by the final deployed smoke.

### Task 2: Keep NyxID actor turns on the activation scheduler

**Files:**

- `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs`
- `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTurnGAgent.cs`

- [x] Remove all `ConfigureAwait(false)` calls from both actor classes.
- [x] Make no shared runtime, protocol, state, or API behavior change.
- [x] Prove the regression with a conversation create/link mutation: restored `ConfigureAwait(false)` produced terminal `Failed`; removing it produced 1/1 passed; reintroducing it failed again; final restoration passed again.

A separate mutation on the turn executor await remained green and is not counted as evidence. The regression's proven scope is the production failure boundary: conversation actor create/link and the complete first-turn outcome.

### Task 3: Verify, review, integrate, and deploy

- [x] Run focused NyxID actor, history/SSE, Studio projection, and Orleans forwarding tests.
- [x] Run all required guards, docs lint, `git diff --check`, full build, and full solution tests.
- [x] Complete a read-only review and resolve every Critical/Important finding. Independent reviewer dispatch was unavailable, so the primary agent verified both production files by exact token comparison against the baseline with only `ConfigureAwait(false)` removed, then audited the integration test and project wiring; no Critical/Important finding remained.
- [ ] Commit, fetch and merge the latest `origin/feature/integrate`, then re-run verification on the exact merge tree.
- [ ] Push with `git push origin HEAD:feature/integrate`; never force push.
- [ ] Wait until the production pod image contains the merge commit.
- [ ] Create a temporary production conversation, verify it lists with empty history, stream `Reply with exactly: OK`, require text `OK` plus `RUN_FINISHED`, verify appended user/assistant history, and delete the conversation.

## Acceptance

- No `ConfigureAwait(false)` remains in either NyxID actor class.
- The committed merge tree passes repository verification.
- Production returns `OK` and `RUN_FINISHED` for a fresh conversation and the temporary conversation is cleaned up.

## Pre-Merge Verification Evidence

- Focused tests: AI 144/144, Studio NyxID 17/17, Orleans forwarding 15/15, and Orleans first-turn regression 1/1.
- Guards: test stability, query projection priming, projection state version, current-state mirror, architecture, docs lint, and `git diff --check` all exited 0.
- Build: `dotnet build aevatar.slnx --nologo --no-restore` exited 0 with 0 errors.
- Full tests: the first run had one unrelated scripting completion observation timeout; its exact test passed 1/1 and its complete project passed 443/443 on fresh reruns. The unchanged full solution command then exited 0.
