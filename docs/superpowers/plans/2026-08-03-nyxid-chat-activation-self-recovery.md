# NyxID Chat Activation Self-Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the Orleans activation-time self-dispatch deadlock without changing any NyxID Chat recovery, API, state-machine, projection, or secret-boundary feature.

**Architecture:** Keep `IActorDispatchPort` as the external actor inbox admission boundary, including its Orleans activation probe. Route current-actor recovery continuations through the existing `GAgentBase` `IEventPublisher` self-publication path, preserving correlation and stable delivery lineage with `EventEnvelopePublishOptions`.

**Tech Stack:** .NET 10, C#, Orleans 10.0.1, Protobuf, xUnit, FluentAssertions.

## Global Constraints

- Preserve every recovery behavior introduced by `1458a5bdbda5aefd17f8b5a0c43efee1618970f1`.
- Activation may enqueue typed self-continuations but must never execute provider/tool work inline.
- Do not change `/api/chat`, AGUI/SSE, protobuf, read-model, identity, accepted-only ACK, or secret contracts.
- Do not change `OrleansActorDispatchPort`, add a second transport, add a runtime-specific business helper, or add a new abstraction.
- Keep messages to other actors on `IActorDispatchPort`.
- Use existing `PublishAsync`/`SendToAsync` and `EventEnvelopePublishOptions`; add no dependency.
- Follow test-first RED → GREEN; any modified test requires `tools/ci/test_stability_guards.sh`.
- Push without force only after fetching and reconciling `origin/feature/integrate`.

---

### Task 1: Pin Activation Recovery To The Actor Self-Publication Contract

**Files:**
- Modify: `test/Aevatar.AI.Tests/NyxIdChatRecoveryAndSecurityTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatRecoveryAndSecurityTests.cs`

**Interfaces:**
- Consumes: `IEventPublisher.PublishAsync<TEvent>(..., TopologyAudience.Self, ..., EventEnvelopePublishOptions?)` and `IEventPublisher.SendToAsync<TEvent>(string, ..., EventEnvelopePublishOptions?)`.
- Produces: assertions that recovery messages use the actor-owned publisher, retain typed payload identity, `CorrelationId`, and `Delivery.OperationId`, and never use `IActorDispatchPort` for current-actor activation continuation.

- [ ] **Step 1: Add a focused recovery publisher recorder in the existing test file**

Add a private test-only `RecoveryRecordingEventPublisher` to `NyxIdChatRecoveryAndSecurityTests.cs`. It retains `(Event, Audience, Options)` for publications and `(TargetActorId, Event, Options)` for direct sends. Do not change the shared `AgentCoverageTestSupport.cs` recorder or any unrelated tests.

```csharp
public List<(IMessage Event, TopologyAudience Audience, EventEnvelopePublishOptions? Options)> PublishCalls { get; } = [];
public List<(string TargetActorId, IMessage Event, EventEnvelopePublishOptions? Options)> SendCalls { get; } = [];
```

- [ ] **Step 2: Change only activation-recovery assertions to the wished-for publisher contract**

In `NyxIdChatRecoveryAndSecurityTests`, assign one `TestRecordingEventPublisher` to each conversation/turn actor before activation. Assert that the typed `NyxIdChatRecoveryRequestedSignal` is published with `TopologyAudience.Self`, that the external dispatch recorder has no self call, and that options contain the existing correlation and stable recovery identity. Build a test-only envelope for manually invoking the handler from the recorded payload/options.

```csharp
publisher.PublishCalls.Should().ContainSingle(call =>
    call.Event is NyxIdChatRecoveryRequestedSignal &&
    call.Audience == TopologyAudience.Self);
dispatch.Calls.Should().BeEmpty("activation recovery uses the current actor publisher");
```

Keep the existing assertions for postcondition redispatch, interrupted LLM, effect-capable tool uncertainty, stale key/version, blocked browser action, turn delivery loss, repeated activation, and no provider/tool replay.

- [ ] **Step 3: Run the focused test and verify RED**

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatRecoveryAndSecurityTests'
```

Expected: FAIL because activation recovery still records a self call on `RecordingActorDispatchPort`, while `TestRecordingEventPublisher.PublishCalls` is empty. No production file may be modified before this failure is observed.

- [ ] **Step 4: Record the RED evidence**

Capture the failing test name and assertion message in the working notes/terminal output. Confirm the failure is transport selection, not test setup or compilation.

---

### Task 2: Route NyxID Self-Continuations Through The Existing Publisher

**Files:**
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTurnGAgent.cs`
- Modify: `test/Aevatar.Integration.Tests/NyxIdChatOrleansFirstTurnIntegrationTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatRecoveryAndSecurityTests.cs`
- Test: `test/Aevatar.Integration.Tests/NyxIdChatOrleansFirstTurnIntegrationTests.cs`

