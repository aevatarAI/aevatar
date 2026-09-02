# NyxIdChat Multi-Turn And Authorization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make NyxIdChat multi-turn identity server-owned, guarantee typed SSE terminals, and expose typed NyxID authorization blockers.

**Architecture:** Keep `actorId` as the conversation actor address and use a server-created `turnId` as the existing RoleGAgent session/replay key. Actor-committed protobuf facts remain the only source for projection terminals; NyxID structured errors become typed tool receipts and blocked completion facts before the AGUI adapter maps them.

**Tech Stack:** .NET 10, C#, protobuf, xUnit, FluentAssertions, actorized CQRS Projection Pipeline, AGUI SSE.

## Global Constraints

- Modify only `/Users/eanzhao/Code/aevatar`; `../NyxID` is read-only contract evidence.
- Do not relax `RoleGAgent.ResolveTrackedSession` prompt/input equality.
- Do not add process-local actor/session/idempotency registries.
- Keep command, correlation, actor, turn, client request, and approval request identities distinct.
- Add every behavior test before its implementation and verify the expected RED failure.
- Do not expose credentials or raw internal exception messages in SSE.
- Do not commit unless the user explicitly requests a commit.

---

### Task 1: Server-Owned Turn Identity

**Files:**
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatInteraction.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatEndpointsCoverageTests.cs`

**Interfaces:**
- Produces: `NyxIdChatStreamRequest.ClientRequestId`, `NyxIdChatCommand.TurnId`, server turn-id resolver.
- Consumes: path `actorId`, optional body/header idempotency identity.

- [ ] Add endpoint tests asserting fresh turn ids for ordinary submissions and ignored repeated legacy session ids.
- [ ] Run the focused tests and confirm they fail because commands currently reuse `SessionId`.
- [ ] Add `clientRequestId`, generate random turn ids without it, and derive stable actor-scoped turn ids with it.
- [ ] Rename internal command/receipt observation semantics from session to turn while mapping `TurnId` to `ChatRequestEvent.SessionId`.
- [ ] Run the focused tests and confirm they pass.

### Task 2: Replay And Typed Conflict

**Files:**
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AI.Core/RoleGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatProjectionSession.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatCompletionAguiFrameBuilder.cs`
- Test: `test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatProjectionSessionTests.cs`

**Interfaces:**
- Produces: committed `RoleChatSessionConflictEvent` and AGUI `RUN_ERROR(code=IDEMPOTENCY_CONFLICT)`.
- Consumes: RoleGAgent's unchanged prompt/input equality invariant.

- [ ] Add a test proving identical turn/input executes the provider once and replays the committed result.
- [ ] Add a test proving changed input commits a conflict without replacing the original session state.
- [ ] Add a projector test for the conflict terminal frame.
- [ ] Run all three tests and confirm RED for the missing typed event/mapping.
- [ ] Add the protobuf conflict fact, typed internal conflict exception, handler commit, activation, and projector mapping.
- [ ] Re-run the tests and confirm GREEN.

### Task 3: Complete SSE Identity And Failure Terminals

**Files:**
- Modify: `src/Aevatar.AGUI.Contracts/agui_events.proto`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatSseWriter.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatAguiSseEventWriter.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs`
- Modify: `src/Aevatar.AI.Core/RoleGAgent.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatAguiSseEventWriterTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatEndpointsCoverageTests.cs`
- Test: `test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs`

**Interfaces:**
- Produces: `RUN_STARTED/FINISHED/ERROR` with stable turn id and typed code/status.
- Consumes: committed completion/conflict frames and endpoint-visible infrastructure failures.

- [ ] Add writer tests for turn id, error code, completion status, and credential-free generic failures.
- [ ] Add an endpoint test whose interaction fails after start and assert one terminal frame followed by no keepalive.
- [ ] Add a RoleGAgent test for an exception outside stream enumeration becoming a committed safe failure.
- [ ] Run focused tests and confirm RED.
- [ ] Extend AGUI completion status, writer serialization, endpoint error codes, and full RoleGAgent handler guard.
- [ ] Re-run focused tests and confirm GREEN.

### Task 4: Typed NyxID Authorization Receipt

**Files:**
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AI.Abstractions/ToolProviders/IAgentTool.cs`
- Modify: `src/Aevatar.AI.Core/Tools/AgentToolReceiptFactory.cs`
- Modify: `src/Aevatar.AI.Core/Tools/StreamingToolExecutor.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/ConnectedServiceProxyTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdConnectedServiceToolSourceTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdApiClientCoverageTests.cs`

