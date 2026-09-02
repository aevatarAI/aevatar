# NyxID Assistant Chat v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Issues:** #2936, #2954, #2955, #2956, #2957, #2961

**Goal:** Complete milestone 36 by making NyxIdChat task execution, stop, steering, reconnect, browser-action handoff, and terminal failure semantics actor-owned, protobuf-backed, and observable through one committed Projection Pipeline.

**Architecture:** Keep the public `actorId` as the canonical conversation-controller identity and move provider/tool I/O into a short-lived `NyxIdChatTurnGAgent` that executes exactly one authorized operation per actor turn. The controller commits an operation waterline before dispatch, reconciles typed child results using the complete identity/generation key, then commits the next task fact; the same committed controller state feeds live AGUI frames and an actor-scoped current-state read model. Terminal transcript remains a separate `ChatConversationGAgent` concern.

**Tech Stack:** .NET 10, protobuf, Aevatar event-sourced GAgents, CQRS Core, Projection Pipeline, ASP.NET Core minimal APIs, AGUI/SSE, xUnit, FluentAssertions.

## Global Constraints

- Preserve strict `Domain / Application / Infrastructure / Host` responsibility: endpoints authenticate, admit, map, and write responses only; actors own task/control decisions; queries read read models only.
- Use `EventEnvelope` as the only actor/projection transport shell; task/control/action meaning is carried by typed protobuf payloads.
- Never keep conversation, turn, task, operation, cancellation, action, or cursor facts in a service-level `Dictionary`, `ConcurrentDictionary`, `HashSet`, `Queue`, or process-local registry.
- The canonical conversation actor commits before dispatching an LLM/tool operation and commits again before live/durable observation.
- Realtime model execution remains stream-first. Do not add `ChatAsync` to NyxIdChat, Scope Service, CLI, AGUI, or workflow chat paths.
- Durable state, commands, events, callbacks, and child-result signals use protobuf. JSON exists only at HTTP/AGUI or external NyxID adapter boundaries.
- Keep `actorId`, `turnId`, `taskId`, `stepId`, `operationId`, `operationGeneration`, `stopRequestId`, `steeringId`, `actionRequestId`, `originTurnId`, `clientRequestId`, `continuationTurnId`, `commandId`, `correlationId`, and `stateVersion` distinct.
- Do not persist or project credentials, bearer/access/refresh tokens, authorization/cookie headers, OAuth/device/user codes, client secrets, raw upstream bodies, URI userinfo, or secret-bearing URI query/fragment values.
- A required `failed` or `uncertain` step cannot yield task `succeeded`; browser-reported `completed` cannot make a step `done` without a typed matching postcondition read.
- A stop/steering fence is durable and prevents every later old-plan model round, tool, retry, or step start. Cancellation is best effort and must report `uncancellable`/`may_have_changed` honestly when it cannot be proved.
- The state query reads `NyxIdChatConversationCurrentStateDocument` only. It must not activate an actor, attach/prime a projection, replay an event store, execute I/O, or create a turn.
- Actor-derived committed version is the read-model `StateVersion`; no projection-local counter may be introduced.
- Tests use visibly distinct IDs such as `conversation-alpha`, `turn-alpha`, `task-alpha`, `step-alpha`, `operation-alpha`, and `action-alpha`.
- New asynchronous tests coordinate with `TaskCompletionSource`, `Channel`, or actor messages. Do not add arbitrary `Task.Delay` polling.
- Do not introduce ports `5000` or `5050`.

---

### Task 1: Port #2936 Producer-Owned Failure Classification

**Files:**
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdProxyToolExactIdentityTests.cs`
- Modify: `test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj`
- Modify: `test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs`

**Interfaces:**
- Consumes: `IAgentTool.CreateResultReceipt(string? callId, string? toolName, string argumentsJson, string resultJson)` and the existing workflow fail-fast/projection/SSE pipeline.
- Produces: an `AgentToolReceipt` with `Status = Error`, `ErrorCode = "NYXID_PROXY_SERVICE_ID_REQUIRED"`, and the exact service-identity validation message only for the producer-known missing-`service_id` result.

- [ ] **Step 1: Add the exact producer-classification tests**

Add tests that execute `nyxid_proxy` with slug but no `service_id`, then assert:

```csharp
receipt.Should().NotBeNull();
receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
receipt.ErrorCode.Should().Be("NYXID_PROXY_SERVICE_ID_REQUIRED");
receipt.ErrorMessage.Should().Be("'service_id' is required when 'slug' is provided");
handler.RequestCount.Should().Be(0);
```

Also pass the same JSON body with a valid exact `service_id` and assert `CreateResultReceipt` returns `null`; ordinary domain JSON named `error` must not become a generic failure detector.

- [ ] **Step 2: Run the focused test to verify RED**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~NyxIdProxyToolExactIdentityTests
```