**Interfaces:**
- Consumes: the publisher contract pinned by Task 1 and the existing typed recovery handlers.
- Produces: deadlock-free activation recovery with unchanged typed recovery state transitions and unchanged external actor dispatch.

- [ ] **Step 1: Add the real Orleans reactivation regression**

Extend `NyxIdChatOrleansFirstTurnIntegrationTests` with a test which uses the registered in-memory `IEventStore` to append one `NyxIdChatTurnOperationAdmittedEvent` for a turn actor, initializes the actor kind, deactivates it, and then triggers reactivation with `IsInitializedAsync()`. Assert with `WaitAsync(TimeSpan.FromSeconds(5))` that reactivation completes, then observe the event store until the typed recovery completion/delivery events exist and assert the fake executor call count remains zero. Use the existing shared Orleans host and no arbitrary `Task.Delay`.

```csharp
await turn.IsInitializedAsync().WaitAsync(TimeSpan.FromSeconds(5));
executor.CallCount.Should().Be(0, "activation recovery must not repeat operation I/O");
```

The event-store observation must use a condition signaled by a small test `IEventStore` decorator or another existing deterministic callback; do not introduce polling or `Task.Delay`.

- [ ] **Step 2: Run the Orleans regression against pre-fix production code**

Run:

```bash
dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatOrleansFirstTurnIntegrationTests.TurnReactivation'
```

Expected: FAIL with activation not completing within five seconds (or the equivalent Orleans self-call timeout) while the current production code uses `_actorDispatchPort.DispatchAsync(Id, ...)`. If the failure is test setup, correct the test and repeat until it fails for the self-dispatch reason.

- [ ] **Step 3: Implement the minimum production change**

In both actors, delete manual self `EventEnvelope` construction where only the current actor is targeted. Publish the typed signal using the existing base helpers. Preserve prior correlation and stable dispatch identity through options:

```csharp
var options = new EventEnvelopePublishOptions
{
    Propagation = new EventEnvelopePropagationOverrides
    {
        CorrelationId = operationId,
    },
    Delivery = new EventEnvelopeDeliveryOptions
    {
        OperationId = stableDispatchId,
    },
};
await PublishAsync(signal, TopologyAudience.Self, ct, options);
```

Use `PublishAsync(..., TopologyAudience.Self, ...)` for operation recovery, history initialization, and history terminal signals. Use `SendToAsync(Id, ...)` for the existing direct self input materialization route. Do not alter handlers, state transitions, retry policy, external turn/conversation dispatch, or `OrleansActorDispatchPort`.

- [ ] **Step 4: Run focused GREEN tests**

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatRecoveryAndSecurityTests'
dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatOrleansFirstTurnIntegrationTests'
```

Expected: PASS. The unit suite proves all original feature semantics; the real Orleans test proves reactivation completes and provider/tool I/O is not repeated.

- [ ] **Step 5: Run NyxID and repository guards**

Run:

```bash
bash tools/ci/nyxid_chat_semantics_guard.sh
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
```

Expected: all exit 0.

- [ ] **Step 6: Run complete build and test verification**

Run:

```bash
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo --no-build
```

Expected: build has 0 errors and full tests have 0 failures. Existing repository warnings may remain; record them separately from the result.

- [ ] **Step 7: Review the final diff against the feature-preservation checklist**

Verify:

```bash
git diff --check
git diff --stat origin/feature/integrate...HEAD
git diff origin/feature/integrate...HEAD -- \
  agents/Aevatar.GAgents.NyxidChat \
  test/Aevatar.AI.Tests/NyxIdChatRecoveryAndSecurityTests.cs \
  test/Aevatar.Integration.Tests/NyxIdChatOrleansFirstTurnIntegrationTests.cs \
  docs/superpowers
```

Expected: no shared runtime, protobuf, API, projection, or unrelated file changes.

- [ ] **Step 8: Commit the implementation**

```bash
git add \
  agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs \
  agents/Aevatar.GAgents.NyxidChat/NyxIdChatTurnGAgent.cs \
  test/Aevatar.AI.Tests/NyxIdChatRecoveryAndSecurityTests.cs \
  test/Aevatar.Integration.Tests/NyxIdChatOrleansFirstTurnIntegrationTests.cs \
  docs/superpowers/specs/2026-08-03-nyxid-chat-activation-self-recovery-design.md \
  docs/superpowers/plans/2026-08-03-nyxid-chat-activation-self-recovery.md
git commit -m "Fix NyxID chat activation recovery"
```

- [ ] **Step 9: Reconcile and push the authorized branch**

```bash
git fetch origin feature/integrate
git rebase origin/feature/integrate
git push origin HEAD:feature/integrate
git ls-remote --heads origin feature/integrate
```

Expected: no force push; remote `refs/heads/feature/integrate` equals local `HEAD`.
