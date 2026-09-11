# Unified Channel Agent Key Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved unified Channel Agent Key model for new Lark and Telegram registrations, including creation-time `NYXID_DEFAULT` and `EXPLICIT_SERVICE_ALLOWLIST` modes, exact NyxID `UserService.id` authorization, v1 scope planning, strongly typed persistence, and registration-authorized runtime admission. Do not add existing-registration replacement, mode switching, import, general rotation, compatibility removal, unified deletion authorization, or frontend Service selection.

**Architecture:** Extend the existing Protobuf registration fact with two explicit authorization modes, an optional business Service allowlist, a Vault-backed typed credential, and the verified provider grant snapshot. Host boundaries preserve `service_ids` presence and reject malformed input before side effects. A narrow application orchestration path validates exact NyxID Service identity and owner authority, resolves current authoritative runtime dependencies and any required bot-specific connection, invokes the existing NyxID v1 scope-plan contract, and passes only verified typed grants to the shared key provisioner. The registration actor remains the sole owner of committed mode, allowlist, credential, and grant facts; projection only materializes them. Registration-authorized runtime calls use the read model and authoritative configuration to enforce business allowlist/internal-call-site admission and actual key-grant admission without sender-to-registration fallback.

**Current release boundary:** Tasks 1–6 below are the preserved execution record of
the earlier default-only baseline. Their scope assertions are not the final
delivery contract. Tasks 7–13 supersede that baseline for this release: source
design §§1–19 require both creation modes and creation-time Service allowlists,
while §§20–25 remain deferred. In particular, the final implementation must not
be audited as though `service_ids`, the explicit mode, scope planning, or exact
connection preplanning were still deferred.

**Tech Stack:** .NET, C#, Protobuf, xUnit, FluentAssertions, NSubstitute, ASP.NET Core Minimal APIs, NyxID REST adapter, `ISecretVault`, event-sourced GAgents, CQRS Projection Pipeline.

---

### Task 1: Add The Strongly Typed Registration Contract

**Files:**
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/protos/channel_bot_registration.proto`
- Create: `agents/Aevatar.GAgents.Channel.Runtime/ChannelRegistrationAuthorizationContract.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelBotRegistrationProtoCompatibilityTests.cs`
- Create: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelRegistrationAuthorizationContractTests.cs`

- [x] **Step 1: Write failing field-number and presence tests**

Add assertions for Entry fields 19/20, Command fields 14/15, Document fields 20/21, the two-value authorization enum, credential fields 1/2/3, and grant fields 1/2/4/5. Add a round-trip test that explicitly sets both optional booleans to `false` and verifies `HasAllowAllServices` and `HasAllowAllNodes` remain true after serialization.

- [x] **Step 2: Run the focused compatibility tests and verify RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --filter FullyQualifiedName~ChannelBotRegistrationProtoCompatibilityTests
```

Expected: compilation or assertion failure because the new Protobuf symbols and fields do not exist.

- [x] **Step 3: Add only the approved Protobuf fields**

Append exactly the `ChannelRegistrationAuthorizationMode` enum, `ChannelAgentKeyCredential`, and `ChannelAgentKeyGrantSnapshot`, then add Entry 19/20, Command 14/15, and Document 20/21. Do not add the deferred fields 18/13/19/3, an explicit allowlist enum value, or a scope-plan digest.

- [x] **Step 4: Write failing contract-classification tests**

Cover valid `NYXID_DEFAULT`, true legacy (`UNSPECIFIED` plus absent new credential), missing mode, missing credential, unknown mode, incomplete Vault reference, missing optional booleans, noncanonical ID arrays, `allow_all_* = true` with a nonempty list, mismatched key aliases, and mismatched full `SecretReference` aliases.

- [x] **Step 5: Run the classifier tests and verify RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --filter FullyQualifiedName~ChannelRegistrationAuthorizationContractTests
```

Expected: compilation failure because `ChannelRegistrationAuthorizationContract` does not exist.

- [x] **Step 6: Implement the minimal classifier**

Create a runtime-owned static contract with `Classify(ChannelBotRegistrationEntry)`, `IsValidNewCommand(ChannelBotRegisterCommand)`, and `TryGetAuthoritativeCredential(ChannelBotRegistrationEntry, out ChannelAgentKeyCredential?)`. Validate exact aliases, complete typed Vault references, optional boolean presence, strictly increasing ordinal ID arrays, and contradictory `allow_all_*` combinations. Do not infer mode from grants or provider defaults.