Expected: the missing-service test fails because the current tool returns ordinary JSON and no receipt.

- [ ] **Step 3: Add the narrow result constant and receipt classification**

Define the exact constants and gate on both input identity and exact producer result:

```csharp
private const string ServiceIdRequiredErrorCode = "NYXID_PROXY_SERVICE_ID_REQUIRED";
private const string ServiceIdRequiredErrorMessage = "'service_id' is required when 'slug' is provided";
private const string ServiceIdRequiredResult = """{"error":"'service_id' is required when 'slug' is provided"}""";
```

Return `AgentToolReceiptStatus.Error` only when `service_id` is absent, slug/service is present, and `resultJson` equals `ServiceIdRequiredResult` ordinally. Reuse the constant from `ExecuteAsync`.

- [ ] **Step 4: Prove scheduling, read-model, and SSE terminal propagation**

Add one integration regression with a two-step workflow (`call_service -> report_q1000`). Capture requested step IDs and assert only `call_service` runs, `WorkflowCompletedEvent.Success` is false, the projected report is failed, the AGUI/SSE mapper emits one error terminal, and it emits no finished terminal.

- [ ] **Step 5: Run focused and integration tests to verify GREEN**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~NyxIdProxyToolExactIdentityTests
dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~NyxIdMissingServiceId
```

Expected: all new regressions pass.

- [ ] **Step 6: Commit the slice**

```bash
git add src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs test/Aevatar.AI.Tests/NyxIdProxyToolExactIdentityTests.cs test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs
git commit -m "Propagate NyxID proxy validation failures"
```

### Task 2: Typed Task, Control, Action Contracts and Pure Transition Policy

**Files:**
- Create: `agents/Aevatar.GAgents.NyxidChat/protos/nyxid_chat_task.proto`
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTaskTransitionPolicy.cs`
- Create: `test/Aevatar.AI.Tests/NyxIdChatTaskContractTests.cs`
- Create: `test/Aevatar.AI.Tests/NyxIdChatTaskTransitionPolicyTests.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/Aevatar.GAgents.NyxidChat.csproj`

**Interfaces:**
- Consumes: existing `ChatRequestEvent`, `AgentToolReceipt`, `AgentToolExecutionContextPayload`, `LLMControlContextPayload`, `ChatContentPart`, and `AgentProfileSnapshot` contracts.
- Produces: `NyxIdChatConversationGAgentState`, `NyxIdChatTurnState`, closed task/step/effect/action enums, typed commands/events/signals, safe resource-ref `oneof`, and pure `NyxIdChatTaskTransitionPolicy` decisions.

- [ ] **Step 1: Write protobuf round-trip and identity-separation tests**

Construct a state using distinct identity shapes and round-trip it through `ToByteArray`/`Parser.ParseFrom`. Assert task statuses (`active/succeeded/failed/stopped/blocked`), step statuses (`planned/waiting/running/done/failed/skipped/cancelled/uncertain`), effect evidence (`not_started/not_applied/confirmed/may_have_changed`), and action dispositions (`completed/declined/failed/cancelled/expired`) are all represented by closed enums, not strings or bags.

- [ ] **Step 2: Define the actor-owned protobuf contract**

The state root must contain a typed `role_configuration`, scope/profile binding, active/latest turn, bounded terminal summaries, active task, pending approval/action, control fence, continuation admission, and progress sequence. Operation signals carry this full key:

```protobuf
message NyxIdChatOperationKey {
  string conversation_actor_id = 1;
  string turn_id = 2;
  string task_id = 3;
  string step_id = 4;
  string operation_id = 5;
  int64 operation_generation = 6;
}
```

Use typed `oneof` fields for operation input/result and safe resource references. Do not include access tokens, headers, raw upstream bodies, arbitrary metadata, or credential-bearing URI fields.

- [ ] **Step 3: Write the transition matrix tests to verify RED**

