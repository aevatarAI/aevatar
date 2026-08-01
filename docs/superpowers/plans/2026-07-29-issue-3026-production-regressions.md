# Issue #3026 Production Regression Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:systematic-debugging, superpowers:test-driven-development, aevatar-prod-verify, aevatar-prod-logs, and superpowers:verification-before-completion. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the two production-only #3026 failures: authenticated NyxID chat identity not reaching `nyxid_require_service`, and signal-only `action.continue` hanging when no pending actions remain.

**Architecture:** Preserve the existing Mainnet facade, NyxIdChat actor, receipt mapper, projection pipeline, and postcondition port. Carry principal-derived owner identity through the existing chat command into the typed tool context; make `nyxid_require_service` context/readiness errors produce a typed error receipt; and make the actor-owned browser-action state machine commit a successful zero-step continuation when an empty wake finds no pending actions.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, protobuf actor commands/events, xUnit, FluentAssertions, NyxID CLI, read-only Kubernetes logs.

## Global Constraints

- Scope and NyxID caller identity come only from the authenticated principal/request context, never request JSON or route authority.
- Keep one `NyxIdChatConversationGAgent` authority, one projection pipeline, and the existing schema-v4 `service.connect` registry mapping.
- Do not special-case AWS, provider names, or service slugs.
- `actions=[]` carries no origin turn, disposition, resource, or mutation authority.
- Preserve exact retry idempotency and fail closed on conflicting reuse.
- Do not weaken assistant-action registry startup validation or copy the NyxID registry into Aevatar.
- Do not touch the pre-existing user changes listed in the task attachment.

---

### Task 1: Principal-Derived Tool Authority

**Files:**
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatInteraction.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdRequireServiceTool.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatPublicEndpointsTests.cs`
- Modify: `test/Aevatar.AI.Tests/ToolProviderHttpClientRegistrationTests.cs`

**Interfaces:**
- Consumes: authenticated `ClaimsPrincipal`, `NyxIdChatCommand`, `AgentToolExecutionContextPayload`, live NyxID UserService readiness.
- Produces: `Caller.OwnerScopeId`, `Caller.OwnerSubject`, and `NyxIdAuthority` on the transient tool context; `AuthorizationRequired` only for verified registration-required results; typed error receipts for missing or unavailable authority/readiness.

- [ ] Add a public-route test proving the authenticated subject and scope enter `NyxIdChatCommand` without request-body authority.
- [ ] Add an envelope-factory test proving the principal-derived values survive into `NyxIdChatStartTurnCommand.ToolContext`.
- [ ] Add `nyxid_require_service` tests proving arbitrary missing slugs create one verified `AuthorizationRequired` receipt and missing owner scope creates a typed error receipt.
- [ ] Add `nyxid_require_service` tests proving malformed readiness and argument/result slug mismatch create typed error receipts rather than authorization or null receipts.
- [ ] Run the focused tests and confirm RED for the missing command fields/tool-context values and absent error receipt.
- [ ] Add only the required typed command fields and map them once in `NyxIdChatCommandEnvelopeFactory`.
- [ ] Return a closed typed error receipt for explicit tool error results while retaining fail-closed behavior for stale, malformed, and slug-mismatched readiness.
- [ ] Run the focused tests and confirm GREEN.

### Task 2: Signal-Only Wake Terminal

**Files:**
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatBrowserActions.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTurnOperationExecutor.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatProjectionSession.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationAguiFrameBuilder.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatBrowserActionTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatConversationGAgentTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatTurnGAgentTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatProjectionSessionTests.cs`

**Interfaces:**
- Consumes: `NyxIdChatActionContinueCommand` with empty `OriginTurnId` and empty `Actions`.
- Produces: a distinct continuation turn; pending actions become typed postcondition steps and dispatch in order; signal-only postconditions may enter the typed port with `Unspecified` and must return verified `Completed`; continuation admission projects against `ContinuationTurnId`; zero pending actions commit and project a successful zero-step terminal.

- [ ] Add a pure-state regression test for zero pending actions producing an accepted terminal continuation.
- [ ] Add an actor integration test proving the no-op continuation is committed, reserves history, and prepares terminal delivery without operation dispatch.
- [ ] Keep the existing pending-action empty-wake test as the regression for observable postcondition dispatch.
- [ ] Add an executor regression proving a signal-only `Unspecified` postcondition reaches the typed query port and can return verified `Completed`.
- [ ] Add a session projection regression proving action admission routes by `ContinuationTurnId` and emits task progress plus terminal frames for a zero-step continuation.
- [ ] Run the focused tests and confirm RED because zero pending currently returns an uncommitted invalid decision.
- [ ] Remove only the zero-pending rejection; reuse the existing `CompleteContinuationTask` path.
- [ ] Align executor validation with the typed postcondition port contract by accepting `Completed | Unspecified`, while keeping all other browser dispositions fail closed.
- [ ] Route action continuation admission by `ContinuationTurnId` and include the committed task snapshot plus terminal frames when that same committed state is terminal.
- [ ] Run the focused tests and confirm GREEN.

### Task 3: Contract and Verification

**Files:**
- Modify: `docs/canon/nyxid-chat-api.md` only if the no-pending terminal is not already explicit.

**Interfaces:**
- Produces: verified public facade, action/postcondition/current-state/history/profile behavior and an honest external registry status.

- [ ] Run the named NyxIdChat, Mainnet facade/composition, action/postcondition/current-state/history, and profile-governance tests.
- [ ] Run `bash tools/ci/test_stability_guards.sh`, projection/query guards applicable to touched tests, agent-profile governance, docs lint, formatting, build, and architecture guards.
- [ ] Confirm `GET https://nyx-api.chrono-ai.fun/api/v1/assistant/actions` deployment status without changing Aevatar defaults.
- [ ] Use a new `nyxid proxy request aevatar api/chat` canary with explicit JSON/SSE headers, then correlate read-only production logs.
- [ ] Report local evidence separately from deployment evidence; do not claim production fixed unless the running deployment contains the patch.
