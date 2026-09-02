# Scheduled Agent Key Final Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close every blocking whole-branch review finding before the scheduled Agent Key branch is rebased, pushed, and verified in production.

**Architecture:** Persist the full expected Team-automation activation decision as typed actor-owned Protobuf state at Begin, and accept Complete only when its normalized configuration matches that decision. Reuse the approved exact-`userServiceId` Console/relay implementation, then harden its asynchronous observation semantics. UserConfig scope selection and create-audit replay both fail closed through typed contracts rather than string fallbacks or request-local guesses.

**Tech Stack:** .NET 10, C#, Google Protobuf, xUnit, FluentAssertions, React, TypeScript, TanStack Query, Jest, Bash, jq.

## Global Constraints

- Preserve `Domain / Application / Infrastructure / Host` dependency direction; API/Host only adapts and composes.
- Actor-owned facts, commands, events, and persisted state use typed Protobuf; do not add JSON or generic bags.
- Fire-time execution must not query UserConfig, replay events, or fill route/model from host defaults.
- Keep Agent Key material in `ISecretVault`; never log or project bearer tokens, raw keys, refresh tokens, Vault references, or ciphertext.
- Preserve distinct `Unspecified`, `Gateway`, and `NyxIdUserService` states.
- `userServiceId`, route, owner scope, binding ID, workflow ID, member ID, and published service ID remain distinct identities.
- An Agent Key workflow must have complete caller authority including exact `BindingId` before invocation.
- Do not weaken the canonical `6201`/`6202` audit allowlists or accept conflicting duplicate binding evidence.
- Do not add query-time replay, projection priming, in-process identity registries, or polling-based backend tests.
- Do not modify `/Users/eanzhao/Code/aevatar/.worktrees/owner-llm-exact-identity`; reuse only its committed Git objects.

---

### Task 1: Bind Complete To The Actor-Owned Activation Decision

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledDispatchModels.cs`
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto`
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs`
- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/Schedules/ScheduledDispatchActorPort.cs`
- Modify: `src/platform/Aevatar.GAgentService.Application/Schedules/ScheduledDispatchApplicationService.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchApplicationServiceTests.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs`

**Interfaces:**
- Produces: `TeamAutomationActivationDecision`, carried by `TeamAutomationCredentialOperation` and persisted by the authoritative schedule actor.
- Consumes: the exact schedule, target, caller authority, authorization fact, payload, owner, cron, timezone, enabled flag, and schedule kind known before credential materialization.
- Keeps: `MutationDigest` as an idempotency/correlation value, not as the sole semantic authority.

- [ ] **Step 1: Write failing actor tests for decision substitution**

Create one valid Begin decision and Complete configuration, then clone and mutate each stable semantic independently. At minimum reject changes to:

```text
scheduleId, displayName, Team owner, service tenant/app/namespace/serviceId,
endpointId, packed ChatRequestEvent prompt/scope/LlmControl, caller platform,
tenant/externalUserId/scope/BindingId, permission digest, policy version,
authorization owner, service grants, owner LLM selection, cron, timezone,
enabled, schedule kind, and non-empty headers
```

Assert the actor remains pending, retains its candidate credential, and emits no activation event for every mismatch. Add an exact Complete replay test proving a changed configuration is rejected even after the first activation.