Cover every legal forward transition and explicitly reject terminal regressions, two running generations for one step, identity mismatch, required failed/uncertain success, unsafe retry, unsafe skip, post-fence starts, and browser completion without verified postcondition.

- [ ] **Step 4: Implement the pure transition policy**

Expose focused methods with no I/O:

```csharp
public static NyxIdChatTransitionDecision StartOperation(
    NyxIdChatConversationGAgentState state,
    NyxIdChatOperationKey key,
    NyxIdChatStepKind kind,
    bool mayChangeExternalState);

public static NyxIdChatTransitionDecision ReconcileOperation(
    NyxIdChatConversationGAgentState state,
    NyxIdChatOperationResultSignal signal);

public static NyxIdChatAvailableActions ResolveAvailableActions(
    NyxIdChatTaskStepState step);
```

Return typed accepted/rejected/idempotent decisions with stable safe reason codes; never mutate the input state.

- [ ] **Step 5: Run contract/policy tests to verify GREEN**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NyxIdChatTaskContractTests|FullyQualifiedName~NyxIdChatTaskTransitionPolicyTests"
```

Expected: all transition and serialization cases pass.

- [ ] **Step 6: Commit the slice**

```bash
git add agents/Aevatar.GAgents.NyxidChat/protos/nyxid_chat_task.proto agents/Aevatar.GAgents.NyxidChat/NyxIdChatTaskTransitionPolicy.cs agents/Aevatar.GAgents.NyxidChat/Aevatar.GAgents.NyxidChat.csproj test/Aevatar.AI.Tests/NyxIdChatTaskContractTests.cs test/Aevatar.AI.Tests/NyxIdChatTaskTransitionPolicyTests.cs
git commit -m "Define NyxIdChat task control contracts"
```

### Task 3: Responsive Conversation Controller and Run-Scoped Single-Operation Actor

**Files:**
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs`
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTurnGAgent.cs`
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTurnOperationExecutor.cs`
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTurnActorIds.cs`
- Create: `test/Aevatar.AI.Tests/NyxIdChatConversationGAgentTests.cs`
- Create: `test/Aevatar.AI.Tests/NyxIdChatTurnGAgentTests.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatLifecycleFacade.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatServiceDefaults.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `IActorRuntime`, `IActorDispatchPort`, `IAgentRunReplyGenerationExecutorPort`, accepted-only `DispatchAdmission`, and the Task 2 protobuf commands/results.
- Produces: canonical `NyxIdChatConversationGAgent` authority and short-lived `NyxIdChatTurnGAgent`, with one committed operation waterline per provider/tool I/O.

- [ ] **Step 1: Write controller responsiveness and dispatch-order tests**

Use a controlled operation executor whose LLM `TaskCompletionSource` stays incomplete. Dispatch a chat command, wait for the operation dispatch signal, then dispatch a stop/control command to the controller and prove it commits while the turn actor remains blocked. Assert the operation-requested fact is committed before `IActorDispatchPort.DispatchAsync` is called.

- [ ] **Step 2: Write turn-actor single-operation tests**

Assert one LLM command performs one streaming LLM step and returns one typed result signal without automatically running a tool; one later authorized tool command performs one tool step. Duplicate/stale commands no-op, and a missing transient authorized-tool capability after restart returns a typed `not_started`/recovery result instead of guessing.

- [ ] **Step 3: Implement stable opaque turn actor IDs**

Derive an address from server-owned identities solely as an opaque reuse key:

```csharp
public static string ForTurn(string conversationActorId, string turnId) =>
    $"nyxid-chat-turn:{StableHash.Compose(conversationActorId, turnId)}";
```

No caller may parse the address or infer task facts from its text.

- [ ] **Step 4: Implement the turn actor and operation executor**

The turn actor persists only its operation admission/delivery waterline. It delegates one LLM or one tool step to the existing focused executor primitives, streams progress back as typed signals, and sends one terminal result to the controller. Runtime credentials arrive only on transient command messages and are stripped at the commit funnel.

- [ ] **Step 5: Implement the canonical conversation controller**

On chat admission, atomically persist scope/turn/task/initial-step and the first operation waterline, create/link the turn actor, then dispatch one transient operation command. Handle child result signals by validating all six key fields, committing the result, and only then deciding whether another operation may be authorized.

- [ ] **Step 6: Move public lifecycle creation to the controller**

