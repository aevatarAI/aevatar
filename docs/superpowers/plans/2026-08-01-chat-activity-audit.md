# Chat Activity Audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose sanitized `POST /api/chat` tool calls and NyxID browser-action activity under `/admin`, with personal-by-default authorization, explicit platform-admin all-user access, and 30-day retention.

**Architecture:** Extend the existing typed Audit Trail contract and Elasticsearch `audit-trail-current` store; do not add a ChatLog store, actor, read model, or projection path. Tool facts stay on `ToolExecutionAuditMiddleware`; NyxID action facts are translated from committed actor events by the existing actor-scoped `StudioMaterializationContext`; `GET /api/audit/chat-activity` fixes trusted scope and HMAC actor identities before the store paginates.

**Tech Stack:** .NET 10, C#, Protobuf, actor-owned event sourcing, CQRS Projection Pipeline, Elasticsearch HTTP API, ASP.NET Core minimal APIs, xUnit, FluentAssertions/Shouldly, single-file HTML/JavaScript admin console, Bash/curl/jq.

## Global Constraints

- Start implementation from a freshly fetched `origin/feature/integrate`, not the current checkout: this checkout has unrelated dirty files and is materially behind the integration branch.
- Preserve or port the approved design commit `c8cedcb29` and exact-retry fix `603fa518c`; neither is currently an ancestor of `origin/feature/integrate`.
- Reuse `AuditRecord`, `IAuditTrailAppender`, `IAuditTrailQueryPort`, the `audit-trail-current` alias, fingerprinted copy-forward reconcile, and the unified Projection Pipeline.
- Do not add a ChatLog table/index, ChatActivity actor/read model, second event envelope, second projection context, retry queue, or request-time migration.
- Include tool executions from both Mainnet `POST /api/chat` branches: NyxID Assistant and Workflow Chat. Browser actions are NyxID Assistant only.
- Store no prompt, transcript, assistant text, reasoning, input part, attachment, tool arguments/results, action params, raw subject, credential, header, cookie, OAuth/device code, or secret-bearing URL.
- Keep audit capture operational and best effort: append failure is logged safely and never changes chat execution.
- Personal reads fix exactly one authenticated scope and one normalized NyxID subject on the server. They never accept caller-supplied `scope`, `auditActorId`, or `identityKeyId`.
- Platform admins get cross-user/cross-scope data only when they explicitly send `scope=__all__`; admin default remains personal activity.
- Missing/conflicting subject or scope fails closed before `IAuditTrailQueryPort`; unavailable admin authorization or query storage returns `503` without actor/event/transcript fallback.
- HMAC rotation queries the active and every configured retained identity in one storage-level Elasticsearch `terms` filter. Retired keys remain configured for at least 30 days.
- NyxID conversation ownership is actor-owned: commit it once from `FirstTurn.ToolContext.Caller.OwnerSubject`, reject later conflicts, and never let an existing ownerless actor be claimed.
- Raw owner subject remains inside the conversation actor contract/state only; it must not enter `AuditRecord`, response DTOs, public current-state read models, AGUI, transcript, logs, or errors.
- `chat.action.requested` comes only from committed `NyxIdChatActionRequestedEvent`. `chat.action.resolved` comes only from authoritative committed resolution facts.
- Caller-reported `completed` is not success. Emit success only for a verified typed postcondition; an unavailable or unverified postcondition emits no terminal action record.
- Keep at most two artifacts per action: one requested and one resolved. Audit IDs are deterministic and redelivery remains duplicate/idempotent.
- Keep `AuditContractSemantics.CurrentSchemaVersion = "1.0"`: additive protobuf fields are backward-compatible. Let the Elasticsearch mapping fingerprint trigger physical-index copy-forward; do not mark all existing `1.0` records incompatible.
- Default query `take=50`, maximum `take=200`; preserve `occurred_at DESC, audit_id ASC` cursor order and existing coverage/watermark response fields.
- Apply 30-day deletion only when typed Chat provenance exists. Do not delete unrelated governance artifacts sharing Audit Trail.
- Do not backfill legacy records or ownerless conversations.
- Use distinct identity fixtures: `user-audit-alpha`, `user-audit-beta`, `conversation-alpha`, `turn-alpha`, `task-alpha`, `step-alpha`, and `action-alpha`.
- Any test change requires `bash tools/ci/test_stability_guards.sh`; query/projection work also requires the guards listed in Task 11.

---

## File Map

| Responsibility | Files |
|---|---|
| Trusted authenticated subject | Create `src/Aevatar.Capabilities/AevatarPrincipalSubjectResolver.cs`; test in `test/Aevatar.Capabilities.Tests/AevatarPrincipalSubjectResolverTests.cs`; replace NyxID ingress resolver in `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs` |
| Audit chat contract and response | Modify `src/Aevatar.Audit.Abstractions/audit_messages.proto`, `src/Aevatar.Audit.Core/Sanitization/AuditRecordSanitizer.cs`, `src/Aevatar.Audit.Hosting/AuditTrailContracts.cs`, and `src/Aevatar.Audit.Hosting/AuditTrailResponseMapper.cs`; test in Audit Abstractions/Core/Hosting projects |
| Typed tool invocation provenance | Modify `src/Aevatar.AI.Abstractions/ai_messages.proto`, `AgentToolExecutionContext.cs`, and `AgentToolExecutionContextMapper.cs`; test in `test/Aevatar.AI.Tests` |
| NyxID attribution and owner authority | Modify `NyxIdChatInteraction.cs`, `protos/agent_run.proto`, `protos/nyxid_chat_task.proto`, `NyxIdChatConversationGAgent.cs`, and the transient authorized-tool handoff; test in `test/Aevatar.AI.Tests` and `test/Aevatar.GAgents.ChannelRuntime.Tests` |
| Workflow attribution | Modify `WorkflowCallerCredentialToolContextMapper.cs`, `WorkflowRoleGAgent.cs`, and `AgentWorkflowToolSourceAdapter.cs`; test in `test/Aevatar.Workflow.Core.Tests` |
| Tool audit record | Modify `src/Aevatar.AI.Core/Auditing/ToolAuditRecordFactory.cs`; test in `ToolExecutionAuditMiddlewareTests.cs` |
| Action audit translators | Create `src/Aevatar.Studio.Projection/Audit/NyxIdChatActionAuditTranslators.cs`, map the existing typed action enum to stable audit names, and register it in existing Studio projection DI; test in `test/Aevatar.Studio.Tests` |
| Query/store/index mapping | Modify `AuditTrailQuery.cs`, `InMemoryAuditTrailStore.cs`, `AuditTrailDocumentMetadataProvider.cs`, and `MainnetAgentProjectionDocumentStoresExtensions.cs`; test both stores |
| HMAC key rotation | Modify `IAuditActorIdentityHasher.cs` and `AuditActorIdentityHasher.cs`; test `AuditActorIdentityHasherTests.cs` |
| Personal/admin API | Modify `AuditTrailEndpoints.cs` and `AuditTrailCapabilityHostBuilderExtensions.cs`; test `AuditTrailEndpointsTests.cs` |
| Admin page | Modify `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`; test `BackendConsoleStaticAssetEndpointTests.cs` |
| Retention and rollout | Create `tools/audit/retain_chat_activity.sh`, its shell test, and `docs/operations/chat-activity-audit-retention.md`; update the Audit/Chat canon and index-cutover runbook |

### Task 1: Rebase the Implementation onto Current Integration

**Files:**

- Preserve all current user-modified and untracked files; do not stage them.
- Restore the approved design at `docs/superpowers/specs/2026-07-31-chat-activity-audit-design.md` on the fresh implementation branch.