- [ ] **Step 2: Run focused tests and prove RED**

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter "FullyQualifiedName~ScheduledDispatchGAgentTests"
```

Expected: at least the caller-binding or authorization-fact mutation activates because Begin stores only the opaque digest.

- [ ] **Step 3: Add the typed decision contract without renumbering fields**

Append new fields only. Use the next free field numbers (`64` on `ScheduledDispatchState`, `11` on Begin command/event) and a dedicated message shaped like:

```proto
message TeamAutomationActivationDecisionState {
  string schedule_id = 1;
  string display_name = 2;
  TeamMemberAutomationOwnerState owner = 3;
  aevatar.gagentservice.ServiceIdentity service_identity = 4;
  string endpoint_id = 5;
  google.protobuf.Any payload = 6;
  aevatar.credentials.ScheduledCallerNyxIdAuthority caller_authority = 7;
  ScheduledInvocationAuthorizationFactState authorization_fact = 8;
  string cron_expression = 9;
  string timezone = 10;
  bool enabled = 11;
  ScheduledDispatchScheduleKindState schedule_kind = 12;
  map<string, string> headers = 13;
  ScheduledDispatchScheduleModeState schedule_mode = 14;
  google.protobuf.Timestamp one_shot_fire_at = 15;
  ScheduledDispatchCredentialRequirementTargetKindState credential_requirement_target_kind = 16;
  string revision_id = 17;
  aevatar.gagentservice.ServiceInvocationCaller caller = 18;
}
```

Reuse the existing credential-contract caller-authority type exactly. Do not serialize the candidate credential into this decision; the actor already owns and validates it separately.

- [ ] **Step 4: Build and persist the decision at Begin**

Add an equivalent immutable Application-abstraction record. Studio computes the authorization fact and packed `ChatRequestEvent` once, clones them into the Begin decision, and later reuses them for Complete configuration. Derive the existing `MutationDigest` from this typed decision so there is one field list, while keeping the typed decision as authority. Application normalization clones every nested message/list, Infrastructure maps it one-to-one into the Begin command, and the actor normalizes it before committing `TeamAutomationCredentialOperationBeganEvent`.

Actor Begin validates command schedule/owner equality, decision fact permission/policy equality, full caller authority including `BindingId`, headers, mode/one-shot semantics, and all target fields. Exact Begin replay compares the typed decision in addition to operation, idempotency, effect locator, policy, permission digest, and mutation digest. Any drift returns the existing operation-conflict result.

- [ ] **Step 5: Compare Complete against the committed decision**

Normalize Complete configuration once, compare every stable semantic with the pending decision, and only then persist activation. Canonicalize unordered grants, node IDs, and headers before comparison; compare `Any` type URL plus bytes and every authorization-fact subfield. The Agent Key reference must still equal the actor-owned candidate. Reject extra headers and unexpected target/caller/fact fields.

Move the current Active fast path after configuration normalization. Exact active replay compares against installed actor state before returning success. Pending legacy state with no typed decision fails closed as `team_automation_activation_decision_missing`; mismatch fails as `team_automation_activation_decision_mismatch` while retaining pending/candidate state. Existing active legacy schedules remain readable/runnable, and the deployment drain guarantees no legacy provisioning/replacement operation crosses the release.

Do not admit transport-derived values into the business decision: `ConfiguredAt`, command/correlation IDs, derived payload type URL, and trigger-envelope transport timestamps/routes are reconstructed or ignored. For service invocation, still require the canonical service-invocation target actor ID and consistency between any prepared trigger request and the decision target so read-model/diagnostic fields cannot drift from runtime `State.Target`.

Clear the pending decision only when activation, terminal operation failure, or deletion makes it unreachable. Keep the installed active configuration as the replay authority.

- [ ] **Step 6: Run focused tests and commit**

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter "FullyQualifiedName~ScheduledDispatchGAgentTests"
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "FullyQualifiedName~ScheduledDispatchApplicationServiceTests"
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~StudioMemberWorkflowSchedulePortTests"
git add src/platform/Aevatar.GAgentService.Abstractions src/platform/Aevatar.GAgentService.Core src/platform/Aevatar.GAgentService.Infrastructure src/platform/Aevatar.GAgentService.Application src/Aevatar.Studio.Application test/Aevatar.Workflow.Core.Tests test/Aevatar.GAgentService.Tests test/Aevatar.Studio.Tests
git commit -m "Bind scheduled activation to committed decision"
```

---

### Task 2: Fail Closed On Missing UserConfig Scope And Preserve Unspecified

**Files:**
- Create: `src/Aevatar.Studio.Application/Studio/Abstractions/AppScopeResolverExtensions.cs`
- Delete: `src/Aevatar.Studio.Infrastructure/ActorBacked/AppScopeResolverExtensions.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/UserConfigService.cs`
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionUserConfigQueryPort.cs`
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionStudioWorkspaceQueryPort.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/LlmSelection/DefaultUserLlmOptionsService.cs`
- Test: `test/Aevatar.Studio.Tests/UserConfigServiceTests.cs`
- Test: `test/Aevatar.Studio.Tests/ProjectionUserConfigQueryPortTests.cs`
- Test: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/DefaultUserLlmOptionsServiceTests.cs`

**Interfaces:**
- Produces: one shared `IAppScopeResolver.ResolveScopeIdOrDefault()` policy in Application Abstractions.
- Preserves: `"default"` only for a genuine non-request context; authenticated requests without `scope_id` throw before read or write.

- [ ] **Step 1: Write failing scope and Unspecified tests**

Add authenticated-missing-scope resolver fixtures where `Resolve()` returns null and `HasAuthenticatedRequestWithoutScope()` returns true. Assert `GetAsync`, `GetRuntimeAsync`, generic `SaveAsync`, and `SaveLlmPreferenceAsync` all throw before invoking query/command/catalog ports. Assert ambient projection `GetAsync()` also throws before document read.

Add options tests for null config, null selection, and explicit `Unspecified`, each with both empty and non-empty compatibility routes. Every case must ignore `PreferredLlmRoute`, return `Current == null` and an empty/null current route, and never manufacture Gateway or a service selection. The typed selection is the only authority.

- [ ] **Step 2: Run focused tests and prove RED**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~UserConfigServiceTests|FullyQualifiedName~ProjectionUserConfigQueryPortTests"
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --filter "FullyQualifiedName~DefaultUserLlmOptionsServiceTests"
```