`NyxIdChatConversationCreateCommandTargetResolver` creates `NyxIdChatConversationGAgent` under the public actor ID. Retain the existing long-loop `NyxIdChatGAgent` only as reusable/legacy execution behavior while the new turn actor becomes the v1 public execution path. Register both kinds explicitly and update tests so only the controller uses `NyxIdChatServiceDefaults.GAgentKind`.

- [ ] **Step 7: Run focused actor tests to verify GREEN**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NyxIdChatConversationGAgentTests|FullyQualifiedName~NyxIdChatTurnGAgentTests|FullyQualifiedName~NyxIdChatGAgentTests"
```

Expected: controller, single-operation actor, and retained profile regressions pass.

- [ ] **Step 8: Commit the slice**

```bash
git add agents/Aevatar.GAgents.NyxidChat test/Aevatar.AI.Tests/NyxIdChatConversationGAgentTests.cs test/Aevatar.AI.Tests/NyxIdChatTurnGAgentTests.cs
git commit -m "Make NyxIdChat execution actor responsive"
```

### Task 4: Actor-Owned Task Lifecycle and Honest Terminal Projection

**Files:**
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTaskLifecycle.cs`
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationAguiFrameBuilder.cs`
- Create: `test/Aevatar.AI.Tests/NyxIdChatTaskLifecycleTests.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatProjectionSession.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatCommittedStateProjectionActivationPlanProvider.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatAguiSseEventWriter.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatProjectionSessionTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatStreamIdentityAndTerminalTests.cs`

**Interfaces:**
- Consumes: typed LLM/tool progress/results and Task 2 transition policy.
- Produces: committed task/step progress custom frames, exact failure/stopped/blocked terminal frames, and actor-computed safe actions/effect evidence.

- [ ] **Step 1: Write multi-step success/failure/effect tests**

Cover plan creation from typed tool calls, `planned -> running -> done`, required tool failure before effect, confirmed effect, uncertain effect, approval waiting/denied/expired, optional safe skip, idempotent safe retry, and required failure/uncertainty preventing successor dispatch and task success.

- [ ] **Step 2: Implement lifecycle derivation**

Map typed operation facts and receipts into task steps. Never inspect arbitrary result JSON or model prose. External-effect evidence derives only from operation-start waterline, `AgentToolReceipt`, safety/idempotency fields, and later typed postcondition evidence.

- [ ] **Step 3: Emit task/step frames from committed controller facts**

Add AGUI custom events backed by protobuf payloads:

```text
nyxid.task.snapshot
nyxid.task.step.changed
nyxid.control.changed
nyxid.action.request
```

The projector consumes controller committed events only. The endpoint does not synthesize progress or terminal frames.

- [ ] **Step 4: Enforce one honest terminal**

Failed tasks emit `RunError` only, stopped tasks emit stopped semantics, browser/authorization handoffs emit `RunFinished(Blocked)`, and succeeded tasks emit `RunFinished(Completed)`. Assert no task can emit both success and failure terminal frames.

- [ ] **Step 5: Run lifecycle/projection/SSE tests**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NyxIdChatTaskLifecycleTests|FullyQualifiedName~NyxIdChatProjectionSessionTests|FullyQualifiedName~NyxIdChatStreamIdentityAndTerminalTests"
```

Expected: all typed lifecycle and terminal consistency cases pass.

- [ ] **Step 6: Commit the slice**

```bash
git add agents/Aevatar.GAgents.NyxidChat test/Aevatar.AI.Tests
git commit -m "Project honest NyxIdChat task lifecycle"
```

### Task 5: Stop, Steering, Retry, and Skip Commands

**Files:**
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatControlCommands.cs`
- Create: `test/Aevatar.AI.Tests/NyxIdChatControlCommandTests.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatInteraction.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs`
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/ScopeGAgents/GAgentRegistryPorts.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatEndpointsCoverageTests.cs`

**Interfaces:**
- Consumes: Task 2 stop/steering/retry/skip protobuf commands and Task 3 controller state.
- Produces: accepted-only command receipts, durable fences/results, `ACTIVE_TURN_REQUIRES_STEERING`, and canonical control routes.

- [ ] **Step 1: Write stop and steering actor tests**

Cover stop before dispatch, during LLM, during cancellable/uncancellable tool, between steps, duplicate/stale/wrong-turn commands, late LLM/tool results, two simultaneous steering requests, steering after terminal, disconnect without stop, and restart at each committed fence.

- [ ] **Step 2: Write endpoint admission/receipt tests**