**Interfaces:**

- Produces a clean branch named `feat/2026-08-01_chat-activity-audit` from current `origin/feature/integrate`.
- Preserves the typed Admin Audit response mapping introduced by `5f2189a18`.
- Makes `603fa518c` and `c8cedcb29` explicit ports rather than accidental merge results.

- [ ] **Step 1: Create an isolated current-baseline worktree**

```bash
git status --short --branch
PLAN_COMMIT=$(git log -1 --format=%H -- docs/superpowers/plans/2026-08-01-chat-activity-audit.md)
git fetch origin feature/integrate
git worktree add ../aevatar-chat-activity origin/feature/integrate
cd ../aevatar-chat-activity
git switch -c feat/2026-08-01_chat-activity-audit
```

Expected: the new worktree is clean and begins at the fetched integration tip. Do not stash, reset, stage, or move the four modified files and unrelated untracked directories in the original checkout.

- [ ] **Step 2: Verify the required typed Admin baseline**

```bash
git merge-base --is-ancestor 5f2189a18 HEAD
rg -n "continuationCursor|operationName|terminalOutcome" \
  src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html \
  src/Aevatar.Audit.Hosting/AuditTrailContracts.cs
```

Expected: the typed Admin Audit contract is present. If history was rewritten and ancestry fails, the semantic `rg` check must still pass before proceeding.

- [ ] **Step 3: Port the approved local commits**

```bash
git cherry-pick 603fa518c
git cherry-pick c8cedcb29
git cherry-pick "$PLAN_COMMIT"
git status --short
```

Expected: only the exact-retry change, approved design, and this implementation plan are added; the worktree is clean. `PLAN_COMMIT` is captured before leaving the original checkout so this unmerged plan is not lost. If exact-retry behavior already exists, abort that cherry-pick, prove it with its focused test, then cherry-pick the design and plan.

### Task 2: Add Typed Chat Provenance Contracts

**Files:**

- Modify: `src/Aevatar.Audit.Abstractions/audit_messages.proto`
- Modify: `src/Aevatar.AI.Abstractions/ai_messages.proto`
- Modify: `src/Aevatar.AI.Abstractions/ToolProviders/AgentToolExecutionContext.cs`
- Modify: `src/Aevatar.AI.Abstractions/ToolProviders/AgentToolExecutionContextMapper.cs`
- Modify: `src/Aevatar.Audit.Core/Sanitization/AuditRecordSanitizer.cs`
- Modify: `src/Aevatar.Audit.Hosting/AuditTrailContracts.cs`
- Modify: `src/Aevatar.Audit.Hosting/AuditTrailResponseMapper.cs`
- Test: `test/Aevatar.Audit.Abstractions.Tests/AuditRecordProtoTests.cs`
- Test: `test/Aevatar.AI.Tests/AgentToolExecutionContextMapperTests.cs`
- Test: `test/Aevatar.AI.Tests/AgentToolExecutionContextPayloadContractTests.cs`
- Test: `test/Aevatar.Audit.Core.Tests/AuditRecordSanitizerTests.cs`
- Test: `test/Aevatar.Audit.Hosting.Tests/AuditTrailEndpointsTests.cs`

**Interfaces:**

- Produces protobuf `AuditChatSurface` with `NYXID_ASSISTANT=1` and `WORKFLOW_CHAT=2`.
- Produces protobuf `AuditChatProvenance { surface, conversation_id, turn_id, task_id, step_id, action_request_id }`.
- Adds `AuditExecutionProvenance.chat = 12`; existing field numbers and schema version `1.0` remain unchanged.
- Produces C# `AgentChatInvocationSurface` and `AgentChatInvocationContext` with the same closed fields and an `Empty` value.
- Adds protobuf `AgentChatInvocationSurfacePayload`, `AgentChatInvocationContextPayload`, and `AgentToolExecutionContextPayload.chat = 17`.
- Adds nullable `Chat` to `AuditExecutionProvenanceResponse`; raw owner subject has no response field.

- [ ] **Step 1: Add failing descriptor, round-trip, and exclusion tests**

Add to `AuditRecordProtoTests.cs`:

```csharp
[Fact]
public void AuditRecord_RoundTripsTypedChatProvenanceWithoutRawIdentity()
{
    var record = CreateRecord();
    record.Provenance.Chat = new AuditChatProvenance
    {
        Surface = AuditChatSurface.NyxidAssistant,
        ConversationId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = "step-alpha",
        ActionRequestId = "action-alpha",
    };

    var parsed = AuditRecord.Parser.ParseFrom(record.ToByteArray());

    parsed.Provenance.Chat.ShouldBe(record.Provenance.Chat);
    AuditChatProvenance.Descriptor.Fields.InFieldNumberOrder()
        .Select(static field => field.Name)
        .ShouldBe(["surface", "conversation_id", "turn_id", "task_id", "step_id", "action_request_id"]);
    AuditRecord.Descriptor.Fields.InFieldNumberOrder().Select(static field => field.Name)
        .ShouldNotContain("owner_subject");
}
```

Add mapper tests that construct `AgentChatInvocationContext(NyxIdAssistant, ...)`, pass it through `ToPayload -> Parser.ParseFrom -> FromPayload`, and assert all six values survive. Assert `AgentToolExecutionContextPayload` contains field `(17, "chat")`. Extend descriptor/exclusion tests with `owner_subject`, `prompt`, `arguments_json`, `result_json`, and `params`.

Extend the current-record response assertion in `AuditTrailEndpointsTests.cs` with typed `provenance.chat`; assert the JSON contains all six safe chat fields and still contains no `ownerSubject`, prompt, arguments/results, or action params.

- [ ] **Step 2: Run contract tests and verify RED**

```bash
dotnet test test/Aevatar.Audit.Abstractions.Tests/Aevatar.Audit.Abstractions.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AuditRecordProtoTests'
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AgentToolExecutionContextMapperTests|FullyQualifiedName~AgentToolExecutionContextPayloadContractTests'
dotnet test test/Aevatar.Audit.Hosting.Tests/Aevatar.Audit.Hosting.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AuditTrailEndpointsTests'
```

Expected: compilation fails because the typed chat messages/context do not exist.

- [ ] **Step 3: Add minimal protobuf and C# contracts**

In `audit_messages.proto` add:

```proto
enum AuditChatSurface {
  AUDIT_CHAT_SURFACE_UNSPECIFIED = 0;
  AUDIT_CHAT_SURFACE_NYXID_ASSISTANT = 1;
  AUDIT_CHAT_SURFACE_WORKFLOW_CHAT = 2;
}

message AuditChatProvenance {
  AuditChatSurface surface = 1;
  string conversation_id = 2;
  string turn_id = 3;
  string task_id = 4;
  string step_id = 5;
  string action_request_id = 6;
}
```

Add `AuditChatProvenance chat = 12;` to `AuditExecutionProvenance`. Do not add owner subject and do not change `CurrentSchemaVersion`.

In `ai_messages.proto` add the closed tool-context contract and then append `AgentChatInvocationContextPayload chat = 17;` to `AgentToolExecutionContextPayload`:

```proto
enum AgentChatInvocationSurfacePayload {
  AGENT_CHAT_INVOCATION_SURFACE_PAYLOAD_UNSPECIFIED = 0;
  AGENT_CHAT_INVOCATION_SURFACE_PAYLOAD_NYXID_ASSISTANT = 1;
  AGENT_CHAT_INVOCATION_SURFACE_PAYLOAD_WORKFLOW_CHAT = 2;
}

message AgentChatInvocationContextPayload {
  AgentChatInvocationSurfacePayload surface = 1;
  string conversation_id = 2;
  string turn_id = 3;
  string task_id = 4;
  string step_id = 5;
  string action_request_id = 6;
}
```

