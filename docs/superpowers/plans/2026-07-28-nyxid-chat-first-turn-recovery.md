# NyxID Chat First-Turn Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> `superpowers:executing-plans`, `superpowers:test-driven-development`,
> `superpowers:systematic-debugging`, `ponytail:ponytail`, and
> `biz-sdd-workflow` task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking. Execute inline in the existing worktree; do not delegate. If an
> implementation decision is not specified here or in the approved design,
> stop and amend the plan/spec instead of improvising.

**Goal:** Restore end-to-end first-turn behavior for a newly accepted
`nyxid.chat` conversation: runtime linking cannot self-deadlock, an empty
transcript becomes eventually readable, every terminal turn is delivered once
through the existing chat-history actor trunk, and SSE reaches exactly one
terminal within its wall-clock deadline (R1-R7 of the approved design).

**Architecture:** The Orleans runtime detects when `LinkAsync` is running
inside the parent grain and updates that already-bound persistent state without
calling the parent grain through a hosted client. `ChatConversationGAgent`
remains the sole transcript authority; Studio Infrastructure adapts narrow
source-neutral application commands to the existing conversation and turn-
delivery actors. `NyxIdChatConversationGAgent` atomically records one bounded
initialization outbox after registry admission, one stable history reservation
with each started turn, and one bounded terminal outbox in the same commit as a
terminal transition. The HTTP endpoint owns only a writer gate and an outer
wall-clock race, so late inner frames cannot write after timeout.

**Tech Stack:** .NET 10, C# 14, Orleans 10.0.1, Google.Protobuf 3.33.4,
Grpc.Tools 2.76.0, xUnit, FluentAssertions, ASP.NET Core SSE.

## Global Constraints

- The approved design at
  `docs/superpowers/specs/2026-07-28-nyxid-chat-first-turn-recovery-design.md`
  is the only product/architecture decision source for this change.
- Preserve strict `Domain / Application / Infrastructure / Host` layering.
  NyxID may consume `IChatHistoryCommandPort` but must not reference the
  concrete chat-history actor implementation.
- `ChatConversationGAgent` remains the single transcript authority. Do not
  add a NyxID transcript store, transcript read-side reconstruction, or
  query-time event replay/priming.
- All durable controller, transcript, delivery, retry, and outbox state/events
  are Protobuf. C# application records are in-process typed port requests only
  and are mapped to Protobuf before actor dispatch.
- An accepted command receipt means inbox admission only. It must not imply
  handler completion, committed transcript append, or read-model visibility.
- Self continuation and retry/timeout work must re-enter the actor inbox through
  typed messages. No callback, timer thread, `Task.Run`, lock, or process-local
  ID map may mutate actor-owned state.
- Initialization and terminal delivery are at-least-once with idempotent
  receivers. Stable operation/delivery identities must survive activation and
  retries; conflicting identity reuse fails closed.
- Registration acceptance and the pending initialization outbox are one
  `NyxIdChatConversationRegistrationAcceptedEvent` state transition. A
  terminal controller status and its pending terminal outbox are one state
  transition. Do not insert a non-durable gap between either pair.
- The NyxID current-state projector and AGUI frame builder must not expose
  initialization/reservation/terminal delivery outbox payloads. Credentials,
  reasoning, tool arguments, raw tool results, and execution capabilities must
  never enter these outboxes.
- Keep parent/child topology and both relay bindings. Do not make the Orleans
  grain globally reentrant and do not bypass `IActorRuntime.LinkAsync`.
- `nyxid-chat` remains the transport. The NyxID conversation list remains
  the create-status resource; `/api/chat` create-recovery remains workflow-
  specific; `nyxid.chat.legacy` remains read-only without an explicit
  migration.
- Tests must not add arbitrary `Task.Delay` or polling. Endpoint deadline
  tests coordinate with `TaskCompletionSource` and invoke late writers
  deterministically.
- Do not add packages, compatibility shims, process-local registries, or ports
  5000/5050.
- Every behavior change follows RED, GREEN, REFACTOR: write and run the focused
  failing test first, confirm the stated failure, then write the minimum
  implementation and rerun the focused suite.

## Requirement Coverage

| Requirement | Deliverable |
| --- | --- |
| R1 Runtime-safe topology | A bound parent updates its own Orleans persistent `Children` state without a hosted-client self-call; other parents keep the proxy path and all relays. |
| R2 Honest first dispatch | History reservation admission precedes child creation/link/dispatch; create/link/dispatch rejection becomes a typed failed terminal instead of remaining `requested`. |
| R3 Empty transcript | Registry acceptance prepares a durable initialization outbox; `ChatConversationGAgent` idempotently initializes a zero-turn state and normal projection returns `200 []`. |
| R4 One transcript trunk | Existing `ChatTurnHistoryDeliveryGAgent` accepts source-neutral reservation/terminal messages and appends completed, failed, stopped, and blocked NyxID turns idempotently. |
| R5 Durable recovery | Activation and durable typed callbacks redeliver pending initialization/terminal outboxes; receiver admission clears only the matching pending item. |
| R6 Strict SSE terminal | Text/action and approval streams enforce an outer wall-clock timeout, close one writer gate, emit one `STREAM_TIMEOUT`, and discard deterministic late frames. |
| R7 Contract clarity | Canon states empty-history eventual visibility, transport-specific recovery, legacy read-only behavior, runtime self-parent linking, and production verification evidence. |

## File Map