Assert the five routes are registered, scope/conversation admission is checked, malformed IDs fail before dispatch, and successful commands return `202 Accepted` with request/command/correlation IDs plus the canonical state URL.

- [ ] **Step 3: Implement actor control fences**

On stop/steering, the controller first commits a fence. It does not dispatch another old-plan operation after the commit. Late model output is discarded; late exact tool evidence may refine `external_effect` but cannot advance the old task. An unprovably cancelled effect-capable operation becomes `uncertain`/`may_have_changed`.

- [ ] **Step 4: Implement steering and normal-stream admission**

Ordinary stream submission against an active controller returns typed `ACTIVE_TURN_REQUIRES_STEERING`. Accepted steering creates a server-owned `continuationTurnId` only at a safe checkpoint, preserves completed/effect facts, and never re-executes them.

- [ ] **Step 5: Implement safe retry/skip**

Retry/skip commands validate path `turnId`/`stepId`, current actor state, expected generation/version, and actor-derived available actions. Same request replays idempotently; same ID with different content fails closed.

- [ ] **Step 6: Run control and endpoint tests**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NyxIdChatControlCommandTests|FullyQualifiedName~NyxIdChatEndpointsCoverageTests|FullyQualifiedName~NyxIdChatStreamIdentityAndTerminalTests"
```

Expected: all stop/steering/retry/skip cases pass.

- [ ] **Step 7: Commit the slice**

```bash
git add agents/Aevatar.GAgents.NyxidChat src/platform/Aevatar.GAgentService.Abstractions/ScopeGAgents/GAgentRegistryPorts.cs test/Aevatar.AI.Tests
git commit -m "Add actor-owned NyxIdChat controls"
```

### Task 6: NyxID Browser Action Request, Continuation, and Postcondition Reconciliation

**Files:**
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdAssistantActionRegistry.cs`
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdActionPostconditionPort.cs`
- Create: `agents/Aevatar.GAgents.NyxidChat/NyxIdActionSecretPolicy.cs`
- Create: `test/Aevatar.AI.Tests/NyxIdAssistantActionRegistryTests.cs`
- Create: `test/Aevatar.AI.Tests/NyxIdChatBrowserActionTests.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationAguiFrameBuilder.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: startup-snapshotted NyxID action registry, typed action params, safe resource refs, and action-specific read-model postcondition ports.
- Produces: committed `nyxid.action.request`, blocked origin turn, authenticated action continuation admission, and verified step outcomes.

- [ ] **Step 1: Write registry and parameter validation tests**

Cover schema revision 4, unknown revision/action, unsupported tier, undeclared fields, distinct catalog/custom `service.connect` variants, patch schemas, advisory risk, and rejection of caller-supplied risk downgrade.

- [ ] **Step 2: Write action state-machine tests**

Cover request idempotency/content conflict, commit-before-frame observation, blocked origin turn, every disposition, duplicate/partial/out-of-order reports, cross-scope/conversation/origin rejection, continuation during another active turn, and restart/reload.

- [ ] **Step 3: Implement registry snapshot and safe params**

Load/validate the registry once at host startup. Convert JSON schema descriptors into closed typed internal action definitions. Model catalog service and custom endpoint variants with protobuf `oneof`; do not use `custom: true` to change field meaning.

- [ ] **Step 4: Implement action handoff and wire frame**

Atomically commit `actionRequestId`, waiting/blocked step, and origin turn terminal before projecting:

```json
{"name":"nyxid.action.request","payload":{"schemaVersion":4,"actorId":"conversation-alpha","originTurnId":"turn-alpha","taskId":"task-alpha","stepId":"step-alpha","actionRequestId":"action-alpha","action":"service.connect","params":{"catalogService":{"serviceSlug":"api-github","requestedScopes":["repo"]}}}}
```

Outer AGUI remains `payload`; inner action arguments are `params`.

- [ ] **Step 5: Implement action continuation and typed postcondition reads**

`action.continue` is a discriminated authenticated stream input with its own `clientRequestId`. The server creates a new turn. A `completed` report remains waiting/blocked until the action-specific read model proves the exact resource state; missing/stale/unavailable/mismatched state never becomes success.

- [ ] **Step 6: Enforce the secret and URL boundary**