**Interfaces:**
- Produces: `AgentToolReceipt.AuthorizationRequired` populated from structured NyxID status/key/code.
- Consumes: external NyxID `ErrorResponse` and proxy wrapper JSON.

- [ ] Add tests using nested NyxID `forbidden/1002` and `unauthorized/1001` response envelopes.
- [ ] Assert the receipt contains service identity and safe fields but no bearer token or credential material.
- [ ] Run tests and confirm RED because the result is currently a normal JSON string.
- [ ] Add the protobuf blocker, general result-receipt hook, structured parser/classifier, and NyxID tool mappings.
- [ ] Re-run tests and confirm GREEN.

### Task 5: Blocked Completion Projection

**Files:**
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AI.Core/RoleGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatCompletionAguiFrameBuilder.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatAguiSseEventWriter.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatSseWriter.cs`
- Test: `test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatProjectionSessionTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatAguiSseEventWriterTests.cs`

**Interfaces:**
- Produces: typed completion outcome `BLOCKED`, custom `nyxid.authorization.required`, and `RUN_FINISHED(BLOCKED)`.
- Consumes: typed authorization receipt from Task 4.

- [ ] Add actor, projector, and SSE adapter tests for the blocked sequence and safe payload.
- [ ] Run focused tests and confirm RED.
- [ ] Derive completion outcome from receipts, persist the blocker, and map it at the AGUI boundary.
- [ ] Re-run focused tests and confirm GREEN.

### Task 6: Deterministic Missing-Service Tool

**Files:**
- Create: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdRequireServiceTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdAgentToolSource.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/Skills/system-prompt.md`
- Test: `test/Aevatar.AI.Tests/NyxIdConnectedServiceToolSourceTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatSystemPromptTests.cs`

**Interfaces:**
- Produces: `nyxid_require_service` and the same typed authorization receipt without an external service operation.
- Consumes: model-provided service slug/optional label; emits fixed safe reason/message semantics.

- [ ] Add source/tool tests for a missing service and prompt-contract tests requiring the typed tool path.
- [ ] Run tests and confirm RED.
- [ ] Implement and register the tool, sanitize its public fields, and update the embedded prompt.
- [ ] Re-run tests and confirm GREEN.

### Task 7: Approval Continuation Identity

**Files:**
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AI.Core/RoleGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatInteraction.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatEndpointsCoverageTests.cs`
- Test: `test/Aevatar.AI.Tests/RoleGAgentRemoteApprovalEscalationTests.cs`

**Interfaces:**
- Produces: server-owned approval continuation turn and typed pending scope restoration.
- Consumes: pending `requestId`; legacy approval session is ignored.

- [ ] Add tests proving the endpoint-generated turn is observed and the actor resumes only the matching pending request.
- [ ] Add a stale request test proving it cannot affect pending state.
- [ ] Run tests and confirm RED.
- [ ] Add continuation turn/scope protobuf fields and update endpoint, envelope, and actor continuation logic.
- [ ] Re-run tests and confirm GREEN.

### Task 8: Multi-Turn And Blocked History

**Files:**
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatGAgent.cs`
- Modify: `agents/Aevatar.GAgents.ChatHistory/chat_history_messages.proto`
- Modify: `src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedChatHistoryStore.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs`
- Test: `test/Aevatar.Studio.Tests/ChatHistoryEndpointsTests.cs`

**Interfaces:**
- Produces: turn-derived user/assistant ids and blocked archive status.
- Consumes: Role session outcome and shared in-actor `ChatHistory`.

- [ ] Add a two-turn test asserting distinct ids and first-turn messages in the second LLM request.
- [ ] Add blocked-turn history mapping and next-turn admission tests.
- [ ] Run tests and confirm RED.
- [ ] Map typed outcomes to history without clearing/deactivating the conversation.
- [ ] Re-run tests and confirm GREEN.

### Task 9: Documentation And Verification

**Files:**
- Create: `docs/canon/nyxid-chat-api.md`
- Modify: `docs/README.md`
- Modify: `docs/canon/nyxid-connected-service-tools.md`

- [ ] Document actor, turn, client idempotency, command/correlation, and approval request identities.
- [ ] Document deprecated legacy session behavior and caller migration.
- [ ] Document authorization custom and blocked terminal frames.
- [ ] Run focused NyxIdChat/Role/tool/history tests.
- [ ] Run `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo`.
- [ ] Run `bash tools/ci/test_stability_guards.sh`.
- [ ] Run `bash tools/ci/architecture_guards.sh` and relevant projection guards.
- [ ] Run necessary project/solution builds and inspect the final diff.