- [x] **Step 7: Run both focused test classes and verify GREEN**

Run the filters from Steps 2 and 5. Expected: all selected tests pass.

### Task 2: Build The Shared NyxID Key And Vault Provisioning Core

**Files:**
- Create: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelAgentKeyProvisioningService.cs`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/DependencyInjection/NyxIdRelayChannelServiceCollectionExtensions.cs`
- Create: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelAgentKeyProvisioningServiceTests.cs`

- [x] **Step 1: Write failing strict-parser and Vault-gate tests**

Test a valid response, exact `read write proxy` scopes, required `general` class, required typed `allow_all_services` and `allow_all_nodes`, canonical Service/Node arrays, contradictory allow-all/list combinations, missing `full_key`, and Vault failure cleanup. Verify the create request omits `allowed_service_ids`, `allowed_node_ids`, `allow_all_services`, `allow_all_nodes`, and `scope_plan_digest`.

- [x] **Step 2: Run the focused provisioning-core tests and verify RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --filter FullyQualifiedName~ChannelAgentKeyProvisioningServiceTests
```

Expected: compilation failure because the shared service does not exist.

- [x] **Step 3: Implement the shared provisioning core**

Create `ChannelAgentKeyProvisioningService` with `ProvisionAsync(platform, accessToken, relayCallbackUrl, scopeId, registrationId, ct)` and `CleanupAsync(accessToken, credential, registrationId, cleanupToken)`. Keep raw `full_key` in a private redacted parser result only long enough to call `ISecretVault.PutAsync`. Invalid create responses fail as `channel_authorization_contract_invalid`; Vault failures fail as `secret_vault_unavailable`. If an ID was created, delete the key with a detached bounded token; after a successful Vault write, cleanup order is key delete then Vault revoke.

- [x] **Step 4: Register the shared component and verify GREEN**

Register it as a singleton in the NyxID relay composition root, then rerun the focused tests. Expected: all selected tests pass and no output contains raw key material.

### Task 3: Move Lark And Telegram Onto The Unified Provisioning Flow

**Files:**
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxLarkProvisioningService.cs`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxTelegramProvisioningService.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/NyxLarkProvisioningServiceTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/NyxTelegramProvisioningServiceTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelWorkflowResultDeliveryContractTests.cs`

- [x] **Step 1: Write failing platform integration tests**

Require both adapters to dispatch `AuthorizationMode = NyxIdDefault`, the unified credential, and legacy aliases that exactly equal the new key ID/reference. Require Telegram to persist its `full_key`, report workflow delivery enabled, and request `read write proxy`. Require Vault failure to stop before bot/route/actor writes. Require compensation order route, bot, key, Vault and detached bounded cleanup after caller cancellation.

- [x] **Step 2: Run the Lark and Telegram focused tests and verify RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --filter "FullyQualifiedName~NyxLarkProvisioningServiceTests|FullyQualifiedName~NyxTelegramProvisioningServiceTests"
```

Expected: failures because Telegram discards `full_key`, Lark permits a missing Vault credential, and neither command carries the new contract.

- [x] **Step 3: Replace per-platform key creation with the shared core**

Keep Lark order `Key/Vault -> bot -> route -> best-effort proxy connection -> actor command`. Keep Telegram relay-only with no new proxy-service connection. Build each command with `AuthorizationMode = NyxIdDefault`, a clone of `ChannelAgentKey`, and the two exact legacy aliases. Use an internal timeout token for all rollback calls and never roll back after a confirmed command acceptance.

- [x] **Step 4: Run platform and end-to-end delivery tests and verify GREEN**

Run the Step 2 filter and `ChannelWorkflowResultDeliveryContractTests`. Expected: all selected tests pass.

### Task 4: Enforce Actor Ownership And Historical-Only Compatibility

**Files:**
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/ChannelBotRegistrationGAgent.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/ChannelWorkflowResultDeliveryCapability.cs`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelWorkflowResultDeliveryRepairService.cs`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelNyxIdAgentKeyReadinessPort.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelBotRegistrationStoreTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelWorkflowResultDeliveryRepairServiceTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelNyxIdAgentKeyReadinessPortTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelConversationTurnRunnerTests.cs`

- [x] **Step 1: Write failing Actor and compatibility tests**