Reject or redact forbidden field names and values before actor dispatch. Custom URLs reject userinfo and, in v1, all query/fragment components. Prove state/read model/SSE/log/audit-safe payloads contain none of the forbidden values. `device.approve.user_code` is never accepted by Aevatar.

- [ ] **Step 7: Run browser-action tests**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NyxIdAssistantActionRegistryTests|FullyQualifiedName~NyxIdChatBrowserActionTests|FullyQualifiedName~NyxIdChatAguiSseEventWriterTests"
```

Expected: registry, handoff, postcondition, idempotency, and secret-boundary tests pass.

- [ ] **Step 8: Commit the slice**

```bash
git add agents/Aevatar.GAgents.NyxidChat test/Aevatar.AI.Tests
git commit -m "Add NyxID browser action handoff"
```

### Task 7: Durable Current-State Materialization and Conditional Query

**Files:**
- Create: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/INyxIdChatConversationStateQueryPort.cs`
- Create: `src/Aevatar.Studio.Infrastructure/ActorBacked/ProjectionNyxIdChatConversationStateQueryPort.cs`
- Create: `src/Aevatar.Studio.Projection/Projectors/NyxIdChatConversationCurrentStateProjector.cs`
- Create: `src/Aevatar.Studio.Projection/Metadata/NyxIdChatConversationCurrentStateDocumentMetadataProvider.cs`
- Create: `src/Aevatar.Studio.Projection/ReadModels/NyxIdChatConversationCurrentStateDocument.Partial.cs`
- Create: `test/Aevatar.Studio.Tests/NyxIdChatConversationCurrentStateProjectorTests.cs`
- Create: `test/Aevatar.Studio.Tests/ProjectionNyxIdChatConversationStateQueryPortTests.cs`
- Modify: `src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto`
- Modify: `src/Aevatar.Studio.Projection/Aevatar.Studio.Projection.csproj`
- Modify: `src/Aevatar.Studio.Projection/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/Aevatar.Studio.Projection/Orchestration/StudioCommittedStateProjectionActivationPlanProvider.cs`
- Modify: `src/Aevatar.Studio.Infrastructure/Aevatar.Studio.Infrastructure.csproj`
- Modify: `src/Aevatar.Studio.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/Aevatar.Studio.Hosting/StudioProjectionReadModelServiceCollectionExtensions.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatEndpointsCoverageTests.cs`

**Interfaces:**
- Consumes: `CommittedStateEventPublished<NyxIdChatConversationGAgentState>` from the canonical actor.
- Produces: `NyxIdChatConversationCurrentStateDocument`, `INyxIdChatConversationStateQueryPort.GetAsync`, and HTTP `current/not_modified/reload_required` outcomes.

- [ ] **Step 1: Write projector monotonicity tests**

Project a committed controller state and assert query-shaped safe fields, exact actor `StateVersion`, ordered steps, active operation, fences, pending approval/actions, and progress sequence. Use the standard store to prove newer overwrite, equal-byte idempotency, equal-version conflict, and old-version rejection.

- [ ] **Step 2: Define the query-shaped read model**

Add a distinct `NyxIdChatConversationCurrentStateDocument`; do not extend terminal transcript `ChatConversationCurrentStateDocument`. Copy only stable safe controller fields and never pack the full internal state into `Any`.

- [ ] **Step 3: Register durable materialization**

Add the controller actor type/projection kind to the Studio activation provider, register materializer/metadata/document stores, and include the state descriptor in the Elasticsearch type registry. Projection consumes committed controller state only.

- [ ] **Step 4: Write conditional query tests**

Assert:

```text
server > afterStateVersion                       => current + full snapshot
server == afterStateVersion and turn matches     => not_modified
client version in future                         => reload_required
turn/conversation mismatch or invalid version    => reload_required
missing document                                 => not_found
```

Also prove the query port has only a projection document reader dependency.

- [ ] **Step 5: Implement the read-model-only query port and endpoint**

Map `GET /api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}/state?afterStateVersion={version}&turnId={turnId}` through scope/conversation admission, then call the query port. Return typed JSON without touching runtime, event store, projection lifecycle, or live session ports.