| Path | Rung | Responsibility | R7 reason/defense |
| --- | --- | --- | --- |
| `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs` | R4 | Reuse the current grain-bound parent state during `LinkAsync` and preserve the non-bound path/relays. | Not applicable. |
| `test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeForwardingTests.cs` | R5 | Reproduce the self-parent proxy call and prove persistent-state/idempotent behavior. | Not applicable. |
| `agents/Aevatar.GAgents.ChatHistory/chat_history_messages.proto` | R4 | Define transcript initialization and source-neutral delivery contracts while retaining existing field numbers/type names. | Not applicable. |
| `agents/Aevatar.GAgents.ChatHistory/ChatConversationGAgent.cs` | R4 | Own idempotent initialization and conflict checks without creating a turn. | Not applicable. |
| `agents/Aevatar.GAgents.ChatHistory/ChatTurnHistoryDeliveryActorIds.cs` | R4 | Expose the existing opaque deterministic delivery actor address to the Infrastructure adapter. | Not applicable. |
| `agents/Aevatar.GAgents.ChatHistory/ChatTurnHistoryDeliveryGAgent.cs` | R4 | Use source-neutral identity internally, adapt workflow terminal input, and append one terminal turn. | Not applicable. |
| `agents/Aevatar.GAgents.ChatHistory/ChatTurnHistoryTerminalDeliveryPort.cs` | R4 | Map existing workflow-specific reservation/bind receipts to source-neutral actor commands. | Not applicable. |
| `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IChatHistoryCommandPort.cs` | R4 | Publish narrow initialization, reservation, and terminal application contracts consumed by NyxID. | Not applicable. |
| `src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedChatHistoryStore.cs` | R4 | Ensure deterministic history/delivery actors and map application requests to Protobuf commands. | Not applicable. |
| `src/Aevatar.Studio.Projection/Projectors/ChatHistoryCreateRecoveryCurrentStateProjector.cs` | R4 | Keep workflow create-recovery projection workflow-only after delivery fields become source-neutral. | Not applicable. |
| `test/Aevatar.Studio.Tests/ChatConversationGAgentAppendTests.cs` | R5 | Prove zero-turn initialization, exact replay, conflict, and initialize-after-append behavior. | Not applicable. |
| `test/Aevatar.Studio.Tests/ChatConversationCurrentStateProjectorTests.cs` | R4 | Prove initialized state materializes a zero-turn current-state document. | Not applicable. |
| `test/Aevatar.Studio.Tests/ActorBackedChatHistoryStoreTests.cs` | R4 | Prove typed adapter dispatch and empty-message read result. | Not applicable. |
| `test/Aevatar.Studio.Tests/ChatTurnHistoryDeliveryGAgentTests.cs` | R5 | Prove source-neutral wire compatibility, workflow adaptation, status mapping, deduplication, and immutable committed reservations. | Not applicable. |
| `test/Aevatar.Studio.Tests/ChatTurnHistoryTerminalDeliveryPortTests.cs` | R4 | Preserve workflow behavior while asserting source-neutral mapping. | Not applicable. |
| `test/Aevatar.Studio.Tests/ChatHistoryCreateRecoveryCurrentStateProjectorTests.cs` | R4 | Prove only workflow create reservations produce create-recovery documents. | Not applicable. |
| `agents/Aevatar.GAgents.NyxidChat/protos/nyxid_chat_task.proto` | R4 | Persist bounded reservation/initialization/terminal outboxes and typed retry signals/events. | Not applicable. |
| `agents/Aevatar.GAgents.NyxidChat/protos/agent_run.proto` | R4 | Extend the existing registration-accepted event with the atomically prepared controller state. | Not applicable. |
| `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs` | R4 | Prepare/dispatch/retry history operations and convert first-dispatch failures to typed terminal facts. | Not applicable. |
| `test/Aevatar.AI.Tests/NyxIdChatConversationGAgentTests.cs` | R5 | Prove ordering, atomic outboxes, terminal mappings, retry/recovery, and typed failures. | Not applicable. |
| `test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs` | R4 | Preserve create/delete lifecycle behavior with the expanded command port and atomic registration state. | Not applicable. |
| `test/Aevatar.AI.Tests/NyxIdChatRecoveryAndSecurityTests.cs` | R4 | Prove outboxes exclude credentials/raw execution payloads and recover after activation. | Not applicable. |
| `src/Aevatar.Studio.Projection/Projectors/NyxIdChatConversationCurrentStateProjector.cs` | R4 | Continue copying only query-shaped task state and explicitly exclude delivery outboxes. | Not applicable. |
| `test/Aevatar.Studio.Tests/NyxIdChatConversationCurrentStateProjectorTests.cs` | R5 | Prove outbox-only data is absent from the NyxID read document. | Not applicable. |
| `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs` | R4 | Share one writer gate and race inner interactions against the normalized wall-clock timeout. | Not applicable. |
| `test/Aevatar.AI.Tests/NyxIdChatStreamIdentityAndTerminalTests.cs` | R5 | Use a cancellation-ignoring interaction and deterministic late emission to prove strict terminal behavior. | Not applicable. |
| `docs/canon/nyxid-chat-api.md` | R4 | Document new-chat/history/recovery/legacy/SSE semantics. | Not applicable. |
| `docs/canon/architecture.md` | R4 | Document the Orleans current-parent topology update path. | Not applicable. |

## Cross-Task Interfaces

Application requests are C# records; actor state and actor messages remain
Protobuf. Use these names consistently:

```csharp
public sealed record ChatHistoryConversationInitialization(
    string OperationId,
    string ScopeId,
    string ConversationId,
    string ServiceId,
    string ServiceKind,
    DateTimeOffset CreatedAt,
    string? InitialTitle = null);

public sealed record ChatHistoryTurnDeliveryReservation(
    string DeliveryId,
    string ScopeId,
    string ConversationId,
    string TurnId,
    string UserText,
    string SourceActorId,
    string SourceCommandId,
    string SourceCorrelationId,
    string RequestFingerprint,
    bool CreateConversationIfMissing,
    bool ExposeCreateRecovery = false);

public enum ChatHistoryTurnTerminalStatus
{
    Completed = 1,
    Failed = 2,
    Stopped = 3,
    Blocked = 4,
}

public sealed record ChatHistoryTurnTerminalNotification(
    string DeliveryId,
    string SourceActorId,
    string SourceCommandId,
    ChatHistoryTurnTerminalStatus Status,
    string Text,
    string ErrorCode,
    DateTimeOffset ObservedAt);
```

`IChatHistoryCommandPort` produces accepted-only tasks:

```csharp
Task InitializeConversationAsync(
    ChatHistoryConversationInitialization request,
    CancellationToken ct = default);

Task ReserveTurnDeliveryAsync(
    ChatHistoryTurnDeliveryReservation request,
    CancellationToken ct = default);

Task NotifyTurnTerminalAsync(
    ChatHistoryTurnTerminalNotification notification,
    CancellationToken ct = default);
```

## Deferred Simplifications

| Ceiling | Business Trigger | Backport Skill |
| --- | --- | --- |
| `biz-defer: nyxid.chat.legacy remains read-only and has no identity migration, product approves an explicit legacy-to-controller identity mapping and state migration` | A migration contract and rollout owner are approved. | none |
| `biz-defer: 202 create does not synchronously guarantee transcript projection visibility, a future API contract requires an observed read-model receipt rather than polling` | Clients require a stronger acknowledged stage and the projection pipeline exposes it honestly. | none |

---

### Task 1: Remove the Orleans self-parent link deadlock

**Files:**

- Modify: `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs`
- Test: `test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeForwardingTests.cs`

**Interfaces:**

- Consumes `IRuntimeActorStateBindingAccessor.Current` and the existing
  `IPersistentState<RuntimeActorGrainState>` contract.
- Produces unchanged `IActorRuntime.LinkAsync(string parentId, string childId,
  CancellationToken ct)` semantics with a current-parent fast path.

- [x] **Step 1: Write the bound-parent RED tests**

Add tests that bind a persistent state whose `AgentId == parentId`, invoke
`LinkAsync`, and assert:

```csharp
boundState.State.Children.Should().ContainSingle("child");
boundState.WriteCount.Should().Be(1);
grains["parent"].AddChildCallCount.Should().Be(0);
grains["child"].ParentId.Should().Be("parent");
```

Invoke the same link twice and assert one child entry and one persistent write.
Keep the existing non-bound test and add an explicit assertion that it invokes
`AddChildAsync`. Reuse the repository's `DispatchProxy` persistent-state
test pattern; do not create a new mocking dependency.

Run:

```bash
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~OrleansActorRuntimeForwardingTests"
```

Expected: FAIL because `OrleansActorRuntime` does not receive the binding
accessor and still calls the parent grain proxy.

- [x] **Step 2: Implement the minimum current-parent branch**

Inject the existing `IRuntimeActorStateBindingAccessor`. After child
initialization and inside the existing call-chain-reentrancy scope, compare
`Current.State.AgentId` to the normalized `parentId`. For a match, add
the child only if absent and call `WriteStateAsync` on the already-bound
state. Otherwise call the existing `parent.AddChildAsync`. Leave child parent
assignment, hierarchy relay, committed-observation relay, logging, and public
signatures unchanged.

- [x] **Step 3: Verify GREEN, guards, and commit**

Run:

```bash
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~OrleansActorRuntimeForwardingTests"
bash tools/ci/test_stability_guards.sh
git diff --check
```

Expected: PASS; bound-parent tests show zero parent proxy calls, non-bound tests
still call the proxy, and both relay assertions remain green.

```bash
git add src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors/OrleansActorRuntime.cs test/Aevatar.Foundation.Runtime.Hosting.Tests/OrleansActorRuntimeForwardingTests.cs
git commit -m "Fix Orleans self-parent actor linking"
```

---

### Task 2: Initialize an empty transcript through its authoritative actor

**Files:**

- Modify: `agents/Aevatar.GAgents.ChatHistory/chat_history_messages.proto`
- Modify: `agents/Aevatar.GAgents.ChatHistory/ChatConversationGAgent.cs`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IChatHistoryCommandPort.cs`
- Modify: `src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedChatHistoryStore.cs`
- Test: `test/Aevatar.Studio.Tests/ChatConversationGAgentAppendTests.cs`
- Test: `test/Aevatar.Studio.Tests/ChatConversationCurrentStateProjectorTests.cs`
- Test: `test/Aevatar.Studio.Tests/ActorBackedChatHistoryStoreTests.cs`

**Interfaces:**

- Produces `InitializeChatConversationCommand` and
  `ChatConversationInitializedEvent` Protobuf messages with operation,
  scope, conversation, service, creation time, and optional title.
- Produces `InitializeConversationAsync(ChatHistoryConversationInitialization,
  CancellationToken)` from the Cross-Task Interfaces section.
- Consumes the existing deterministic `ChatHistoryActorIds.Conversation`
  address and `StudioActorCommandDispatch` accepted-only pipeline.

- [x] **Step 1: Write initialization actor RED tests**

Add tests for an empty actor, exact retry, conflicting identity/payload, and an
initialize command arriving after a same-identity append. Key assertions:

```csharp
agent.State.ScopeId.Should().Be("scope-a");
agent.State.ConversationId.Should().Be("conversation-a");
agent.State.ServiceKind.Should().Be("nyxid.chat");
agent.State.Turns.Should().BeEmpty();
persisted.Count(e => e.EventData.Is(ChatConversationInitializedEvent.Descriptor))
    .Should().Be(1);
```

An exact replay produces no second event. A changed scope, conversation,
service identity, timestamp, or initial title under the same operation fails
closed without changing state. Initialization after a same-identity append
fills identity/service/timestamps but preserves the existing turn.

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ChatConversationGAgentAppendTests"
```

Expected: FAIL because the initialization Protobuf messages and handler do not
exist.

- [x] **Step 2: Implement idempotent initialization**

Add the Protobuf command/event without changing existing field numbers. The
handler validates required identity, valid timestamp, non-deleted state, and
stable operation ID. It commits one initialization event when identity is
empty or same-identity state still needs initialization. It no-ops only for an
exact replay and throws `InvalidOperationException` for conflict. The state
transition sets identity/service/title/created/updated timestamps and never
adds or removes a turn.

- [x] **Step 3: Write adapter and projection RED tests**

Assert `InitializeConversationAsync` ensures the deterministic conversation
actor and dispatches the exact typed command. Project an initialized state and
assert a zero-turn document. Seed that document into the adapter reader and
assert:

```csharp
result.Status.Should().Be(ChatHistoryConversationResultStatus.Found);
result.Messages.Should().BeEmpty();
```

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ActorBackedChatHistoryStoreTests|FullyQualifiedName~ChatConversationCurrentStateProjectorTests"
```

Expected: FAIL because the application port and actor-backed initialization
mapping do not exist.

- [x] **Step 4: Implement the typed adapter mapping**

Add the application record/method exactly as declared above. Normalize required
values once in Infrastructure, ensure the deterministic conversation actor,
map to `InitializeChatConversationCommand`, and dispatch through the existing
command pipeline. Keep the read path unchanged; normal committed-state
projection makes the empty document visible.

- [x] **Step 5: Verify GREEN, guards, and commit**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ChatConversationGAgentAppendTests|FullyQualifiedName~ChatConversationCurrentStateProjectorTests|FullyQualifiedName~ActorBackedChatHistoryStoreTests"
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
git diff --check
```

Expected: PASS; one committed initialization produces a projected zero-message
conversation and no test/query path primes projection.

```bash
git add agents/Aevatar.GAgents.ChatHistory/chat_history_messages.proto agents/Aevatar.GAgents.ChatHistory/ChatConversationGAgent.cs src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IChatHistoryCommandPort.cs src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedChatHistoryStore.cs test/Aevatar.Studio.Tests/ChatConversationGAgentAppendTests.cs test/Aevatar.Studio.Tests/ChatConversationCurrentStateProjectorTests.cs test/Aevatar.Studio.Tests/ActorBackedChatHistoryStoreTests.cs
git commit -m "Initialize accepted chat transcripts"
```

---

### Task 3: Generalize the existing terminal-delivery trunk

**Files:**

- Modify: `agents/Aevatar.GAgents.ChatHistory/chat_history_messages.proto`
- Modify: `agents/Aevatar.GAgents.ChatHistory/ChatTurnHistoryDeliveryActorIds.cs`
- Modify: `agents/Aevatar.GAgents.ChatHistory/ChatTurnHistoryDeliveryGAgent.cs`
- Modify: `agents/Aevatar.GAgents.ChatHistory/ChatTurnHistoryTerminalDeliveryPort.cs`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IChatHistoryCommandPort.cs`
- Modify: `src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedChatHistoryStore.cs`
- Modify: `src/Aevatar.Studio.Projection/Projectors/ChatHistoryCreateRecoveryCurrentStateProjector.cs`
- Test: `test/Aevatar.Studio.Tests/ChatTurnHistoryDeliveryGAgentTests.cs`
- Test: `test/Aevatar.Studio.Tests/ChatTurnHistoryTerminalDeliveryPortTests.cs`
- Test: `test/Aevatar.Studio.Tests/ChatHistoryCreateRecoveryCurrentStateProjectorTests.cs`
- Test: `test/Aevatar.Studio.Tests/ActorBackedChatHistoryStoreTests.cs`

**Interfaces:**

- Renames `workflow_actor_id/workflow_command_id/workflow_correlation_id`
  to `source_actor_id/source_command_id/source_correlation_id` in every
  existing `ChatTurnHistoryDelivery*` message while retaining field numbers,
  message names, and type URLs.
- Produces `ChatTurnHistorySourceTerminalNotified` with delivery/source
  identities, closed terminal status, safe text/error, and observed time.
- Produces the reservation/terminal records and command-port methods from the
  Cross-Task Interfaces section.
- Existing `IWorkflowChatHistoryTerminalDeliveryPort` remains unchanged and
  maps workflow receipts/notifications at the boundary.

- [x] **Step 1: Write source-neutral wire and actor RED tests**

Write handcrafted Protobuf bytes for fields 6, 7, and 8 and parse them with the
new generated classes. Assert descriptor field numbers remain 6/7/8 and the
message type URLs are unchanged. Add neutral completed/failed/stopped/blocked
notifications and assert exact append mapping. Add exact retry and conflicting
reservation/terminal tests. Keep a workflow `WorkflowRunTerminalNotification`
test to prove the compatibility handler maps it into the same neutral core.

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ChatTurnHistoryDeliveryGAgentTests|FullyQualifiedName~ChatTurnHistoryTerminalDeliveryPortTests"
```

Expected: FAIL because the generated contract still exposes workflow-named
fields and has no neutral terminal message.

- [x] **Step 2: Rename fields and centralize terminal handling**

Retain all wire numbers and message names. Update state transitions and
validation to source terminology. Add one source-neutral terminal handler/core;
the existing workflow handler only validates/maps the workflow message and
calls that core. For blocked status, append the safe blocker summary as
assistant text. For failed/stopped, append empty assistant text plus sanitized
safe error/stop code. Keep completed behavior and append-result recording.

Make the existing deterministic delivery actor ID helper public. Mark
`ChatTurnHistoryDeliveryGAgent` as `IProjectedActor` because it already
feeds the stable create-recovery current-state consumer.

- [x] **Step 3: Write application adapter RED tests**

Assert the ActorBacked adapter:

- ensures one deterministic `ChatTurnHistoryDeliveryGAgent` for a delivery ID;
- dispatches source-neutral reservation with the supplied fingerprint and
  create flags;
- dispatches terminal with route publisher equal to `SourceActorId`;
- maps application terminal enum values exactly;
- does not expose a NyxID reservation through workflow create-recovery when
  `ExposeCreateRecovery == false`.

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ActorBackedChatHistoryStoreTests|FullyQualifiedName~ChatHistoryCreateRecoveryCurrentStateProjectorTests"
```

Expected: FAIL because the application contracts/mappings and workflow-only
projection flag do not exist.

- [x] **Step 4: Implement adapter and workflow-only recovery projection**

Add `ExposeCreateRecovery` to the delivery reserve/event/state contract on
new field numbers. Existing workflow create reservation sets it true; NyxID
sets it false even when `CreateConversationIfMissing` is true. Filter the
create-recovery projector on this field, then map source fields back into its
workflow-specific read document. Do not rename the public workflow recovery
DTO in this issue.

- [x] **Step 5: Verify GREEN, guards, and commit**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ChatTurnHistoryDeliveryGAgentTests|FullyQualifiedName~ChatTurnHistoryTerminalDeliveryPortTests|FullyQualifiedName~ChatHistoryCreateRecoveryCurrentStateProjectorTests|FullyQualifiedName~ActorBackedChatHistoryStoreTests"
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
git diff --check
```