Test rejection of legacy-shaped new commands and malformed new contracts, persistence of valid mode/credential/grant facts, duplicate registration preservation, old-event replay, new-model immunity to repair events, valid new-first credential selection, legacy-only fallback, and invalid-new no-fallback behavior.

- [x] **Step 2: Run focused Actor/runtime tests and verify RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --filter "FullyQualifiedName~ChannelBotRegistrationGAgentTests|FullyQualifiedName~ChannelConversationTurnRunnerTests|FullyQualifiedName~ChannelWorkflowResultDeliveryRepairServiceTests|FullyQualifiedName~ChannelNyxIdAgentKeyReadinessPortTests"
```

Expected: selected new tests fail for missing validation and legacy fallback.

- [x] **Step 3: Implement Actor validation and immutable new-model behavior**

Validate commands before building entries, clone mode/credential into the committed event, reject duplicate active IDs that would overwrite a valid new registration, and gate every repair handler/transition with `HistoricalLegacy`. Seed historical tests by appending old `ChannelBotRegisteredEvent` payloads instead of sending production legacy register commands.

- [x] **Step 4: Implement new-first runtime credential selection**

For `NyxIdDefault`, use only `channel_agent_key`; for true legacy, use only old aliases; for invalid new records, return no credential and reject inbound execution with `channel_authorization_contract_invalid`. Change normal readiness to exact Vault reference resolution without Agent Key self-inspection of the human-only API-key management surface; retain proxy-scope inspection and mutation solely in the supported historical Lark repair Nyx port, using its authorized owner credential.

- [x] **Step 5: Run focused Actor/runtime tests and verify GREEN**

Rerun the Step 2 filter. Expected: all selected tests pass.

### Task 5: Materialize And Expose Only The Safe Owner View

**Files:**
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/ChannelBotRegistrationProjector.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/ChannelBotRegistrationQueryPort.cs`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelCallbackEndpoints.cs`
- Modify: `src/Aevatar.AI.ToolProviders.ChannelAdmin/ChannelRegistrationTool.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelBotRegistrationProjectorTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelCallbackEndpointsTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelRegistrationToolTests.cs`

- [x] **Step 1: Write failing projection/query tests**

Assert that mode, credential, grant arrays, and optional boolean presence survive projection and query mapping without inference.

- [x] **Step 2: Write failing HTTP/tool boundary tests**

For `null`, `[]`, a nonempty array, and a non-array value, send root `service_ids` and assert `service_allowlist_not_supported` before the provisioning facade is called. Assert the tool schema still omits the field. Assert valid owner list results include `authorization_mode = nyxid_default` and authoritative `state_version`, true legacy rows omit the mode, and neither surface contains `secret_reference`, grant fields, Vault descriptors, or secret values.

- [x] **Step 3: Run the focused projection/endpoint/tool tests and verify RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --filter "FullyQualifiedName~ChannelBotRegistrationProjectorTests|FullyQualifiedName~ChannelCallbackEndpointsTests|FullyQualifiedName~ChannelRegistrationToolTests"
```

Expected: failures because new facts are not materialized, unsupported input is ignored, and owner views omit mode/version.

- [x] **Step 4: Implement projection cloning and boundary rejection**

Clone the new message in both projector directions. Inspect raw HTTP JSON before DTO mapping and tool arguments before registration. Map `secret_vault_unavailable` to 503 and `channel_authorization_contract_invalid` to 409. Expose only the normalized mode string and version. `QueryAllSnapshotsAsync` returns
each mapped registration and `StateVersion` from one projection document query.
HTTP and tool owner lists consume that atomic snapshot and do not call
`GetStateVersionAsync` per row.

- [x] **Step 5: Run focused projection/endpoint/tool tests and verify GREEN**

Rerun the Step 3 filter. Expected: all selected tests pass.

### Task 6: Document The Decision And Verify Repository Gates

**Files:**
- Create: `docs/adr/0051-unified-channel-agent-key.md`
- Modify: `docs/README.md`
- Modify: `docs/superpowers/plans/2026-09-09-unified-channel-agent-key.md`

- [x] **Step 1: Add the architecture decision**

Document one Actor-owned Channel Agent Key fact, Vault-only raw material, new/legacy read rules, strict provider parsing, Lark/Telegram ordering, and explicit deferral of allowlists, scope-plan, general rotation, import/mode-switch, compatibility removal, and unified deletion authorization. Include a Mermaid sequence with the repository-required init directive.