In `AgentToolExecutionContext.cs` add:

```csharp
public enum AgentChatInvocationSurface
{
    Unspecified = 0,
    NyxIdAssistant = 1,
    WorkflowChat = 2,
}

public sealed record AgentChatInvocationContext(
    AgentChatInvocationSurface Surface,
    string? ConversationId,
    string? TurnId,
    string? TaskId,
    string? StepId,
    string? ActionRequestId)
{
    public static AgentChatInvocationContext Empty { get; } =
        new(AgentChatInvocationSurface.Unspecified, null, null, null, null, null);
}
```

Add `public AgentChatInvocationContext Chat { get; init; } = AgentChatInvocationContext.Empty;`. Map it explicitly in `ToPayload`/`FromPayload`; never put these fields in `ExternalMetadata`.

- [ ] **Step 4: Add typed response mapping and validation**

Add:

```csharp
public sealed record AuditChatProvenanceResponse(
    string Surface,
    string? ConversationId,
    string? TurnId,
    string? TaskId,
    string? StepId,
    string? ActionRequestId);
```

Append `AuditChatProvenanceResponse? Chat` to `AuditExecutionProvenanceResponse`, map stable lowercase names `nyxid_assistant` and `workflow_chat`, and return null when chat is absent/unspecified. Extend `AuditRecordSanitizer.ValidateSupplementalContracts` so a present chat block requires a specified surface; identifiers remain optional.

- [ ] **Step 5: Run tests and commit GREEN**

```bash
dotnet test test/Aevatar.Audit.Abstractions.Tests/Aevatar.Audit.Abstractions.Tests.csproj --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AgentToolExecutionContextMapperTests|FullyQualifiedName~AgentToolExecutionContextPayloadContractTests'
dotnet test test/Aevatar.Audit.Core.Tests/Aevatar.Audit.Core.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AuditRecordSanitizerTests'
dotnet test test/Aevatar.Audit.Hosting.Tests/Aevatar.Audit.Hosting.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AuditTrailEndpointsTests'
git add src/Aevatar.Audit.Abstractions/audit_messages.proto \
  src/Aevatar.AI.Abstractions/ai_messages.proto \
  src/Aevatar.AI.Abstractions/ToolProviders/AgentToolExecutionContext.cs \
  src/Aevatar.AI.Abstractions/ToolProviders/AgentToolExecutionContextMapper.cs \
  src/Aevatar.Audit.Core/Sanitization/AuditRecordSanitizer.cs \
  src/Aevatar.Audit.Hosting/AuditTrailContracts.cs src/Aevatar.Audit.Hosting/AuditTrailResponseMapper.cs \
  test/Aevatar.Audit.Abstractions.Tests/AuditRecordProtoTests.cs \
  test/Aevatar.AI.Tests/AgentToolExecutionContextMapperTests.cs \
  test/Aevatar.AI.Tests/AgentToolExecutionContextPayloadContractTests.cs \
  test/Aevatar.Audit.Core.Tests/AuditRecordSanitizerTests.cs \
  test/Aevatar.Audit.Hosting.Tests/AuditTrailEndpointsTests.cs
git commit -m "Add typed chat audit provenance"
```

### Task 3: Unify Trusted Subject Resolution and HMAC Rotation

**Files:**

- Create: `src/Aevatar.Capabilities/AevatarPrincipalSubjectResolver.cs`
- Create: `test/Aevatar.Capabilities.Tests/AevatarPrincipalSubjectResolverTests.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs`
- Modify: `src/Aevatar.Audit.Abstractions/Identity/IAuditActorIdentityHasher.cs`
- Modify: `src/Aevatar.Audit.Core/Identity/AuditActorIdentityHasher.cs`
- Modify: `test/Aevatar.Audit.Core.Tests/AuditActorIdentityHasherTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdChatEndpointsCoverageTests.cs`

**Interfaces:**

- Produces `AevatarPrincipalSubjectResolver.TryResolveNyxIdSubject(ClaimsPrincipal principal, out string subject)`.
- Recognizes `uid`, `sub`, `ClaimTypes.NameIdentifier`, and `user_id` case-insensitively; trims, removes empty values, accepts exactly one distinct ordinal value, and rejects conflicts.
- Replaces the first-match resolver in NyxID Chat ingress and is reused by Chat Activity reads in Task 8.
- Adds a default `IReadOnlyList<AuditActorIdentity> HashAll(string canonicalActorKey) => [Hash(canonicalActorKey)]`; existing fake implementations remain source-compatible, while the real hasher overrides it for active plus retained keys. `Hash` still returns only the active identity.

- [ ] **Step 1: Write failing identity-boundary tests**

Create these cases in `AevatarPrincipalSubjectResolverTests.cs`:

```csharp
[Theory]
[InlineData("uid")]
[InlineData("sub")]
[InlineData(ClaimTypes.NameIdentifier)]
[InlineData("user_id")]
public void TryResolveNyxIdSubject_WithOneRecognizedClaim_ReturnsTrimmedValue(string claimType)
```

Add tests asserting duplicate aliases with the same value succeed, `uid=user-audit-alpha` plus `sub=user-audit-beta` fails, whitespace-only fails, and unauthenticated/empty principals fail. In `AuditActorIdentityHasherTests`, configure active `key-2` plus retained `key-1`; assert `HashAll` returns active first and remaining keys ordered by key id, with distinct identities, while `Hash` equals the `key-2` entry.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AevatarPrincipalSubjectResolverTests'
dotnet test test/Aevatar.Audit.Core.Tests/Aevatar.Audit.Core.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AuditActorIdentityHasherTests'
```

Expected: resolver and `HashAll` do not exist.

- [ ] **Step 3: Implement the shared resolver and replace first-match behavior**

Use one LINQ pipeline over `principal.Claims`; no cache, service, or interface is needed. Update `NyxIdChatEndpoints.Streaming.cs` to call the shared resolver and remove its private `ResolveAuthenticatedOwnerSubject`. Add an endpoint test proving conflicting claims return `401` before dispatch.

- [ ] **Step 4: Implement retained-key identity enumeration**

Add the default interface method so the existing test fakes need no edits:

```csharp
IReadOnlyList<AuditActorIdentity> HashAll(string canonicalActorKey) =>
    [Hash(canonicalActorKey)];
```

Override it only in `AuditActorIdentityHasher`. Build every identity with existing `BuildAuditActorId`; store an explicit ordered key-id list with the active key first and remaining key IDs ordinal-sorted so dictionary enumeration is not the contract. Return a copied array, reject blank canonical keys exactly as `Hash` does, and never expose key bytes/options. Keep `Verify` unchanged. Task 8's `RecordingHasher` is the only fake that overrides `HashAll`, because its endpoint test must prove a two-key query.

- [ ] **Step 5: Run tests and commit GREEN**

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AevatarPrincipalSubjectResolverTests'
dotnet test test/Aevatar.Audit.Core.Tests/Aevatar.Audit.Core.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AuditActorIdentityHasherTests'
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatEndpointsCoverageTests'
git add src/Aevatar.Capabilities/AevatarPrincipalSubjectResolver.cs \
  test/Aevatar.Capabilities.Tests/AevatarPrincipalSubjectResolverTests.cs \
  agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs \
  src/Aevatar.Audit.Abstractions/Identity/IAuditActorIdentityHasher.cs \
  src/Aevatar.Audit.Core/Identity/AuditActorIdentityHasher.cs \
  test/Aevatar.Audit.Core.Tests/AuditActorIdentityHasherTests.cs \
  test/Aevatar.AI.Tests/NyxIdChatEndpointsCoverageTests.cs
git commit -m "Unify chat audit subject identity"
```