Expected: PASS; workflow delivery/recovery behavior remains green, neutral
messages append all four terminal statuses, and NyxID reservations do not
materialize workflow create-recovery rows.

```bash
git add agents/Aevatar.GAgents.ChatHistory/chat_history_messages.proto agents/Aevatar.GAgents.ChatHistory/ChatTurnHistoryDeliveryActorIds.cs agents/Aevatar.GAgents.ChatHistory/ChatTurnHistoryDeliveryGAgent.cs agents/Aevatar.GAgents.ChatHistory/ChatTurnHistoryTerminalDeliveryPort.cs src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IChatHistoryCommandPort.cs src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedChatHistoryStore.cs src/Aevatar.Studio.Projection/Projectors/ChatHistoryCreateRecoveryCurrentStateProjector.cs test/Aevatar.Studio.Tests/ChatTurnHistoryDeliveryGAgentTests.cs test/Aevatar.Studio.Tests/ChatTurnHistoryTerminalDeliveryPortTests.cs test/Aevatar.Studio.Tests/ChatHistoryCreateRecoveryCurrentStateProjectorTests.cs test/Aevatar.Studio.Tests/ActorBackedChatHistoryStoreTests.cs
git commit -m "Generalize chat history terminal delivery"
```

- [x] **Step 6: Add a review-gap RED for malformed reservation replay**

Commit one valid reservation, then replay the same delivery actor with an
empty required `UserText`. Assert the handler throws a reservation-conflict
exception and the original `Reserved` status, request fingerprint, and empty
error fields remain unchanged.

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~WhenCommittedReservationIsReusedWithMalformedPayload"
```

Expected: FAIL because reserve validation currently persists a failed event
before checking whether the actor already owns a committed reservation.

- [x] **Step 7: Make committed reservation identity immutable**

Check existing state before validating a new reserve command. Return only for
an exact reservation replay; otherwise throw the existing conflict exception
without committing another event. Keep the existing validation-and-failure
behavior unchanged for a fresh delivery actor.

- [x] **Step 8: Verify the immutable-reservation review gap**

Run the new RED/GREEN test, the complete
`ChatTurnHistoryDeliveryGAgentTests` class, the Studio project, stability
guard, and `git diff --check`.

---

### Task 4: Add NyxID history reservation and durable outboxes

**Files:**

- Modify: `agents/Aevatar.GAgents.NyxidChat/protos/nyxid_chat_task.proto`
- Modify: `agents/Aevatar.GAgents.NyxidChat/protos/agent_run.proto`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatConversationGAgentTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatRecoveryAndSecurityTests.cs`
- Test: `test/Aevatar.Studio.Tests/NyxIdChatConversationCurrentStateProjectorTests.cs`

**Interfaces:**

- Consumes all three new `IChatHistoryCommandPort` methods.
- Produces bounded Protobuf state:
  `NyxIdChatHistoryInitializationOutbox`,
  `NyxIdChatHistoryDeliveryReservationState`, and
  `NyxIdChatHistoryTerminalOutbox`.
- Produces typed self signals and committed dispatched/retry events keyed by
  stable operation/delivery ID and attempt.
- Action continuations reserve the server-owned continuation turn before a
  postcondition dispatch. Their history input is a fixed rendering of closed
  report dispositions, never an origin prompt, resource identity, caller safe
  message, or raw action payload.
- Extends `NyxIdChatConversationRegistrationAcceptedEvent` with a state
  snapshot on a new field number so registration acceptance and initialization
  outbox preparation are one commit.

- [x] **Step 1: Write registration/initialization outbox RED tests**

After admission-visible registry registration, inspect the single registration
accepted event and its resulting state. Assert it contains one initialization
outbox with:

```csharp
outbox.ScopeId.Should().Be("scope-a");
outbox.ConversationId.Should().Be(actorId);
outbox.ServiceId.Should().Be(actorId);
outbox.ServiceKind.Should().Be(NyxIdChatServiceDefaults.GAgentKind);
outbox.OperationId.Should().NotBeNullOrWhiteSpace();
```

Assert a registration-unavailable path has no outbox. Invoke the typed self
handler: accepted history initialization dispatch commits a matching dispatched
event and clears only that pending item; a thrown port call retains it, commits
an incremented retry fact, and schedules a durable callback containing only
stable IDs/attempt. Reactivation republishes a typed self signal while pending.

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~NyxIdChatGAgentTests|FullyQualifiedName~NyxIdChatRecoveryAndSecurityTests"
```

Expected: FAIL because registration accepted does not transition controller
state and no initialization outbox/signals exist.

- [x] **Step 2: Implement atomic initialization outbox and retry**

Clone the controller state after registry admission, prepare the deterministic
initialization operation exactly once, and persist it inside the existing
registration-accepted event. After commit, publish a typed `Self`
continuation. Its handler calls `InitializeConversationAsync`. On accepted
dispatch, commit/clear; on failure, commit the next attempt and schedule
`ScheduleSelfDurableTimeoutAsync`. Activation republishes pending work;
callbacks and handlers compare operation ID plus attempt before acting.

- [x] **Step 3: Write reservation-before-provider and dispatch-failure RED tests**

Update the start-turn harness to record `history.reserve` separately from
runtime and provider dispatch. Assert this order:

```text
turn-started commit -> history.reserve admission -> create -> link -> dispatch -> operation-dispatched commit
```

The reservation uses a deterministic delivery ID from
`conversationActorId + turnId`, stores prompt plus source command/
correlation IDs in the delivery actor, sets
`CreateConversationIfMissing=true` and
`ExposeCreateRecovery=false`, and is exact under replay. Independently
make reserve, create, link, and provider dispatch throw/reject. For each, assert
no later provider operation runs and the controller commits a failed turn/task
rather than leaving phase `requested`.

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~NyxIdChatConversationGAgentTests"
```