- [ ] **Step 6: Run projection/query/endpoint tests and guards**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NyxIdChatConversationCurrentState|FullyQualifiedName~ProjectionNyxIdChatConversationStateQueryPort"
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~NyxIdChatEndpointsCoverageTests
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
```

Expected: tests and all four projection/query guards exit 0.

- [ ] **Step 7: Commit the slice**

```bash
git add src/Aevatar.Studio.Application.Abstractions src/Aevatar.Studio.Infrastructure src/Aevatar.Studio.Projection src/Aevatar.Studio.Hosting agents/Aevatar.GAgents.NyxidChat test/Aevatar.Studio.Tests test/Aevatar.AI.Tests
git commit -m "Materialize NyxIdChat conversation state"
```

### Task 8: Recovery, Security, Canonical Documentation, and Full Verification

**Files:**
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTurnGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTaskTransitionPolicy.cs`
- Modify: `tools/ci/architecture_guards.sh`
- Modify: `tools/ci/query_projection_priming_guard.sh`
- Modify: `docs/canon/nyxid-chat-api.md`
- Create: `test/Aevatar.AI.Tests/NyxIdChatRecoveryAndSecurityTests.cs`

**Interfaces:**
- Consumes: all preceding actor, action, control, projection, and query contracts.
- Produces: deterministic replay/recovery, stale/late reconciliation, reusable architecture guards, and the canonical v1 API contract.

- [ ] **Step 1: Write restart/late-result/security regressions**

Replay controller events at every waterline and assert the same snapshot without repeated I/O. Cover restart after operation-start with no result, stale generations, duplicate/conflicting facts, late model/tool results after stop/steering, blocked action recovery, and secret scans over serialized state/read model/AGUI frames.

- [ ] **Step 2: Implement activation recovery**

On activation, re-dispatch only a typed self-continuation that committed state proves is outstanding and safe. Never replay an effect-capable started operation automatically. Mark unknown effect-capable work `uncertain/may_have_changed`; a proven-not-started or explicitly idempotent operation may become actor-authorized retry only.

- [ ] **Step 3: Add architecture guards for the durable semantic rules**

Guard against:

```text
NyxIdChat state/query paths reading IEventStore or IActorRuntime
query methods attaching/priming projection sessions
service-level operation/cancellation/action dictionaries
generic JSON "error" inspection for tool failure
device.approve.user_code in Aevatar action contracts
forbidden secret fields in NyxIdChat protobuf/read models
```

- [ ] **Step 4: Update the canonical API document**

Document the identity table, task/step/effect/action states, transition rules, stop/steering/retry/skip routes, `ACTIVE_TURN_REQUIRES_STEERING`, browser action wire examples, registry/risk ownership, dispositions, postcondition proof, conditional state polling, terminal consistency, cancellation limits, and secret boundary. Use the repository-required Mermaid init directive and quoted labels.

- [ ] **Step 5: Run all targeted tests serially**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NyxIdChat|FullyQualifiedName~NyxIdProxyTool|FullyQualifiedName~RoleGAgent"
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~AgentRunGAgent|FullyQualifiedName~AgentRunReplyGenerationExecutor"
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~NyxIdChatConversation|FullyQualifiedName~ChatConversationCurrentState|FullyQualifiedName~ProjectionChatConversation|FullyQualifiedName~ActorBackedChatHistory"
dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~NyxIdMissingServiceId
```

Expected: all targeted tests pass. Run these commands serially because the projects share `bin/obj` outputs.

- [ ] **Step 6: Run mandatory guards and documentation checks**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/ci/solution_split_guards.sh
bash tools/ci/test_solution_ownership_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: every guard exits 0.

- [ ] **Step 7: Run repository build and full test**

```bash
dotnet restore aevatar.slnx --nologo
dotnet build aevatar.slnx --no-restore --nologo
dotnet test aevatar.slnx --no-restore --nologo
```

Expected: build and full test pass; existing package/analyzer warnings may remain, but no new error or failed test is accepted.

- [ ] **Step 8: Inspect and commit the final implementation**

```bash
git diff --check
git status --short
git diff --stat
git add agents src test tools docs/canon/nyxid-chat-api.md
git commit -m "Complete NyxID Assistant chat v1"
```

Expected: `.superpowers/` remains untracked and unstaged; only milestone implementation/docs/tests are committed.

- [ ] **Step 9: Merge the latest target and push directly**

```bash
git fetch origin feature/integrate
git merge --no-edit origin/feature/integrate
dotnet build aevatar.slnx --no-restore --nologo
dotnet test aevatar.slnx --no-restore --nologo
git push origin feature/integrate
```

Expected: no force push; `origin/feature/integrate` contains every milestone commit and local status is clean apart from the preserved untracked `.superpowers/` directory.