- [x] **Step 2: Run focused tests and repository guards**

Status: the full Channel Runtime test project passed 2217/2217. The test stability,
workflow binding boundary, query projection priming, projection state version,
projection state mirror, projection route mapping, solution split, and documentation
guards passed. `git diff --check` also passed. The aggregate architecture guard was
executed and exits 1 only for the pre-existing NyxID conformance revision mismatch in
`src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`, which is not
changed by this work.

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/ci/solution_split_guards.sh
bash tools/docs/lint.sh
```

Expected: every command exits 0.

- [x] **Step 3: Run a fresh solution build and full test attempt**

Status: the fresh solution build completed with 0 errors and 153 warnings. The bounded full-test
attempt reported zero failures from every completed test project, including
Channel Runtime 2217/2217, then stalled with only
`Aevatar.AI.ToolProviders.AevatarInvocation.Tests` still running for more than three
minutes. That test command was interrupted, and no test or VSTest processes remained.

```bash
dotnet build aevatar.slnx --nologo --no-restore
dotnet test aevatar.slnx --nologo --no-build
```

Expected: build exits 0. Full tests should exit 0; if the pre-existing `Aevatar.AI.ToolProviders.AevatarInvocation.Tests` host stalls again, capture completed results, terminate only that test command, and report the baseline limitation without modifying unrelated code.

- [x] **Step 4: Review the final diff against §1–§16**

Confirm no `service_ids` schema, explicit mode, allowlist persistence, scope-plan call, exact connection preplanning, replace/import/mode-switch endpoint, general rotation, compatibility removal, deletion authorization redesign, or frontend Service selector was introduced. Record deferred findings in the handoff instead of implementing them.

The completed Tasks 1–6 remain a historical default-only execution record. Their
requirements to reject `service_ids` and defer explicit allowlists, scope planning,
and exact connection preplanning are superseded by Tasks 7–13. The following
tasks define the final §§1–19 delivery without rewriting the earlier evidence.

### Task 7: Extend The Strongly Typed Contract For Two Creation Modes

**Files:**
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/protos/channel_bot_registration.proto`
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/ChannelRegistrationAuthorizationContract.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelBotRegistrationProtoCompatibilityTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelRegistrationAuthorizationContractTests.cs`

- [x] **Step 1: Write failing Protobuf compatibility tests**

Require `EXPLICIT_SERVICE_ALLOWLIST = 2`, `ChannelRegistrationServiceAllowlist.service_ids = 1`,
Entry field 18, Command field 13, Document field 19, and grant `scope_plan_digest = 3`. Cover
message presence for an explicitly empty allowlist and the existing optional false booleans.

- [x] **Step 2: Run the focused tests and verify RED**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --filter FullyQualifiedName~ChannelBotRegistrationProtoCompatibilityTests
```

- [x] **Step 3: Add the exact approved Protobuf fields and enum value**

Use only the field numbers in §6.1/§18.1. Do not add replacement commands/events or a
persisted runtime-dependency list.

- [x] **Step 4: Write failing authorization-contract tests for both modes**

Require default mode to omit allowlist/digest. Require explicit mode to carry the allowlist
message even when empty, set both `allow_all_*` values explicitly false, carry a nonempty valid
digest, keep business IDs as a subset of the actual grant, and preserve exact legacy aliases.
Reject unknown modes and any partially present new-model shape without legacy fallback.

- [x] **Step 5: Implement the minimal classifier/validator and verify GREEN**

### Task 8: Preserve And Validate `service_ids` Presence At HTTP And Tool Boundaries

**Files:**
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelCallbackEndpoints.cs`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelRegistrationCommandFacade.cs`
- Modify: `src/Aevatar.AI.ToolProviders.ChannelAdmin/ChannelRegistrationTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.ChannelAdmin/ChannelRegistrationToolSource.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelCallbackEndpointsTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelRegistrationToolTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelRegistrationCommandFacadeTestSupport.cs`

- [x] **Step 1: Write failing input-matrix tests**

Cover field absence, explicit `null`, explicit `[]`, canonicalizable strings, non-array values,
null/non-string/empty/whitespace elements, duplicates, and ordinal ordering. Assert invalid input
returns `400 invalid_service_ids` before any Service lookup, connection, scope-plan, Key, Vault,
bot, route, or Actor action.