Expected: FAIL because start-turn has no history reservation and exceptions
escape before a terminal controller fact is committed.

- [x] **Step 4: Implement reservation and typed first-dispatch failure**

Include a pending reservation descriptor in the same state snapshot as
`NyxIdChatTurnStartedEvent`. Admit the deterministic source-neutral
reservation and commit its dispatched marker before child creation. Check the
`DispatchAdmission.Accepted` result. Wrap reservation/create/link/dispatch
stages in one narrowly scoped failure conversion that uses
`NyxIdChatTaskLifecycle.ApplyOperationResult` with stable safe codes and
`NotStarted/NotApplied` evidence; do not catch cancellation as a business
failure. Activation must finish a pending reservation before publishing the
existing interrupted-operation recovery signal.

- [x] **Step 5: Write terminal-outbox RED tests for all closed statuses**

Drive completed, failed, stopped, and blocked transitions. Inspect the exact
committed event state and assert terminal status plus one matching pending
outbox are atomic. Completed carries only final assistant text; failed carries
only safe error; stopped carries stable stop code; blocked carries safe blocker
summary. Assert no credential, reasoning content, tool argument, or raw tool
result bytes appear. Invoke the terminal self handler and assert the source-
neutral notification mapping, accepted dispatch clearing, exact retry no-op,
failure retry scheduling, and activation recovery.

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~NyxIdChatConversationGAgentTests|FullyQualifiedName~NyxIdChatRecoveryAndSecurityTests"
```

Expected: FAIL because terminal controller commits do not prepare or deliver a
history outbox.

- [x] **Step 6: Implement terminal preparation/delivery without a second transcript**

Before persisting each terminal controller event, clone its authoritative next
state and attach one matching terminal outbox from its reservation. Apply this
to normal operation reconciliation, fail-closed start dispatch, stop/control,
blocked browser action, action postcondition, retry/skip terminal outcomes, and
other existing controller paths that can make `ActiveTurn` terminal. Refuse
to overwrite a different still-pending outbox. After commit, publish one typed
self continuation; accepted `NotifyTurnTerminalAsync` commits/clears it,
failure schedules durable retry, and activation republishes it. The outbox is
delivery transport state only; transcript append stays in
`ChatTurnHistoryDeliveryGAgent -> ChatConversationGAgent`.

- [x] **Step 7: Prove outboxes do not leak into current-state/AGUI**

Populate every outbox with unique sentinel safe text and a credential-like
sentinel in an actor-state fixture. Project it and serialize the resulting
`NyxIdChatConversationCurrentStateDocument`. Assert no outbox fields or
sentinels are present while normal task/turn state remains correct. Existing
AGUI security tests must remain green.

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~NyxIdChatConversationCurrentStateProjectorTests"
```

Expected: FAIL only if the implementation exposed new outbox fields; after the
explicit query-shape mapping is kept narrow, PASS with no sentinel leakage.

- [x] **Step 8: Verify GREEN, guards, and commit**

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~NyxIdChatConversationGAgentTests|FullyQualifiedName~NyxIdChatGAgentTests|FullyQualifiedName~NyxIdChatRecoveryAndSecurityTests"
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~NyxIdChatConversationCurrentStateProjectorTests"
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
git diff --check
```

Expected: PASS; every terminal maps to one delivery notification, recovery is
eventized/durable, first-dispatch failures are terminal, and read models remain
outbox-free.

```bash
git add agents/Aevatar.GAgents.NyxidChat/protos/nyxid_chat_task.proto agents/Aevatar.GAgents.NyxidChat/protos/agent_run.proto agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs test/Aevatar.AI.Tests/NyxIdChatConversationGAgentTests.cs test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs test/Aevatar.AI.Tests/NyxIdChatRecoveryAndSecurityTests.cs test/Aevatar.Studio.Tests/NyxIdChatConversationCurrentStateProjectorTests.cs
git commit -m "Deliver NyxID turns to chat history"
```

- [x] **Step 9: Add a review-gap RED for post-accept continuation failure**

Make the actor dispatch port throw while creating an admission-visible local
conversation. Assert that the already committed registration-accepted state
retains its initialization outbox, the registry is not unregistered, the actor
is not destroyed, and no registration-unavailable event is committed.

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~WhenInitializationContinuationDispatchFails"
```

Expected: FAIL because the broad registration `catch` currently treats the
post-commit self-message failure as registration failure and compensates the
already accepted conversation.

- [x] **Step 10: Keep creation compensation before the accepted boundary**

Limit creation compensation to registration/admission and accepted-event
persistence failures. After the accepted event commits, catch a failed
initialization self-message publication separately, log only safe identity and
exception type, and leave the durable outbox pending for activation recovery.

- [x] **Step 11: Verify the post-accept recovery gap**

Run the new RED/GREEN test, all creation/initialization tests in
`NyxIdChatGAgentTests`, the AI project, stability guard, and `git diff --check`.

- [x] **Step 12: Add review-gap RED tests for input-parts-only turns**

Start one turn with an empty prompt and a typed input part containing unique
raw sentinels. Assert history reservation uses exactly `Shared input content.`,
contains none of the raw values, and provider dispatch still runs. Replay the
same turn identity and prompt with different input parts and assert it is not
treated as an exact no-op.

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~InputPartsOnly|FullyQualifiedName~DifferentInputParts"
```

Expected: FAIL because reservation currently forwards the empty prompt as an
invalid empty `UserText`, while exact-turn admission compares only the prompt.

- [x] **Step 13: Reuse the request fingerprint for full input identity**

Keep normal prompt text unchanged. For input-parts-only turns use the fixed safe
transcript text and include a length-safe irreversible digest of each full
input part in the existing request fingerprint. Exact-turn admission compares
that committed fingerprint; no raw part content is added to history state.

- [x] **Step 14: Verify the input-parts compatibility gap**

Run the two new tests, all `NyxIdChatConversationGAgentTests`, the AI project,
stability guard, security scans, and `git diff --check`.

---

### Task 5: Enforce a strict SSE wall-clock terminal

**Files:**

- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatStreamIdentityAndTerminalTests.cs`

**Interfaces:**

- Produces one private shared writer gate used by text/action, approval, and
  heartbeat writes.
- Consumes existing `StreamTerminalTimeout` and request cancellation;
  public HTTP routes/DTOs remain unchanged.

- [x] **Step 1: Replace the cooperative timeout test with a stubborn RED test**

Create an interaction double that captures `emitAsync`, returns a task that
never completes, and ignores its cancellation token. Set a short configured
timeout, await the endpoint with a separate test safety `WaitAsync`, then
assert one `STREAM_TIMEOUT` terminal. After the endpoint returns, invoke the
captured callback with `TEXT_MESSAGE_CONTENT` and `RUN_FINISHED` and
assert frame count/terminal count do not change. Do this for the normal stream
and approval stream without `Task.Delay`.

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~NyxIdChatStreamIdentityAndTerminalTests"
```

Expected: FAIL because the endpoint only passes a cancelled token into the
stubborn inner interaction, continues awaiting it, and reaches the test safety
timeout without returning a STREAM_TIMEOUT frame.

- [x] **Step 2: Implement outer timeout race and writer gate**

Normalize non-positive timeout to five minutes. Start the interaction task and
await it with an outer wall-clock `WaitAsync(timeout, requestToken)` (or an
equivalent `Task.WhenAny` race that distinguishes request cancellation).
On server timeout: acquire the shared gate, atomically close it and write
exactly one safe `STREAM_TIMEOUT`, stop heartbeat when timeout wins, then
cancel the inner linked token and return without awaiting inner cleanup. Attach
a completion/fault-observing continuation that releases the linked token source
only when the detached task finishes. Every callback and heartbeat acquires the
same gate and returns without writing when closed. A real terminal closes the
gate after its write; the timeout/failure paths then emit nothing. Request
cancellation closes work without attempting a disconnected-client terminal.

- [x] **Step 3: Verify existing success/failure/cancellation behavior**

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~NyxIdChatStreamIdentityAndTerminalTests|FullyQualifiedName~NyxIdChatEndpointsCoverageTests|FullyQualifiedName~NyxIdChatActionContinuationEndpointsTests"
bash tools/ci/test_stability_guards.sh
git diff --check
```

Expected: PASS; success/failure still have exactly one terminal, stubborn
interactions return by the configured deadline, late frames are discarded, and
request cancellation writes no synthetic timeout.

- [x] **Step 4: Commit the endpoint fix**

```bash
git add agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs test/Aevatar.AI.Tests/NyxIdChatStreamIdentityAndTerminalTests.cs docs/superpowers/specs/2026-07-28-nyxid-chat-first-turn-recovery-design.md docs/superpowers/plans/2026-07-28-nyxid-chat-first-turn-recovery.md
git commit -m "Enforce NyxID stream wall-clock timeout"
```

- [x] **Step 5: Add review-gap RED tests for inner timeouts**

For both text and approval interactions, return a faulted task whose exception
is `TimeoutException`. Assert that the endpoint emits exactly one terminal with
code `STREAM_FAILURE`, not `STREAM_TIMEOUT`, and does not expose the inner
exception text.

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~WhenInteractionThrowsTimeout_ShouldWriteStreamFailure"
```

Expected: FAIL because the broad endpoint `catch (TimeoutException)` currently
cannot distinguish the inner failure from its own `WaitAsync` deadline.

- [x] **Step 6: Give the wall-clock deadline a private typed exception**

Wait for the interaction with an independent linked deadline cancellation
token. Translate only cancellation of that deadline into one private
endpoint-owned timeout exception; allow an inner `TimeoutException` to reach
the existing safe `STREAM_FAILURE` path. Keep public routes and DTOs unchanged.

- [x] **Step 7: Verify the review gap and commit it with Task 6 docs**

Run the two new tests, the Task 5 endpoint regression suite, stability guard,
and `git diff --check`. Include this narrow correction in the final Task 6
documentation commit.

---

### Task 6: Update canon and close verification

**Files:**

- Modify: `docs/canon/nyxid-chat-api.md`
- Modify: `docs/canon/architecture.md`

**Interfaces:**

- Documents the implemented HTTP/actor/runtime contracts; introduces no new
  code interface.

- [x] **Step 1: Update canonical behavior**

In `nyxid-chat-api.md` state all of the following explicitly:

- create remains `202 Accepted` and the NyxID conversation list/status URL
  is the create-status resource;
- an accepted new conversation eventually has a chat-history document with an
  empty message list before/without a completed first turn;
- terminal turns reach the existing transcript authority at least once and are
  idempotent;
- `/api/chat` `create-recovery/{commandId}` is not a NyxID-chat
  recovery route;
- `nyxid.chat.legacy` histories are read-only on the new transport without
  migration;
- wall-clock timeout closes one writer gate and late frames are discarded.

In `architecture.md` document that Orleans `LinkAsync` updates the
currently bound parent persistent state inside its existing grain turn, while
non-current parents use `AddChildAsync`, and both relay bindings remain.

- [x] **Step 2: Run focused cross-layer verification**

Run:

```bash
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~OrleansActorRuntimeForwardingTests"
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ChatConversationGAgentAppendTests|FullyQualifiedName~ChatConversationCurrentStateProjectorTests|FullyQualifiedName~ActorBackedChatHistoryStoreTests|FullyQualifiedName~ChatTurnHistoryDeliveryGAgentTests|FullyQualifiedName~ChatTurnHistoryTerminalDeliveryPortTests|FullyQualifiedName~ChatHistoryCreateRecoveryCurrentStateProjectorTests|FullyQualifiedName~NyxIdChatConversationCurrentStateProjectorTests"
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~NyxIdChatConversationGAgentTests|FullyQualifiedName~NyxIdChatGAgentTests|FullyQualifiedName~NyxIdChatRecoveryAndSecurityTests|FullyQualifiedName~NyxIdChatStreamIdentityAndTerminalTests|FullyQualifiedName~NyxIdChatEndpointsCoverageTests|FullyQualifiedName~NyxIdChatActionContinuationEndpointsTests"
```

Expected: all focused runtime, transcript, controller, security, and endpoint
tests pass.

- [x] **Step 3: Run mandatory guards and docs lint**

Run:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: every guard/lint exits 0 with no new architecture, polling,
projection, or documentation violations.

- [x] **Step 4: Build and run relevant/full tests**

Run:

```bash
dotnet build aevatar.slnx --nologo --no-restore
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo --no-build --no-restore
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-build --no-restore
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-build --no-restore
dotnet test aevatar.slnx --nologo --no-build --no-restore
```

Expected: build and relevant projects pass; full solution tests pass unless an
environment-owned integration dependency is unavailable, in which case record
the exact failing test/output without weakening it.

- [x] **Step 5: Review the final diff and architecture evidence**

Run:

```bash
git diff --check
git status --short
git diff --stat origin/feature/integrate...HEAD
git diff origin/feature/integrate...HEAD -- src agents test docs
```

Check no user worktree files, generated artifacts, secrets, process-local maps,
query-time lifecycle calls, legacy kind fallback, or unrelated refactors are
present. Confirm each requirement row above has code, tests, and (where
applicable) canon evidence. Apply the repository review checklist and record
any blocking gap before completion.

- [x] **Step 6: Commit final review corrections and documentation**

```bash
git add \
  agents/Aevatar.GAgents.ChatHistory/ChatTurnHistoryDeliveryGAgent.cs \
  agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs \
  agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs \
  docs/canon/architecture.md \
  docs/canon/nyxid-chat-api.md \
  docs/superpowers/plans/2026-07-28-nyxid-chat-first-turn-recovery.md \
  docs/superpowers/specs/2026-07-28-nyxid-chat-first-turn-recovery-design.md \
  src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedChatHistoryStore.cs \
  test/Aevatar.AI.Tests/NyxIdChatConversationGAgentTests.cs \
  test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs \
  test/Aevatar.AI.Tests/NyxIdChatStreamIdentityAndTerminalTests.cs \
  test/Aevatar.Studio.Tests/ChatTurnHistoryDeliveryGAgentTests.cs