### Task 4: Attribute NyxID and Workflow Tool Executions

**Files:**

- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatInteraction.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/AgentRunReplyGenerationExecutor.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/IAgentRunReplyGenerationExecutorPort.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatTurnOperationExecutor.cs`
- Test: `test/Aevatar.GAgents.ChannelRuntime.Tests/AgentRunReplyGenerationExecutorTests.cs`
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/WorkflowCallerCredentialToolContextMapper.cs`
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/WorkflowRoleGAgent.cs`
- Modify: `src/workflow/Aevatar.Workflow.Integration.AI/AgentWorkflowToolSourceAdapter.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/WorkflowCallerCredentialToolContextTests.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/Modules/WorkflowRoleGAgentMappingTests.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/Modules/AgentWorkflowToolSourceAdapterTests.cs`
- Modify: `src/Aevatar.AI.Core/Auditing/ToolAuditRecordFactory.cs`
- Test: `test/Aevatar.AI.Core.Tests/Middleware/ToolExecutionAuditMiddlewareTests.cs`

**Interfaces:**

- NyxID ingress sets chat surface, conversation, turn, and task in `AgentChatInvocationContext`.
- When the exact tool operation key exists, `AgentRunAuthorizedToolStep` enriches the captured context with only typed task/step identities beside the transient capability; it never persists arguments/results.
- Workflow mapping sets `Caller.OwnerSubject` only from trusted `WorkflowCallerCredential.NyxIdAuthority.ExternalUserId` before scope fallback.
- Workflow Chat maps `run_id -> conversation_id`, `session_id -> turn_id`, and exact workflow `step_id -> step_id`; it does not invent workflow draft/member/service identities.
- `ToolAuditRecordFactory` copies `executionContext.Chat` into `AuditExecutionProvenance.Chat`; non-chat tools remain ordinary Audit Trail records.
- `AgentToolReceiptStatus.AuthorizationRequired` becomes terminal failed with stable code `authorization_required`, never success or a completed action.

- [ ] **Step 1: Add failing NyxID tool-provenance tests**

Exercise one exact tool call in `AgentRunReplyGenerationExecutorTests.cs` and inspect the `ToolCallContext.ExecutionContext` observed by middleware. Assert:

```csharp
seen.Chat.Should().Be(new AgentChatInvocationContext(
    AgentChatInvocationSurface.NyxIdAssistant,
    "conversation-alpha",
    "turn-alpha",
    "task-alpha",
    "step-alpha",
    null));
seen.ExternalMetadata.Keys.Should().NotContain([
    "conversation_id", "turn_id", "task_id", "step_id"]);
```

Add `AgentRunAuthorizedToolStep.WithChatOperation(NyxIdChatOperationKey key)` as the produced interface in the test. It returns an immutable transient capability copy whose captured `AgentToolExecutionContext.Chat` contains the exact task/step while the authorized tool list/calls and execution delegate stay unchanged; it must not expose or persist captured tool arguments. Keep the existing constructor overload used by unrelated fakes.

- [ ] **Step 2: Add failing Workflow identity/provenance tests**

Extend `WorkflowCallerCredentialToolContextTests.cs` so a credential with `ExternalUserId = "user-audit-alpha"` yields:

```csharp
context.Caller.OwnerSubject.Should().Be("user-audit-alpha");
```

Assert `WorkflowRunScopeToolContextMapper.Apply("scope-alpha", context)` preserves that value. Add a no-authority case proving scope fallback does **not** populate `OwnerSubject`. In `WorkflowRoleGAgentMappingTests.cs`, assert intent `RunId`, `SessionId`, and `StepId` produce `WorkflowChat` provenance; add the corresponding assertion for direct `AgentWorkflowToolSourceAdapter` execution.

- [ ] **Step 3: Add failing audit-factory tests**

Create NyxID and Workflow execution contexts and assert exact typed provenance and HMAC actor identity. Add this authorization test:

```csharp
record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Terminal);
record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Failed);
record.Failure.Code.Should().Be("authorization_required");
record.Redaction.OmittedFields.Should().Contain(["model.prompt", "tool.arguments", "tool.result"]);
AuditText(record).Should().NotContain("prompt-secret")
    .And.NotContain("argument-secret")
    .And.NotContain("result-secret")
    .And.NotContain("user-audit-alpha");
```

- [ ] **Step 4: Run focused tests and verify RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AgentRunReplyGenerationExecutorTests'
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~WorkflowCallerCredentialToolContextTests|FullyQualifiedName~WorkflowRoleGAgentMappingTests|FullyQualifiedName~AgentWorkflowToolSourceAdapterTests'
dotnet test test/Aevatar.AI.Core.Tests/Aevatar.AI.Core.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~ToolExecutionAuditMiddlewareTests'
```

Expected: provenance/owner assertions fail and authorization-required is not classified as a terminal failure.

- [ ] **Step 5: Implement minimal producer mappings**

In `NyxIdChatInteraction.BuildToolContext`, set:

```csharp
Chat = new AgentChatInvocationContext(
    AgentChatInvocationSurface.NyxIdAssistant,
    command.ActorId.Trim(),
    command.TurnId.Trim(),
    CreateTaskId(command.ActorId, command.TurnId),
    null,
    null),
```

Make `AgentRunAuthorizedToolStep` own the captured authorized `AgentToolExecutionContext` beside its existing transient delegate. The production constructor receives `llmResult.AuthorizedToolContext`; the existing fake-friendly constructor delegates with `AgentToolExecutionContext.Empty`. `WithChatOperation` returns a copy with only `Chat.TaskId` and `Chat.StepId` replaced from the exact key. `ExecuteAsync` supplies that captured context to `ChatRuntimeStepExecutor.ExecuteAuthorizedToolStepAsync`.

At `NyxIdChatTurnOperationExecutor.ExecuteToolAsync`, call `session.AuthorizedToolStep.WithChatOperation(command.Key)` after exact `SameTask`/call validation and before clearing the session capability; pass that copy to `BuildToolStepContinuationAsync`. For Workflow, set owner from `ExternalUserId`, construct `WorkflowChat` provenance from exact intent/request fields at both entry points, then delete `OwnerSubject = Fill(..., scopeId)` from `WorkflowRunScopeToolContextMapper`.

In `ToolAuditRecordFactory`, add one private `ToAuditChatProvenance` mapper and attach only when surface is specified. Map `AuthorizationRequired` to terminal/failed with code `authorization_required` and category `Authorization`. `NotStarted`/`NotApplied` remains evidence on the original NyxID tool result; do not add an external-effect field to `AuditRecord`, and do not infer an action request.