- [ ] **Step 3: Centralize scope policy and remove empty-route normalization**

Move the existing Infrastructure extension behavior into `Aevatar.Studio.Application/Studio/Abstractions` beside `IAppScopeResolver`, so Application, Projection, and Infrastructure consume one dependency-correct policy without a new project reference. Update UserConfig services and replace the duplicate private resolver in `ProjectionStudioWorkspaceQueryPort`. Remove the old Infrastructure extension. Explicit resource-key query overloads remain unchanged.

In `DefaultUserLlmOptionsService`, remove the compatibility-route fallback for null and typed `Unspecified`. Only explicit typed Gateway or exact typed `NyxIdUserService` may produce a current option or current route.

- [ ] **Step 4: Run tests and commit**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~UserConfigServiceTests|FullyQualifiedName~ProjectionUserConfigQueryPortTests"
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --filter "FullyQualifiedName~DefaultUserLlmOptionsServiceTests"
git add src/Aevatar.Studio.Application src/Aevatar.Studio.Infrastructure src/Aevatar.Studio.Projection agents/Aevatar.GAgents.NyxidChat test/Aevatar.Studio.Tests test/Aevatar.GAgents.ChannelRuntime.Tests
git commit -m "Fail closed on missing UserConfig scope"
```

---

### Task 3: Complete And Harden Exact-ID Console And Relay Selection

**Files:**
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/UserLlmSettingsViewBuilder.cs`
- Modify: `src/Aevatar.Studio.Hosting/Controllers/UserLlmWireContracts.cs`
- Test: `test/Aevatar.Studio.Tests/UserLlmSettingsViewBuilderTests.cs`
- Test: `test/Aevatar.Studio.Tests/UserConfigControllerSettingsTests.cs`
- Modify: `apps/aevatar-console-web/src/shared/studio/models.ts`
- Modify: `apps/aevatar-console-web/src/shared/studio/api.ts`
- Modify: `apps/aevatar-console-web/src/shared/studio/api.test.ts`
- Create/Modify: `apps/aevatar-console-web/src/pages/settings/userLlmSelection.ts`
- Create/Modify: `apps/aevatar-console-web/src/pages/settings/userLlmSelection.test.ts`
- Create: `apps/aevatar-console-web/src/pages/settings/userLlmSaveObservation.ts`
- Create: `apps/aevatar-console-web/src/pages/settings/userLlmSaveObservation.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/settings/index.tsx`
- Modify: `apps/aevatar-console-web/src/pages/settings/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/settings/nyxIdRelayLlm.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatConversationConfig.ts`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatConversationConfig.test.ts`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html`
- Modify only where required by existing commits: Console locale and chat-presentation files.

**Interfaces:**
- Consumes: backend `userServiceId`, `savedRouteKind`, `savedUserServiceId`, and `savedServiceSlug`.
- Produces: persisted-selection identity keyed by `gateway` or exact `user-service:{encoded-id}`; route remains display/runtime data only.
- Extends: each selectable route option with nullable `defaultModel`, sourced from that exact inventory-backed option.

- [ ] **Step 1: Capture the current RED and integrate committed Task 7 work**

Run the approved Task 7 focused tests on current HEAD and record the route-only failures. Then cherry-pick, in order, without reading or changing the other worktree:

```bash
git cherry-pick b932bb19e 1991f7d11 a58bc96c7
```

These commits are an implementation base, not review approval. Keep their exact-ID decoder, duplicate-route selection, Gateway, accepted/observed UI, and relay tests; resolve conflicts in favor of the current backend wire contract.

- [ ] **Step 2: Write failing asynchronous-state tests**

Add deterministic Jest/DOM tests for all known regressions:

1. An older relay save completes after the user edits or starts a newer save; it cannot mark the newer draft saved or replace its status.
2. A service save with blank model means platform default and becomes observed when the same exact service ID returns a canonical non-empty saved model.
3. A stale/degraded catalog response that omits or disables the accepted exact service cannot erase the accepted target or pair its model with another service.
4. Settings automatically continues bounded read-model observation after an immediate stale GET and reaches observed state without an unrelated user action.
5. Pending/accepted copy names the accepted exact target, not a subsequently edited draft.
6. Two services sharing one route remain separately selectable and save the chosen exact ID.
7. Observation stops after exactly the fixed delays `[0, 250, 500, 1000, 2000, 3000, 5000]` and reports `accepted_unobserved`, not write failure.

- [ ] **Step 3: Add request generations and a durable pending target**

Relay and React Settings maintain an immutable pending target containing exact kind, exact service ID when applicable, route snapshot, and model intent. Use two monotonic values:

```text
saveToken       increment on each save and lifecycle reset/new wizard
draftRevision   increment on every service/model edit
```

Only a newer save or reset invalidates old success/error/GET callbacks. A normal edit keeps observing the accepted target but prevents it from overwriting the newer visible draft. The pending record contains `saveToken`, `submittedRevision`, `submittedDraft`, `expectedCommittedDraft`, `selectionLabel`, and `phase`.

Expose nullable `defaultModel` on `UserLlmRouteOption` and its JSON wire response, populate it from the exact option in `UserLlmSettingsViewBuilder`, and decode it in Console/relay. Blank service model is represented as `platform default`; its expected committed model is the normalized exact option default. Blank Gateway model expects the explicit empty string. Relay sends `model: ""`, not `null`, so reset cannot preserve an old model.

Do not clear a pending target because a transient catalog omits it. Render a stable disabled retained option from the target or committed saved identity until observation resolves; save remains disabled until a live exact option returns. Never replace the last good relay catalog with null on transient GET failure.

Observe sequentially at exactly `[0, 250, 500, 1000, 2000, 3000, 5000]` milliseconds, launching at cumulative times `[0, 250, 750, 1750, 3750, 6750, 11750]`. Give the seventh GET a bounded 5-second settle window, so final exhaustion is at 16.75 seconds. Check `saveToken` before applying every response or error. Exhaustion becomes `accepted_unobserved` with manual retry, not write failure. Use Jest fake timers; do not require unrelated user activity and do not add backend polling tests.

- [ ] **Step 4: Verify the exact wire contract and product semantics**

Service save sends `{ userServiceId, model }`; Gateway save sends `{ routeValue: "/api/v1/llm/gateway/v1", model }`. Diagnostics without an inventory-backed ID remain non-selectable. Conversation-level route overrides remain route-based and do not become durable owner identity.

Ensure pending text refers to the accepted target. Keep English and Chinese locale keys aligned and remove stale route-as-identity terminology from the changed settings area.

- [ ] **Step 5: Run frontend verification and commit hardening**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~UserLlmSettingsViewBuilderTests|FullyQualifiedName~UserConfigControllerSettingsTests"
pnpm --dir apps/aevatar-console-web test --runInBand --runTestsByPath src/shared/studio/api.test.ts src/pages/settings/userLlmSelection.test.ts src/pages/settings/userLlmSaveObservation.test.ts src/pages/settings/index.test.tsx src/pages/settings/nyxIdRelayLlm.test.ts src/pages/chat/chatConversationConfig.test.ts
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web build
git add apps/aevatar-console-web agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html
git commit -m "Harden exact owner LLM selection state"
```

---

### Task 4: Make Create Acceptance Audit Replay-Safe

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Schedules/TeamAutomationOperationObservationContracts.cs`
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto`
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs`
- Modify: `src/platform/Aevatar.GAgentService.Projection/Orchestration/TeamAutomationOperationObservationSessionEventCodec.cs`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Provisioning/StudioMemberWorkflowScheduleContracts.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs`
- Modify: `src/Aevatar.Studio.Hosting/Endpoints/StudioMemberAutomationEndpoints.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberAutomationEndpointsTests.cs`
- Test: `tools/schedules/tests/test_query_member_automation_audit.sh`

**Interfaces:**
- Produces: a typed `NewOperationCommitted` boolean on committed observation outcome and the internal Studio result.
- Guarantees: exact HTTP replay remains successful but does not emit a second logical `6201` record.

- [ ] **Step 1: Write failing actor/Application/Host replay tests**

Run the same create request twice with identical schedule, operation, idempotency key, mutation digest, and activation decision. Assert the first Begin observation reports `(NewOperationCommitted: true, OwnsEffectAttempt: true)`, replay before lease expiry reports `(false, false)`, and replay after lease expiry reports `(false, true)`. Both public receipts remain accepted, and the audit logger contains exactly one `6201` entry with the same six allowlisted fields.

Keep the shell query strict: two identical `6201` lines and conflicting binding records must both fail with no stdout or leaked field value. The production fix prevents a second line at its source; the parser does not deduplicate it.

- [ ] **Step 2: Run focused tests and prove RED**

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter "FullyQualifiedName~ScheduledDispatchGAgentTests"
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~StudioMemberWorkflowSchedulePortTests|FullyQualifiedName~StudioMemberAutomationEndpointsTests"
bash tools/schedules/tests/test_query_member_automation_audit.sh
```