- [x] **Step 2: Expose the optional tool argument and preserve presence**

Keep absence distinct from an explicitly empty array. Normalize legal values once at the boundary,
then pass a typed creation authorization request into application orchestration. Do not expose
`authorization_mode`, grant, digest, or runtime dependencies as caller-controlled inputs.

- [x] **Step 3: Run the focused endpoint/tool tests and verify GREEN**

### Task 9: Verify Exact NyxID Service Identity And Scope-Plan Contracts

**Files:**
- Modify or create narrow contracts under `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/`
- Reuse: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`
- Reuse: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiAccessContracts.cs`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/DependencyInjection/NyxIdRelayChannelServiceCollectionExtensions.cs`
- Add focused tests under `test/Aevatar.GAgents.ChannelRuntime.Tests/`

- [x] **Step 1: Confirm the sibling NyxID public implementation**

Record the actual v1 request/response fields, exact `UserService` identity and owner representation,
connection-create response, and create-key restriction fields. Aevatar may adapt the public contract
but must not require NyxID changes.

- [x] **Step 2: Write failing exact-Service and owner tests**

Require every normalized input to resolve to the same exact `UserService.id`. Reject slug, name,
catalog ID, prefix, missing Service, mismatched personal owner, and unauthorized organization owner
with the stable §13 errors and no later side effects.

- [x] **Step 3: Write failing scope-plan validation tests**

Reuse `PlanApiKeyScopeAsync` and `NyxIdApiAccessResponseParser.ParseScopePlan`. Validate authority,
contract/policy versions, authenticated actor, intended key owner, per-Service resource owner,
input/Service/Node set equality, freshness, completeness, RFC 3339 evaluation time, and the published
`sha256:` digest form. Personal requests omit `target_org_id`; authorized organization requests set it.

- [x] **Step 4: Implement narrow Infrastructure adapters and verify GREEN**

Do not add a second JSON parser or locally compute/reorder provider facts.

### Task 10: Plan Explicit Grants And Resolve Required Bot Connections

**Files:**
- Modify or create application orchestration under `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxLarkProvisioningService.cs`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxTelegramProvisioningService.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/NyxLarkProvisioningServiceTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/NyxTelegramProvisioningServiceTests.cs`
- Add focused authorization-planning tests under `test/Aevatar.GAgents.ChannelRuntime.Tests/`

- [x] **Step 1: Write failing dependency-union tests**

Require the scope-plan input to be the canonical union of the business allowlist, the exact current
Lark/Telegram proxy connection when required, and current authoritative Skill/Workflow/LLM/Channel
dependencies. Internal dependencies must not be persisted as business allowlist entries. Missing or
invalid required dependencies fail closed instead of selecting default mode.

- [x] **Step 2: Write failing precise-connection and cleanup tests**

For explicit Lark, resolve or create and verify this bot's exact connection before scope-plan/Key
creation, including owner, platform app identity, purpose, exact `UserService.id`, and dedicated slug.
Track whether a connection is newly created and exclusively owned before allowing compensation.
Telegram relay-only must not create a proxy connection unless authoritative configuration requires it.

- [x] **Step 3: Implement the narrow planning/connection flow and verify GREEN**

Keep default-mode platform order and best-effort Lark connection behavior unchanged.

### Task 11: Provision And Strictly Validate Explicitly Restricted Agent Keys

**Files:**
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/ChannelAgentKeyProvisioningService.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelAgentKeyProvisioningServiceTests.cs`

- [x] **Step 1: Write failing explicit-request and response tests**

Require `read write proxy`, both `allow_all_* = false`, scope-plan Service/Node arrays, and
`scope_plan_digest = normalized_grant_digest`. Require the create response to match the verified
plan exactly. Cover provider drift/`scope_plan_changed`, malformed response, cancellation, Vault
failure, and detached bounded compensation without secret leakage.

- [x] **Step 2: Extend the shared provisioner with a typed authorization plan**

Default mode must continue omitting all restriction fields. Explicit mode may accept only a verified
typed scope plan, never raw HTTP `service_ids`.

- [x] **Step 3: Run provisioning and both platform integration tests and verify GREEN**

### Task 12: Commit, Project, Query, And Enforce The Explicit Model

**Files:**
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/ChannelBotRegistrationGAgent.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/ChannelBotRegistrationProjector.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/ChannelBotRegistrationQueryPort.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Runtime/IChannelBotRegistrationQueryPort.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs` and the narrow registration-authority call path it uses
- Modify focused Actor/projector/query/runtime tests under `test/Aevatar.GAgents.ChannelRuntime.Tests/`