- [ ] **Step 6: Run tests and commit GREEN**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AgentRunReplyGenerationExecutorTests'
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~WorkflowCallerCredentialToolContextTests|FullyQualifiedName~WorkflowRoleGAgentMappingTests|FullyQualifiedName~AgentWorkflowToolSourceAdapterTests'
dotnet test test/Aevatar.AI.Core.Tests/Aevatar.AI.Core.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~ToolExecutionAuditMiddlewareTests'
git add agents/Aevatar.GAgents.NyxidChat/NyxIdChatInteraction.cs \
  agents/Aevatar.GAgents.NyxidChat/AgentRunReplyGenerationExecutor.cs \
  agents/Aevatar.GAgents.NyxidChat/IAgentRunReplyGenerationExecutorPort.cs \
  agents/Aevatar.GAgents.NyxidChat/NyxIdChatTurnOperationExecutor.cs \
  test/Aevatar.GAgents.ChannelRuntime.Tests/AgentRunReplyGenerationExecutorTests.cs \
  src/workflow/Aevatar.Workflow.Integration.AI/WorkflowCallerCredentialToolContextMapper.cs \
  src/workflow/Aevatar.Workflow.Integration.AI/WorkflowRoleGAgent.cs \
  src/workflow/Aevatar.Workflow.Integration.AI/AgentWorkflowToolSourceAdapter.cs \
  test/Aevatar.Workflow.Core.Tests/WorkflowCallerCredentialToolContextTests.cs \
  test/Aevatar.Workflow.Core.Tests/Modules/WorkflowRoleGAgentMappingTests.cs \
  test/Aevatar.Workflow.Core.Tests/Modules/AgentWorkflowToolSourceAdapterTests.cs \
  src/Aevatar.AI.Core/Auditing/ToolAuditRecordFactory.cs \
  test/Aevatar.AI.Core.Tests/Middleware/ToolExecutionAuditMiddlewareTests.cs
git commit -m "Attribute chat tool audit records"
```

### Task 5: Commit NyxID Conversation Ownership Once

**Files:**

- Modify: `agents/Aevatar.GAgents.NyxidChat/protos/agent_run.proto`
- Modify: `agents/Aevatar.GAgents.NyxidChat/protos/nyxid_chat_task.proto`
- Modify: `agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs`
- Modify: `src/Aevatar.Studio.Projection/Projectors/NyxIdChatConversationCurrentStateProjector.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatConversationGAgentTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs`
- Test: `test/Aevatar.Studio.Tests/NyxIdChatConversationCurrentStateProjectorTests.cs`

**Interfaces:**

- Adds `owner_subject` to `NyxIdChatConversationGAgentState` and `NyxIdChatConversationCreationStartedEvent` only.
- Does not add owner to `NyxIdChatConversationCreateCommand`; creation reads `command.FirstTurn.ToolContext.Caller.OwnerSubject`.
- A new `/api/chat` first-turn creation requires owner; the separate empty lifecycle-create path may remain ownerless. An existing owned actor accepts only the same owner; an existing ownerless actor remains ownerless and rejects an owner-bearing ordinary turn.
- Public current-state projection explicitly omits the actor-only owner field.

- [ ] **Step 1: Add failing actor ownership tests**

Add tests proving:

1. first creation commits `owner-alpha` in `NyxIdChatConversationCreationStartedEvent` and recovered state;
2. exact retry with `owner-alpha` succeeds;
3. later turn with `owner-beta` commits a typed rejection and dispatches no tool/LLM work;
4. pre-existing ownerless state rejects an owner-bearing turn and remains ownerless;
5. serialized public current-state and AGUI frames do not contain `owner-alpha`.

Use existing event-store/controller helpers and inspect committed protobuf events, not logs.
Keep direct-turn legacy fixtures ownerless. Update only the two manual lifecycle-create fixtures with `FirstTurn` in `NyxIdChatGAgentTests.cs` to set `ToolContext.Caller.OwnerSubject = "owner-alpha"`; lifecycle-create fixtures without `FirstTurn` must remain ownerless.

- [ ] **Step 2: Run tests and verify RED**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatConversationGAgentTests|FullyQualifiedName~NyxIdChatGAgentTests'
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatConversationCurrentStateProjectorTests'
```

Expected: state/event have no owner and conflict/ownerless behavior is not enforced.

- [ ] **Step 3: Add actor-owned owner fields and validation**

Add `string owner_subject = 23;` to `NyxIdChatConversationGAgentState` and `string owner_subject = 6;` to `NyxIdChatConversationCreationStartedEvent`. When `command.FirstTurn` is present, resolve and normalize its subject before the first commit and reject a missing owner; creation without a first turn leaves owner empty. In `ApplyConversationCreationStarted`, set it once. Before the existing-conversation fast path and ordinary turn admission, compare owner values exactly; never fill an empty persisted owner from a later turn.

Use stable safe code `NYXID_CHAT_OWNER_MISMATCH` in the typed rejection; error/log text must not contain either owner.

- [ ] **Step 4: Keep projection/presentation contracts explicitly clean**

The current-state projector must build its response field-by-field and omit owner. Add a descriptor/source test that fails if `owner_subject` is later added to `studio_projection_readmodels.proto` or AGUI wire payloads.

- [ ] **Step 5: Run tests and commit GREEN**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatConversationGAgentTests|FullyQualifiedName~NyxIdChatGAgentTests'
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatConversationCurrentStateProjectorTests'
git add agents/Aevatar.GAgents.NyxidChat/protos/agent_run.proto \
  agents/Aevatar.GAgents.NyxidChat/protos/nyxid_chat_task.proto \
  agents/Aevatar.GAgents.NyxidChat/NyxIdChatConversationGAgent.cs \
  src/Aevatar.Studio.Projection/Projectors/NyxIdChatConversationCurrentStateProjector.cs \
  test/Aevatar.AI.Tests/NyxIdChatConversationGAgentTests.cs \
  test/Aevatar.AI.Tests/NyxIdChatGAgentTests.cs \
  test/Aevatar.Studio.Tests/NyxIdChatConversationCurrentStateProjectorTests.cs
git commit -m "Commit NyxID conversation owner identity"
```

### Task 6: Translate Authoritative Browser-Action Facts

**Files:**

- Create: `src/Aevatar.Studio.Projection/Audit/NyxIdChatActionAuditTranslators.cs`
- Modify: `src/Aevatar.Studio.Projection/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `test/Aevatar.Studio.Tests/NyxIdChatActionAuditTranslatorTests.cs`
- Modify: `test/Aevatar.Studio.Tests/StudioAuditTranslatorTests.cs`

**Interfaces:**

- Produces three `IAuditCommittedEventTranslator` implementations registered on the existing registry/materializer:
  - `NyxIdChatActionRequestedAuditTranslator` for `NyxIdChatActionRequestedEvent`;
  - `NyxIdChatActionContinuationResolvedAuditTranslator` for terminal declined/failed/cancelled/expired `NyxIdChatContinuationAdmissionCommittedEvent` facts;
  - `NyxIdChatActionPostconditionResolvedAuditTranslator` for verified completed `NyxIdChatOperationReconciledEvent` facts.
- Each translator receives `IAuditActorIdentityHasher`, hashes `AuditCanonicalActorKeys.ForNyxIdUser(evt.State.OwnerSubject)`, and makes that identity the record's `AuditActorId`/`IdentityKeyId`; it skips ownerless events.
- Requested operation is `chat.action.requested`, accepted/nonterminal. Resolved operation is `chat.action.resolved`, terminal.
- Target is `chat_action/<stable lowercase action kind>` from an explicit exhaustive switch over `NyxIdAssistantActionKind`; resource IDs/params are omitted. Typed provenance carries conversation/turn/task/step/action request.
- Audit id is `chat-action:{committed_event_id}:{requested|resolved}:{action_request_id}`.

- [ ] **Step 1: Add failing requested-action tests**

Build `CommittedAuditTranslationContext` with `event-requested-alpha` and a `NyxIdChatActionRequestedEvent` whose state owns `user-audit-alpha`. Use typed `NyxIdAssistantActionKind.ServiceConnect` and assert target id `service_connect` plus one record:

```csharp
record.OperationName.Should().Be("chat.action.requested");
record.LifecyclePhase.Should().Be(AuditLifecyclePhase.Accepted);
record.TerminalOutcome.Should().Be(AuditTerminalOutcome.Unspecified);
record.Provenance.Chat.Should().BeEquivalentTo(new AuditChatProvenance
{
    Surface = AuditChatSurface.NyxidAssistant,
    ConversationId = "conversation-alpha",
    TurnId = "turn-alpha",
    TaskId = "task-alpha",
    StepId = "step-alpha",
    ActionRequestId = "action-alpha",
});
record.Redaction.OmittedFields.Should().Contain([
    "action.params", "owner_subject", "source_event.payload"]);
record.ToString().Should().NotContain("user-audit-alpha").And.NotContain("service-secret");
```

Assert an ownerless event returns no record, and translating the same event twice produces byte-equivalent records with the same audit id.

- [ ] **Step 2: Add failing resolution matrix tests**

Add a theory covering the exact committed inputs:

| Fact | Expected record |
|---|---|
| caller-reported `Completed`, postcondition not verified | none |
| verified `Completed` postcondition | `Succeeded` |
| `Declined` | `Cancelled`, failure code `action_declined` in `ErrorCode` only |
| `Failed` | `Failed`, structured failure code `action_failed` |
| `Cancelled` | `Cancelled`, no structured failure |
| `Expired` | `TimedOut`, structured failure code `action_expired` |
| unverified/unavailable postcondition | none |

For continuation rows require `Admission.Kind == Action`, `Admission.Status == Accepted`, and an exact `action_request_id` match between each terminal `Admission.ActionReports` item and a request in `State.RecentActions` carrying the typed action kind and the same report. One continuation event may return multiple records through the existing `IReadOnlyList<AuditRecord>` translator contract. A rejected admission, a `Completed` report, a missing state match, or an unspecified/unknown action kind emits none.

For verified completion, require `Result.ActionPostcondition.Verified`, disposition `Completed`, and an exact request in `State.RecentActions` whose committed `PostconditionResult` and action ID match. For every terminal row assert exactly one record, exact action id, and no params/resource/raw owner. `AuditRecordSanitizer` permits structured failure only for failed/timed-out terminal outcomes, so declined/cancelled records must not attach an `AuditFailure`.

- [ ] **Step 3: Run tests and verify RED**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatActionAuditTranslatorTests|FullyQualifiedName~StudioAuditTranslatorTests'
```

Expected: translators and DI registrations do not exist.

- [ ] **Step 4: Implement translators with one shared private builder**

Use one translator file and private static helpers; do not create a translator framework or duplicate action state. Map the already-committed `NyxIdAssistantActionKind` through an explicit switch to stable lowercase audit names such as `service_connect`; do not use `ToString`, prefix parsing, or the runtime registry. Skip unspecified/unknown values. Build a user-attributed `AuditRecord` directly because `CommittedAuditRecordFactory.CreateSystemRecord` hard-codes system attribution. Reuse its trace/correlation/committed-fact conventions, but never copy the raw owner into `CommittedFactRef.ActorId`, `Subject`, audit id, or annotations; use the HMAC identity instead.

Register all three `IAuditCommittedEventTranslator` implementations next to existing Studio translators, then retain the single existing `AddAuditCommittedFactMaterializer<StudioMaterializationContext>()`. Do not touch `NyxIdChatSessionProjectionContext` or add another materializer.

- [ ] **Step 5: Run tests and commit GREEN**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~NyxIdChatActionAuditTranslatorTests|FullyQualifiedName~StudioAuditTranslatorTests'
git add src/Aevatar.Studio.Projection/Audit/NyxIdChatActionAuditTranslators.cs \
  src/Aevatar.Studio.Projection/DependencyInjection/ServiceCollectionExtensions.cs \
  test/Aevatar.Studio.Tests/NyxIdChatActionAuditTranslatorTests.cs \
  test/Aevatar.Studio.Tests/StudioAuditTranslatorTests.cs
git commit -m "Audit NyxID browser action facts"
```

### Task 7: Filter Chat Activity in Both Audit Stores

**Files:**

- Modify: `src/Aevatar.Audit.Abstractions/Models/AuditTrailQuery.cs`
- Modify: `src/Aevatar.Audit.Core/Stores/InMemoryAuditTrailStore.cs`
- Modify: `src/Aevatar.Audit.Core/Projection/AuditTrailDocumentMetadataProvider.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetAgentProjectionDocumentStoresExtensions.cs`
- Test: `test/Aevatar.Audit.Core.Tests/InMemoryAuditTrailStoreTests.cs`
- Test: `test/Aevatar.Capabilities.Tests/ElasticsearchAuditTrailArtifactStoreTests.cs`

**Interfaces:**

- Adds to `AuditTrailQuery`: `IReadOnlyList<string>? AuditActorIds`, `bool RequireChatProvenance`, `AuditChatSurface? ChatSurface`, and `string? ChatConversationId`; existing `TerminalOutcome` is the outcome filter.
- Personal queries use `AuditActorIds`; legacy `AuditActorId` remains for generic/admin Audit Trail.
- In-memory and Elasticsearch filters are equivalent and run before ordering, cursor, and `take`.
- Elasticsearch explicitly maps query-critical `artifact.record.provenance.chat.*` fields.

- [ ] **Step 1: Add failing in-memory filter tests**

Insert interleaved records for two HMAC actors, a non-chat record, NyxID/Workflow surfaces, two conversations, and multiple outcomes. Query with `AuditActorIds = [alpha-current, alpha-retained]`, `RequireChatProvenance = true`, surface/conversation/outcome, and `Take = 1`. Assert the first page contains the matching record even when newer nonmatching rows exist, and the continuation page remains isolated.

- [ ] **Step 2: Add failing Elasticsearch request-body/mapping tests**

Capture the `_search` JSON and assert:

```csharp
filters.Should().Contain(node => node.GetProperty("terms")
    .GetProperty("artifact.audit_actor_id.keyword").GetArrayLength() == 2);
filters.Should().Contain(node => node.GetProperty("exists")
    .GetProperty("field").GetString() ==
        "artifact.record.provenance.chat.surface");
```

Assert exact term paths for surface, conversation, and terminal outcome; assert `size == take + 1`. Extend index-creation assertions for explicit keyword mappings of `surface`, `conversation_id`, `turn_id`, `task_id`, `step_id`, and `action_request_id`.

- [ ] **Step 3: Run store tests and verify RED**

```bash
dotnet test test/Aevatar.Audit.Core.Tests/Aevatar.Audit.Core.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~InMemoryAuditTrailStoreTests'
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~ElasticsearchAuditTrailArtifactStoreTests'
```

Expected: query properties/mappings do not exist and personal multi-key filtering cannot be expressed.

- [ ] **Step 4: Implement equivalent storage filters**

In memory, add `MatchesChat` and ordinal membership for `AuditActorIds`. In Elasticsearch, add one `terms` node for normalized distinct IDs, one `exists` node, and exact `term` nodes for typed chat fields. Never issue one query per key and never filter a returned page in the endpoint.

Under `record -> provenance -> chat`, add explicit object mappings. Keep dynamic mapping enabled for non-query open fields. The mapping fingerprint automatically creates a new physical index, copies forward, and swaps the alias.

- [ ] **Step 5: Run tests and commit GREEN**