git commit -m "Document NyxID first-turn recovery"
```

## Verification Evidence

- `dotnet build aevatar.slnx --nologo --no-restore`: exit 0, 0 errors.
- Runtime Hosting: 294 passed, 17 environment-gated skips, 0 failed.
- Studio: 1443 passed, 0 failed.
- AI: 1885 passed, 0 failed.
- `dotnet test aevatar.slnx --nologo --no-build --no-restore`: exit 0;
  every executed project reported 0 failures.
- Stability, query/projection, architecture, docs, solution-split, test
  ownership, and slow-test guards: exit 0.
- Production image `de388766` is an `origin/feature/integrate` revision and
  does not contain this branch; the seven-step production smoke in the design
  remains a post-deployment rollout check, not local completion evidence.

## Final Review Checklist

- [x] R1: bound-parent Orleans link avoids the parent proxy, writes once, and
  preserves child parent plus both relays.
- [x] R2: history reservation is admitted before provider dispatch and each
  reserve/create/link/dispatch failure becomes a typed terminal.
- [x] R3: accepted registration atomically prepares initialization; projected
  empty history returns `200` with zero messages.
- [x] R4: completed/failed/stopped/blocked terminals use the existing delivery
  and conversation actors; exact retries do not duplicate turns.
- [x] R4: malformed or conflicting reservation replay cannot overwrite an
  already committed delivery state.
- [x] R5: initialization and terminal pending state survives activation and is
  retried only through typed self events/durable callbacks.
- [x] R6: cancellation-ignoring interactions still return one timeout terminal
  by the configured wall clock and deterministic late frames cannot write.
- [x] R7: docs distinguish NyxID status polling, workflow create recovery, and
  legacy read-only history.
- [x] Protobuf old field numbers/type names are unchanged where renamed.
- [x] NyxID current-state and AGUI outputs contain no history outbox content,
  credential, reasoning, tool argument, or raw tool result.
- [x] No query path activates actors, primes projection, reads the event store,
  or reconstructs transcript state.
- [x] No test adds arbitrary delay/polling and all mandatory guards pass.
- [x] `git status --short` contains no generated output, databases,
  WAL/SHM, secrets, or files from the user's main worktree.
- [x] Over-engineering review result is exactly `符合约定，可交付` or every
  finding is resolved/registered before handoff.

## Plan Self-Review

- Spec Problem/Goals 1-2 map to Tasks 1 and 4; Goals 3-4 map to Tasks 2-4;
  Goal 5 maps to Task 5; Goal 6 and Error Semantics map across Tasks 1-6.
- Product Semantics and Non-goals map to Global Constraints, Task 3's workflow-
  only create-recovery flag, and Task 6 canon.
- Every production behavior has a preceding focused RED command with a concrete
  failure reason and a subsequent GREEN command.
- Cross-task names are fixed in the shared Interfaces section; Task 2 produces
  initialization, Task 3 produces reservation/terminal delivery, and Task 4 is
  their sole NyxID consumer.
- All writes specify ownership, idempotency key, and atomicity boundary. There
  is no cross-actor transaction claim: controller outbox delivery is at-least-
  once and transcript/delivery receivers are idempotent.
- The banned-placeholder scan and unresolved-question scan must return no
  matches before this plan is committed.

## Execution Handoff

Execute inline in
`/Users/eanzhao/Code/aevatar/.worktrees/nyxid-chat-first-turn` on branch
`fix/2026-07-28_nyxid-chat-first-turn`. Pause for a local diff/test review
after Tasks 1-3 and again after Tasks 4-5. On failure, return to the failing
test/root-cause evidence; do not weaken the assertion to fit the implementation.