- [ ] **Step 3: Persist and propagate first-transition evidence**

Append `new_operation_committed = 23` to `TeamAutomationOperationObservedEvent`; append `NewOperationCommitted = false` to `TeamAutomationOperationCommittedOutcome` to preserve call sites, and map it through `TeamAutomationOperationObservationSessionEventCodec`. Set it true only in the branch that commits a new `TeamAutomationCredentialOperationBeganEvent`, never for exact replay, lease reclaim, rejection, candidate, Complete, Fail, Delete, or Revocation.

Propagate the business fact as `StudioMemberWorkflowScheduleResult.NewOperationCommitted`. Gate `6201` emission on both successful create and that property. Keep the public HTTP receipt unchanged. Do not use an in-process deduplication dictionary.

- [ ] **Step 4: Run tests and commit**

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter "FullyQualifiedName~ScheduledDispatchGAgentTests"
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "FullyQualifiedName~TeamAutomationOperationObservationInfrastructureTests|FullyQualifiedName~TeamAutomationObservationCorrelationTests|FullyQualifiedName~ScheduledDispatchApplicationServiceTests"
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~StudioMemberWorkflowSchedulePortTests|FullyQualifiedName~StudioMemberAutomationEndpointsTests"
bash tools/schedules/tests/test_query_member_automation_audit.sh
bash tools/ci/test_stability_guards.sh
git add src test tools/schedules/tests/test_query_member_automation_audit.sh
git commit -m "Make automation acceptance audit replay-safe"
```

---

### Task 5: Re-Review, Verify, Rebase, Push, And Canary

**Files:**
- Modify documentation only if implementation changes the canonical actor/audit contract.

- [ ] **Step 1: Generate one whole-fix review package**

Review from `23f141f76` to final fix HEAD. Resolve every Critical/Important and High/Medium finding in one follow-up wave. Re-review the full branch from merge base `7d1d31f39`.

- [ ] **Step 2: Run backend and frontend validation**

```bash
dotnet build aevatar.slnx --nologo
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web test --runInBand
pnpm --dir apps/aevatar-console-web build
```

- [ ] **Step 3: Run every required guard**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/ci/solution_split_guards.sh
bash tools/ci/test_solution_ownership_guard.sh
bash tools/ci/slow_test_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

- [ ] **Step 4: Fetch and rebase without force**

```bash
git fetch git@github.com:aevatarAI/aevatar.git refs/heads/feature/integrate:refs/remotes/origin/feature/integrate
git rebase origin/feature/integrate
```

If code changes through conflict resolution or upstream integration, rerun Steps 2 and 3 and regenerate the final review package.

- [ ] **Step 5: Repeat the old-binary drain immediately before push**

Use the reviewed runbook with the mode-`0600` bearer file. Require zero `provisioning_pending`, zero `replacement_pending`, and an approved disposition for every active Agent Key automation. Record only allowlisted counts/IDs; never print the bearer or raw audit logs.

- [ ] **Step 6: Push, observe deployment, and execute the production canary**

```bash
git push git@github.com:aevatarAI/aevatar.git HEAD:feature/integrate
```

Wait for a Ready backend image whose immutable source contains the pushed SHA and whose OpenAPI passes the runtime-integrity gate. Then execute `docs/operations/2026-07-23-scheduled-agent-key-production-canary.md` with local NyxID CLI `0.7.1`, finish both `6201`/`6202` evidence checks, revoke the exact key and Vault secret, and clean every temporary resource in documented order.