```bash
dotnet test test/Aevatar.Audit.Core.Tests/Aevatar.Audit.Core.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~InMemoryAuditTrailStoreTests'
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~ElasticsearchAuditTrailArtifactStoreTests'
git add src/Aevatar.Audit.Abstractions/Models/AuditTrailQuery.cs \
  src/Aevatar.Audit.Core/Stores/InMemoryAuditTrailStore.cs \
  src/Aevatar.Audit.Core/Projection/AuditTrailDocumentMetadataProvider.cs \
  src/Aevatar.Mainnet.Host.Api/Hosting/MainnetAgentProjectionDocumentStoresExtensions.cs \
  test/Aevatar.Audit.Core.Tests/InMemoryAuditTrailStoreTests.cs \
  test/Aevatar.Capabilities.Tests/ElasticsearchAuditTrailArtifactStoreTests.cs
git commit -m "Filter typed chat audit activity"
```

### Task 8: Add the Personal-by-Default Chat Activity Endpoint

**Files:**

- Modify: `src/Aevatar.Audit.Hosting/AuditTrailEndpoints.cs`
- Modify: `src/Aevatar.Audit.Hosting/AuditTrailCapabilityHostBuilderExtensions.cs`
- Modify: `test/Aevatar.Audit.Hosting.Tests/AuditTrailEndpointsTests.cs`

**Interfaces:**

- Adds authorized `GET /api/audit/chat-activity`.
- Parameters: `cursor`, `from`, `to`, `take=50`, `surface`, `conversationId`, `outcome`, plus admin-only `scope=__all__` and exact `auditActorId`; an explicit `identityKeyId` is rejected for every caller.
- Ordinary query is fixed to caller scope, `HashAll(AuditCanonicalActorKeys.ForNyxIdUser(subject))`, and `RequireChatProvenance=true`.
- Admin `__all__` clears scope/actor constraints only after elevation. Any other explicit `scope` is rejected; this endpoint does not support arbitrary custom scopes.
- Reuses `AuditTrailReadResponse` and coverage mapping; no transcript enrichment.

- [ ] **Step 1: Add failing personal-read tests**

Use a recording query port and principals with distinct scope/subject fixtures. Assert:

```csharp
query.ScopeId.Should().Be("scope-alpha");
query.AuditActorIds.Should().BeEquivalentTo(["actor-key-2", "actor-key-1"]);
query.RequireChatProvenance.Should().BeTrue();
query.Take.Should().Be(50);
```

Assert user-supplied `scope`, `auditActorId`, or `identityKeyId` cannot widen the query and fails before the port. Assert missing scope, missing subject, and conflicting subject fail before the port. Assert `take=500` becomes `200`.

- [ ] **Step 2: Add failing admin/availability tests**

Assert an admin's default call remains personal. Assert explicit `scope=__all__` calls `IPlatformAdminAuthorizer`, then produces `ScopeId=null`, `AuditActorIds=null`, and optional exact `AuditActorId`. Non-admin returns `403`; missing authorizer returns `503 AUDIT_ADMIN_AUTH_UNAVAILABLE`; a personal call with no hasher returns `503 AUDIT_ACTOR_HASHER_UNAVAILABLE`; missing/thrown query port returns `503 AUDIT_QUERY_UNAVAILABLE` without fallback.

- [ ] **Step 3: Add failing typed-filter/response tests**

Pass `surface=workflow_chat`, `conversationId=run-alpha`, and `outcome=failed`; assert enum/query mapping. Reject unknown surface/outcome with `400`. Serialize a page and assert typed `provenance.chat` plus existing `coverage.continuationCursor`; assert raw subject, prompts, arguments/results, and action params are absent.

- [ ] **Step 4: Run endpoint tests and verify RED**

```bash
dotnet test test/Aevatar.Audit.Hosting.Tests/Aevatar.Audit.Hosting.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~AuditTrailEndpointsTests'
```

Expected: route/handler does not exist and the narrow personal policy cannot be expressed.

- [ ] **Step 5: Implement the narrow endpoint**

Add a separate `QueryChatActivity` handler; do not overload `QueryAuditTrailCore` with personal-policy flags. Resolve scope through `AevatarScopeAccessGuard`, subject through `AevatarPrincipalSubjectResolver`, canonical key through `AuditCanonicalActorKeys.ForNyxIdUser`, and IDs through `HashAll`. Normalize closed string filters explicitly. Preserve current safe error envelopes/logging and cancellation behavior.

Add `/api/audit/chat-activity` to capability health route inventory; reuse the existing query-port readiness probe rather than adding another.

- [ ] **Step 6: Run tests and commit GREEN**

```bash
dotnet test test/Aevatar.Audit.Hosting.Tests/Aevatar.Audit.Hosting.Tests.csproj --nologo
git add src/Aevatar.Audit.Hosting/AuditTrailEndpoints.cs \
  src/Aevatar.Audit.Hosting/AuditTrailCapabilityHostBuilderExtensions.cs \
  test/Aevatar.Audit.Hosting.Tests/AuditTrailEndpointsTests.cs
git commit -m "Expose isolated chat activity reads"
```

### Task 9: Add Chat Activity to the Existing Admin Console

**Files:**

- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`
- Modify: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`

**Interfaces:**

- Adds `chat-activity` beside Audit Trail in the existing Overview group with `auth:'login'`.
- Uses `/api/audit/chat-activity`, typed `operationName`, `terminalOutcome/lifecyclePhase`, `provenance.chat`, and `coverage.continuationCursor`.
- Ordinary users see only My activity. Platform admins additionally see All users and exact HMAC actor filter; `scope=__all__` is sent only after explicit selection.
- Columns: time, kind, name, status, conversation, turn. Inspector may show task/step/call/action request/safe target/side effect/failure/audit actor/scope/correlation.
- Does not call transcript/history/conversation endpoints.

- [ ] **Step 1: Add failing navigation/query-policy tests**

Extend existing Node VM asset tests to assert:

```javascript
assert.match(html, /items:\['fleet','status','audit','chat-activity'\]/);
assert.match(html, /\/api\/audit\/chat-activity/);
```

Execute `chatActivityBuildQuery` with ordinary-user state and assert no `scope`, `auditActorId`, or `identityKeyId`. With admin default state, assert the same. Only after setting `scope:'all'` assert `scope=__all__`; exact actor is included only in this admin mode.

- [ ] **Step 2: Add failing typed-render/privacy tests**

Provide one tool and one action record in the current Audit response shape. Assert the table uses `conversation-alpha`/`turn-alpha`, maps kind to Tool/Action, displays terminal badges, supports copyable full IDs while shortening visible text, and includes task/step/action request in the inspector. Instrument `adminJson` and assert every requested URL starts with `/api/audit/chat-activity`; no request targets chat conversation, history, or transcript routes.

Add assertions for keyboard-activatable rows, visible focus, loading, empty, error, and load-more states.

- [ ] **Step 3: Run admin tests and verify RED**

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~BackendConsoleStaticAssetEndpointTests.AdminShell_ChatActivity'
```

Expected: navigation/view/functions do not exist.

- [ ] **Step 4: Implement by reusing Audit Trail helpers/tokens**

Add only Chat Activity state/query/render functions that materially differ. Reuse existing time formatting, result badges, cursor application, loading/error/empty components, inspector, CSS variables, and table styles. Keep the typed Admin Audit mapper from `5f2189a18`; do not copy the obsolete `action/resourceType/resourceId/nextCursor` contract.

Default `CHAT_ACTIVITY_STATE.scope = 'mine'` for everyone. Gate All users on `ACCOUNT && ACCOUNT.admin`, but rely on the endpoint for authorization. Use buttons or `tabindex=0` plus Enter/Space handling for rows and preserve visible focus.

- [ ] **Step 5: Run tests and commit GREEN**

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo \
  --filter 'FullyQualifiedName~BackendConsoleStaticAssetEndpointTests.AdminShell_ChatActivity|FullyQualifiedName~BackendConsoleStaticAssetEndpointTests.AdminShell_AuditTrail'
git add src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html \
  test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
git commit -m "Add admin chat activity view"
```