- [x] **Step 1: Write failing Actor/projection/query tests**

Assert explicit empty/nonempty allowlists, digest, mode, grant, aliases, optional presence, replay,
reactivation, projection rebuild, and authoritative state version survive unchanged. Default owner
views omit `service_ids`; explicit views return the complete list including `[]`; legacy rows invent
neither mode nor list; no query exposes Vault references, grant, dependency details, or digest.

- [x] **Step 2: Write failing registration-authority admission tests**

For explicit mode, allow a business-listed target or a target proven as an authoritative dependency
at its configured call site only when it is also in the actual Key grant. Reject unknown targets,
grant-only extras, a dependency used outside its call site, invalid contracts, and missing sender
authority without falling back to the registration Key. Keep target Service endpoint/operation/
approval/ownership enforcement untouched.

- [x] **Step 3: Implement Actor ownership, projection cloning, safe query mapping, and narrow admission**

Read registration facts only from the current-state read model and dependencies only from their
authoritative configuration contracts. Do not add query-time replay, Actor state reads, projection
priming, or a process-local registration/dependency registry.

- [x] **Step 4: Run all focused tests and verify GREEN**

### Task 13: Update Decision Records And Release Gates

**Files:**
- Modify: `docs/adr/0051-unified-channel-agent-key.md`
- Modify: `docs/README.md`
- Modify: `docs/superpowers/plans/2026-09-09-unified-channel-agent-key.md`

- [x] **Step 1: Document the two-mode current implementation**

Replace the old allowlist deferral with the approved §1–§19 behavior while preserving explicit
deferral of §20–§25. Update diagrams with the required Mermaid init directive.

Post-implementation hardening: both platform provisioning entries resolve the
authenticated actor and registration owner once, before any NyxID mutation, and
pass the same `VerifiedChannelRegistrationOwner` into explicit planning or
default-key creation. No downstream path re-resolves or infers the owner.

Known limitation: if NyxID commits Create Key but its response is lost before the
key ID reaches Aevatar, the current public contract does not provide an exact ID
for compensation and an orphan provider key may remain. Correlation and recovery
for that case are deferred with general key recovery; this release does not add a
retry, lookup heuristic, or external protocol.

- [x] **Step 2: Record the observability deferral**

User override: this delivery adds no Channel registration Meter or host Meter registration. The
historical NyxID relay callback JWT validation Meter remains unchanged. Record low-cardinality
business metrics for default/explicit/legacy classifications and input, exact-Service, connection,
scope-plan, admission, provisioning, and cleanup outcomes as deferred work rather than implementing
instruments, call sites, or tests in this release.

- [x] **Step 3: Run focused and repository verification**

```bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --no-restore --nologo
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/ci/solution_split_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
git diff --check
dotnet build aevatar.slnx --nologo --no-restore
dotnet test aevatar.slnx --nologo --no-build
```

Fresh verification on 2026-09-11: the focused owner/planning/provisioning set
passed 235/235 and the full Channel Runtime project passed 2489/2489. Test
stability, workflow binding, query projection priming, both projection state
guards, projection route mapping, solution split, documentation lint, and
`git diff --check` passed. The full solution build completed with 0 errors. The
aggregate architecture guard still exits 1 only for the pre-existing NyxID
conformance revision mismatch in the unmodified
`src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`. The full
solution test command reported zero failures from every completed project, then
was interrupted after more than three minutes with only the pre-existing
`Aevatar.AI.ToolProviders.AevatarInvocation.Tests` host still running; no test
process remained afterward.

- [x] **Step 4: Audit the final diff against the deferred boundary**

Confirm there is no existing-registration allowlist replacement, default-to-explicit switch,
legacy import/migration, general key rotation, compatibility removal, unified deletion-authorization
redesign, or frontend selector. Record any useful follow-up without implementing it.

Final audit found no new Channel registration Meter, no frontend changes, no
unsafe ownerless default-key provisioning entry, no downstream owner
re-resolution, and no name/slug/catalog/prefix inference of `UserService.id`.
The existing versioned registration snapshot path and `StateVersion > 0`
admission requirement remain unchanged by the final hardening pass.
