# NyxIdChat Authorization And Replay Corrections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Narrow NyxID authorization blockers to invalid credentials and make completed-turn replay stable for external history.

**Architecture:** NyxID proxy tools normalize every outer proxy failure into a safe typed receipt, but only the stable `401/unauthorized/1001` contract carries `AuthorizationRequired`; disconnected services continue through `nyxid_require_service`. RoleGAgent owns one protobuf terminal timestamp, persists it with the completion state, republishes it on replay, and NyxIdChat passes it unchanged to the history command path.

**Tech Stack:** .NET 10, C#, protobuf, xUnit, FluentAssertions, actor event sourcing, CQRS projection, AGUI SSE.

## Global Constraints

- Modify only `/Users/eanzhao/Code/aevatar`; `../NyxID` remains read-only.
- Preserve strict history payload equality and the completion replay required by per-turn projection.
- Do not query or prime a read model before appending history.
- Never classify by human-readable NyxID messages or persist/stream raw proxy error bodies.
- Add tests first, observe the expected failures, then implement the minimum production change.
- Do not commit unless the user explicitly requests a commit.

---

### Task 1: Safe NyxID Proxy Failure Classification

**Files:**
- Modify: `test/Aevatar.AI.Tests/NyxIdApiClientAuthorizationTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdConnectedServiceToolSourceTests.cs`
- Modify: `test/Aevatar.AI.Tests/ToolProviderHttpClientRegistrationTests.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdAuthorizationReceiptFactory.cs`
- Modify: `src/Aevatar.AI.Core/Tools/StreamingToolExecutor.cs`

**Interfaces:**
- Consumes: outer proxy envelope `{ error, status, body }` and optional typed NyxID `{ error, error_code }` body fields.
- Produces: `AuthorizationRequired` only for `401/unauthorized/1001`; safe `Error` receipts for every other proxy failure.

- [ ] Add theory cases proving `403/forbidden/1002`, scoped 403, and ordinary upstream 403 are not authorization blockers.
- [ ] Add tool tests proving 401 blocks, 403 fails normally, `nyxid_require_service` still blocks, and raw body/query secrets are absent from receipt/result text.
- [ ] Run the focused tests and verify they fail on the current 403 classification and raw proxy result.
- [ ] Parse the outer failure without requiring an inner message; retain typed key/code only when structurally present.
- [ ] Build a safe result receipt for all parsed proxy failures, with `ResultJson` containing only stable code and safe text.
- [ ] Make the streaming executor use a non-success provider receipt's safe `ResultJson` as the tool result passed into history and later output.
- [ ] Re-run the focused tests and verify they pass.

### Task 2: Actor-Owned Terminal Time

**Files:**
- Modify: `test/Aevatar.AI.Tests/AIAbstractionsProtoCoverageTests.cs`
- Modify: `test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs`
- Modify: `test/Aevatar.Studio.Tests/ActorBackedChatHistoryStoreTests.cs`
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AI.Core/RoleGAgent.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatGAgent.cs`

**Interfaces:**
- Produces: `RoleChatSessionCompletedEvent.TerminalTime` tag 16 and `RoleChatSessionState.TerminalTime` tag 17.
- Consumes: the original completion clock value; replay and history mapping only copy it.

- [ ] Add protobuf round-trip and replay tests asserting the first and replayed completion timestamps are equal and provider execution remains one.
- [ ] Add a NyxIdChat retry test asserting two identical archive commands carry the same timestamp, the provider runs once, and a later turn includes prior history.
- [ ] Add history-store coverage proving identical mapped messages create identical `ChatTurn.TerminalTime` values.
- [ ] Run the focused tests and verify they fail because completion state has no terminal timestamp and archival samples its clock twice.
- [ ] Add the two typed protobuf fields, assign the timestamp on every authoritative RoleGAgent completion path, persist it in the reducer, and clone it on replay.
- [ ] Make NyxIdChat archival require the persisted terminal timestamp and remove its independent completion clock sample.
- [ ] Re-run the focused tests and verify they pass.

### Task 3: Combined Idempotency And Security Regression

**Files:**
- Modify: `test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs`
- Modify: `test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs`
- Modify: `test/Aevatar.Studio.Tests/ChatConversationGAgentAppendTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatProjectionSessionTests.cs`

**Interfaces:**
- Consumes: stable server turn id, replayed committed completion, deterministic history payload.
- Produces: terminal output for both requests, one provider/tool invocation, one external turn, no append rejection, and normal continuation for a new request id.

- [ ] Add combined regression assertions across role replay, projection terminal frames, and the real history actor's duplicate path.
- [ ] Run the combined focused tests and verify the pre-fix conflict/rejection behavior is reproduced.
- [ ] Apply only integration adjustments required by the tests; do not weaken `HasSamePayload` or swallow delivery rejection.
- [ ] Re-run the combined tests and verify one turn, unchanged message counts, no `ChatTurnAppendRejectedEvent`, and a later distinct turn.

### Task 4: Documentation And Verification

**Files:**
- Modify: `docs/canon/nyxid-chat-api.md`
- Modify: `docs/canon/nyxid-connected-service-tools.md`

- [ ] Document that only 401/1001 indicates invalid credentials and 403/1002 remains a normal failure.
- [ ] Document actor-owned terminal time and idempotent external-history replay.
- [ ] Run `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo`.
- [ ] Run `dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --no-restore --nologo`.
- [ ] Run `bash tools/ci/test_stability_guards.sh`.
- [ ] Run `bash tools/ci/architecture_guards.sh`.
- [ ] Inspect the final diff for unrelated changes and secret-bearing fixtures outside tests.