### Task 10: Add Scoped Retention and Rollout Evidence

**Files:**

- Create: `tools/audit/retain_chat_activity.sh`
- Create: `tools/audit/tests/test_retain_chat_activity.sh`
- Create: `docs/operations/chat-activity-audit-retention.md`
- Modify: `docs/operations/2026-07-20-audit-trail-index-cutover.md`
- Modify: `docs/canon/audit-trail.md`
- Modify: `docs/canon/chat-api.md`
- Modify: `docs/canon/nyxid-chat-api.md`
- Modify: `docs/superpowers/specs/2026-07-31-chat-activity-audit-design.md`

**Interfaces:**

- `retain_chat_activity.sh --dry-run` calls `_count`; `--execute` calls `_delete_by_query?conflicts=proceed&wait_for_completion=true&refresh=false`.
- Required environment: `AEVATAR_ELASTICSEARCH_URL`; optional `AEVATAR_AUDIT_INDEX_ALIAS` defaults to `aevatar-audit-trail-current`; credentials come from `AEVATAR_ELASTICSEARCH_API_KEY` or curl netrc, never output.
- Query requires both `artifact.recorded_at < now-30d/d` and existence of `artifact.record.provenance.chat.surface`.
- Script prints mode, alias, cutoff expression, matched/deleted count, duration, and success/failure only; it never prints matched or deleted documents.
- Old physical cleanup remains separately approved after alias validation, backup/count evidence, and rollback expiry.

- [ ] **Step 1: Write the failing shell contract test**

Use a temporary fake `curl` that records method/path/body and returns controlled JSON. Assert dry-run uses `POST /<alias>/_count`, execute uses `POST /<alias>/_delete_by_query...`, and both bodies equal this predicate:

```json
{
  "query": {
    "bool": {
      "filter": [
        { "range": { "artifact.recorded_at": { "lt": "now-30d/d" } } },
        { "exists": { "field": "artifact.record.provenance.chat.surface" } }
      ]
    }
  }
}
```

Assert from the captured request body that the predicate cannot match an old fixture document without typed chat provenance. The fake Elasticsearch does not evaluate the query. Also assert execution requires explicit `--execute`, and output contains no fake document or credential.

- [ ] **Step 2: Run shell test and verify RED**

```bash
bash tools/audit/tests/test_retain_chat_activity.sh
```

Expected: script does not exist.

- [ ] **Step 3: Implement the smallest safe operation script**

Use Bash, `curl --fail-with-body --silent --show-error`, and `jq -e`; add no dependency/service. Reject unknown flags and missing URL. Put JSON in a quoted heredoc (not generated from user input), validate the alias against `^[a-zA-Z0-9._-]+$`, and keep `--dry-run` as default.

- [ ] **Step 4: Write runbook and canonical semantics**

Document the 30-day default and retained-HMAC-key requirement; dry-run/count review/least-privilege execution/post-count workflow; staging daily counts, primary bytes, replica factor, 30-day and double-index headroom; alias/fingerprint validation; separate old-physical cleanup; rollout/rollback; and exact included/excluded data. Change design front matter from `status: review-requested` to `status: accepted`. Do not add credentials, cluster addresses, or startup deletion.

- [ ] **Step 5: Run operation/docs tests and commit GREEN**

```bash
bash tools/audit/tests/test_retain_chat_activity.sh
bash tools/docs/lint.sh
git diff --check
git add tools/audit/retain_chat_activity.sh tools/audit/tests/test_retain_chat_activity.sh \
  docs/operations/chat-activity-audit-retention.md \
  docs/operations/2026-07-20-audit-trail-index-cutover.md \
  docs/canon/audit-trail.md docs/canon/chat-api.md docs/canon/nyxid-chat-api.md \
  docs/superpowers/specs/2026-07-31-chat-activity-audit-design.md
git commit -m "Document chat activity retention rollout"
```

### Task 11: Verify the Complete Slice and Prepare Rollout

**Files:**

- No new files; fix only defects exposed by these checks in their owning task's files.

**Interfaces:**

- Proves contract, capture, authorization, query, UI, and retention behavior as one release slice.
- Produces evidence required before index reconcile, retention execution, and deployment.

- [ ] **Step 1: Run focused projects**

```bash
dotnet test test/Aevatar.Audit.Abstractions.Tests/Aevatar.Audit.Abstractions.Tests.csproj --nologo
dotnet test test/Aevatar.Audit.Core.Tests/Aevatar.Audit.Core.Tests.csproj --nologo
dotnet test test/Aevatar.Audit.Hosting.Tests/Aevatar.Audit.Hosting.Tests.csproj --nologo
dotnet test test/Aevatar.AI.Core.Tests/Aevatar.AI.Core.Tests.csproj --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
bash tools/audit/tests/test_retain_chat_activity.sh
```

Expected: all pass, with no `Task.Delay` polling added.

- [ ] **Step 2: Run mandatory guards**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/ci/audit_trail_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: all pass. Chat Activity query performs no projection activation, priming, or replay; action capture uses the existing committed-fact materializer.

- [ ] **Step 3: Build affected production slices and solution**

```bash
dotnet build agents/Aevatar.GAgents.NyxidChat/Aevatar.GAgents.NyxidChat.csproj --nologo
dotnet build src/Aevatar.Audit.Hosting/Aevatar.Audit.Hosting.csproj --nologo
dotnet build src/Aevatar.Studio.Projection/Aevatar.Studio.Projection.csproj --nologo
dotnet build src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj --nologo
dotnet build aevatar.slnx --nologo
```

Expected: all pass. If full-solution test time is available, run `dotnet test aevatar.slnx --nologo`; otherwise record the focused project matrix above as affected-slice evidence.

- [ ] **Step 4: Perform secret/architecture readback**

```bash
rg -n "(owner_subject|tool.arguments|tool.result|action.params|prompt|access_token|authorization|cookie)" \
  src/Aevatar.Audit.* src/Aevatar.Studio.Projection/Audit \
  src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html
rg -n "(ChatLog|ChatActivityActor|NyxIdChatSessionProjectionContext|_delete_by_query)" \
  src agents tools/audit
git diff --check
git status --short
```

Expected: matches are only explicit omission/validation/runbook text or the one retention operation; there is no raw-value assignment, new store/actor/session materializer, or unrelated dirty file.

- [ ] **Step 5: Exercise staging in rollout order**

1. Deploy contract/query mapping to staging and wait for `audit-trail-current` reconcile.
2. Verify alias points to the new fingerprinted physical index and old/new document counts match.
3. Generate one NyxID tool, one Workflow tool, one requested action, one declined action, and one verified action; query as user A, user B, and platform admin.
4. Inspect serialized records for absence of prompts, arguments/results, action params/resources, raw subjects, and credentials.
5. Run `tools/audit/retain_chat_activity.sh --dry-run`; record count and capacity evidence. Do not execute deletion until operations approval.
6. Deploy capture, read API, and navigation together; monitor append failures, query latency, index bytes, and retention outcome.

Expected: personal isolation holds, admin all-user access requires explicit selection, verified completion is the only successful action terminal, and unrelated Audit Trail records survive retention.

- [ ] **Step 6: Final status check**

```bash
git status --short
git log --oneline --decorate -12
```

Expected: every implementation task has a focused commit, worktree is clean, and no unrelated user file was staged. Rollback hides navigation and stops new provenance capture; existing artifacts remain generic Audit Trail records and expire only under scoped retention.
