# Scheduled Agent Key Runtime Integrity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a scheduled Team workflow use one integrity-bound durable decision for its Agent Key, verified caller binding, exact owner LLM route, exact NyxID UserService ID, and model.

**Architecture:** The committed UserConfig read model supplies a typed owner LLM selection to the existing authorization planner. The planner validates and includes that selection in its protobuf digest; Studio copies it into the schedule authorization fact and derives persisted `ChatRequestEvent.LlmControl` from that fact. Scheduled Dispatch stores and projects the same fact, then fails closed before invocation if caller authority, exact service grant, route, or model disagrees.

**Tech Stack:** .NET 10, C#, Google Protobuf, xUnit, FluentAssertions, CQRS current-state projection, actor-owned scheduled dispatch state.

## Global Constraints

- Preserve `Domain / Application / Infrastructure / Host` dependency direction; API remains an adapter.
- Fire-time execution must not query UserConfig, replay events, or fill route/model from host defaults.
- Persist core semantics as typed Protobuf fields, never JSON or a generic bag.
- Keep Agent Key material in `ISecretVault`; never log or project bearer tokens, raw keys, refresh tokens, or ciphertext.
- Preserve distinct `Unspecified`, `Gateway`, and `NyxIdUserService` states.
- An Agent Key workflow must have complete caller authority including `BindingId` before invocation.
- A required owner LLM selection must have a canonical non-empty model and be covered by the plan digest.
- A `NyxIdUserService` selection must match the exact granted service ID, canonical route, and slug snapshot.
- Drain v1 pending Team operations operationally; do not add v1-to-v2 digest compatibility.
- Do not add polling tests. Run all workflow/query/projection/architecture/documentation guards before push.

---

### Task 1: Bind Exact Owner LLM Selection Into the Authorization Plan

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Protos/scheduled_invocation_authorization_plan.proto`
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs`
- Modify: `src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationPlanner.cs`
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionScheduledInvocationAuthorityQueryPorts.cs`
- Test: `test/Aevatar.GAgentService.Tests/Authorization/ScheduledInvocationAuthorizationPlannerTests.cs`
- Test: `test/Aevatar.Studio.Tests/ProjectionScheduledInvocationAuthorityQueryPortTests.cs`

**Interfaces:**
- Consumes: `UserConfigCurrentStateDocument.LlmSelection`, `DefaultModel`, and the existing NyxID catalog snapshot.
- Produces: `ScheduledInvocationOwnerLLMSelection`, `ScheduledInvocationOwnerLLMEvidence(long StateVersion, ScheduledInvocationOwnerLLMSelection Selection)`, and `ScheduledInvocationAuthorizationPlan.OwnerLlmSelection`.

- [ ] **Step 1: Write failing query-port tests for all three states**

Assert exact service, Gateway, and absent/invalid typed selection without legacy slug inference:

```csharp
result!.Selection.RouteKind.Should().Be(ScheduledInvocationOwnerLLMRouteKind.NyxIdUserService);
result.Selection.RouteValue.Should().Be("/api/v1/proxy/s/chrono-llm-public");
result.Selection.NyxIdUserServiceId.Should().Be("us-chrono");
result.Selection.ServiceSlugSnapshot.Should().Be("chrono-llm-public");
result.Selection.Model.Should().Be("gpt-5.5");
unspecified!.Selection.RouteKind.Should().Be(ScheduledInvocationOwnerLLMRouteKind.Unspecified);
unspecified.Selection.RouteValue.Should().BeEmpty();
unspecified.Selection.Model.Should().BeEmpty();
```

- [ ] **Step 2: Run the query tests to prove red**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter FullyQualifiedName~ProjectionScheduledInvocationAuthorityQueryPortTests
```

Expected: FAIL because selection/model are not part of evidence and legacy inference remains.

- [ ] **Step 3: Add the digest-covered protobuf contract**

Add field `12` without renumbering existing fields:

```proto
enum ScheduledInvocationOwnerLLMRouteKind {
  SCHEDULED_INVOCATION_OWNER_LLM_ROUTE_KIND_UNSPECIFIED = 0;
  SCHEDULED_INVOCATION_OWNER_LLM_ROUTE_KIND_GATEWAY = 1;
  SCHEDULED_INVOCATION_OWNER_LLM_ROUTE_KIND_NYX_ID_USER_SERVICE = 2;
}
message ScheduledInvocationOwnerLLMSelection {
  ScheduledInvocationOwnerLLMRouteKind route_kind = 1;
  string route_value = 2;
  string nyx_id_user_service_id = 3;
  string service_slug_snapshot = 4;
  string model = 5;
}
message ScheduledInvocationAuthorizationPlan {
  ScheduledInvocationOwnerLLMSelection owner_llm_selection = 12;
}
```

- [ ] **Step 4: Add one canonical validation policy and typed evidence**

```csharp
public sealed record ScheduledInvocationOwnerLLMEvidence(
    long StateVersion,
    ScheduledInvocationOwnerLLMSelection Selection);

public static class ScheduledInvocationOwnerLLMSelectionPolicy
{
    public const string GatewayRoute = "/api/v1/llm/gateway/v1";
    public const string NyxIdProxyRoutePrefix = "/api/v1/proxy/s/";

    public static bool IsDurableSelectionValid(ScheduledInvocationOwnerLLMSelection? value) =>
        value?.RouteKind switch
        {
            ScheduledInvocationOwnerLLMRouteKind.Gateway =>
                value.RouteValue == GatewayRoute && Canonical(value.Model) &&
                value.NyxIdUserServiceId.Length == 0 && value.ServiceSlugSnapshot.Length == 0,
            ScheduledInvocationOwnerLLMRouteKind.NyxIdUserService =>
                Canonical(value.RouteValue) && Canonical(value.NyxIdUserServiceId) &&
                Canonical(value.ServiceSlugSnapshot) && Canonical(value.Model) &&
                !value.ServiceSlugSnapshot.Contains('/') &&
                value.RouteValue == $"{NyxIdProxyRoutePrefix}{value.ServiceSlugSnapshot}",
            _ => false,
        };

    private static bool Canonical(string value) => value.Length > 0 && value == value.Trim();
}
```

- [ ] **Step 5: Map committed typed UserConfig only**

Delete `MapLegacyEvidence` and route-to-slug inference. Map `LlmSelection` plus `DefaultModel`; null or malformed typed state becomes an empty `Unspecified` selection.

- [ ] **Step 6: Write failing planner tests**

Cover Gateway, exact service, `Unspecified`, blank model, noncanonical route, missing ID, and route/slug mismatch. Mutating any selection field must change the protobuf digest:

```csharp
var changed = result.Plan!.Clone();
changed.OwnerLlmSelection.Model = "gpt-other";
ScheduledInvocationAuthorizationPlanIntegrity.ComputeDigest(changed)
    .Should().NotBe(result.Plan.PermissionDigest);
```

- [ ] **Step 7: Populate the selection in the single planner trunk**

Return selection from `TargetEvidenceResolution`. Add an exact capability only for `NyxIdUserService`; leave grants unchanged for Gateway. Reject invalid/unspecified required selections before catalog access, then clone the selection before digest computation:

```csharp
if (evidence.OwnerLLMSelection is not null)
    plan.OwnerLlmSelection = evidence.OwnerLLMSelection.Clone();
plan.PermissionDigest = ComputeDigest(plan);
```

- [ ] **Step 8: Verify and commit**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter FullyQualifiedName~ProjectionScheduledInvocationAuthorityQueryPortTests
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter FullyQualifiedName~ScheduledInvocationAuthorizationPlannerTests
git add src/platform/Aevatar.GAgentService.Abstractions src/platform/Aevatar.GAgentService.Application/Schedules/Authorization src/Aevatar.Studio.Projection/QueryPorts/ProjectionScheduledInvocationAuthorityQueryPorts.cs test/Aevatar.GAgentService.Tests/Authorization/ScheduledInvocationAuthorizationPlannerTests.cs test/Aevatar.Studio.Tests/ProjectionScheduledInvocationAuthorityQueryPortTests.cs
git commit -m "Bind owner LLM selection into schedule authorization"
```

Expected: both suites PASS.

### Task 2: Persist the Selection Fact and Build Chat Payload From It

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledDispatchModels.cs`
- Modify: `src/platform/Aevatar.GAgentService.Core/Aevatar.GAgentService.Core.csproj`
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto`
- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/Schedules/ScheduledDispatchActorPort.cs`
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchApplicationServiceTests.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs`

**Interfaces:**
- Consumes: validated plan selection from Task 1.
- Produces: `ScheduledInvocationAuthorizationFact.OwnerLLMSelection`, actor-state selection, and persisted `ChatRequestEvent.LlmControl`.

- [ ] **Step 1: Write failing plan-to-fact and payload tests**

Assert create, reauthorize, and update carry exact selection and a route intentionally different from host defaults:

```csharp
configuration.Target.ServiceInvocation!.AuthorizationFact!.OwnerLLMSelection
    .Should().BeEquivalentTo(plan.OwnerLlmSelection);
var chat = configuration.Target.ServiceInvocation.Payload.Unpack<ChatRequestEvent>();
chat.LlmControl.ModelOverride.Should().Be("gpt-5.5");
chat.LlmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm-public");
```

- [ ] **Step 2: Run Studio schedule tests to prove red**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter FullyQualifiedName~StudioMemberWorkflowSchedulePortTests
```

Expected: FAIL because fact and payload omit selection.

- [ ] **Step 3: Extend the runtime fact and actor Protobuf state**

Append an optional fact member and state field `11`:

```csharp
ScheduledInvocationOwnerLLMSelection? OwnerLLMSelection = null
```

```proto
import "scheduled_invocation_authorization_plan.proto";
aevatar.gagentservice.schedules.authorization.ScheduledInvocationOwnerLLMSelection owner_llm_selection = 11;
```

Add the workflow proto directory to Core's `AdditionalImportDirs` because the imported plan depends on authorization evidence.

- [ ] **Step 4: Map the selection through actor ingress and runtime reconstruction**

Use clones in both directions so no mutable protobuf instance is shared:

```csharp
OwnerLlmSelection = fact.OwnerLLMSelection?.Clone(),
```

Add state-round-trip coverage for create/update/reauthorize and replacement-pending fire.

- [ ] **Step 5: Derive LLM control only from the fact**

Use one helper from `BuildScheduleConfiguration`:

```csharp
private static ChatRequestEvent BuildChatRequest(
    string prompt, string scopeId, ScheduledInvocationAuthorizationFact? fact)
{
    var request = new ChatRequestEvent { Prompt = prompt, ScopeId = scopeId };
    if (fact?.OwnerLLMSelection is { } selection &&
        selection.RouteKind != ScheduledInvocationOwnerLLMRouteKind.Unspecified)
    {
        if (!ScheduledInvocationOwnerLLMSelectionPolicy.IsDurableSelectionValid(selection))
            throw new InvalidOperationException("scheduled_owner_llm_selection_invalid");
        request.LlmControl = new LLMControlContextPayload
        {
            ModelOverride = selection.Model,
            NyxIdRoutePreference = selection.RouteValue,
        };
    }
    return request;
}
```

Update must pass `ToScheduleAuthorizationFact(validation.ValidatedPlan!.Plan)`; actor normalization preserves the existing auth when only a fresh fact is supplied.

- [ ] **Step 6: Verify and commit**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter FullyQualifiedName~StudioMemberWorkflowSchedulePortTests
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter FullyQualifiedName~ScheduledDispatchApplicationServiceTests
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter FullyQualifiedName~ScheduledDispatchGAgentTests
git add src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledDispatchModels.cs src/platform/Aevatar.GAgentService.Core src/platform/Aevatar.GAgentService.Infrastructure/Schedules/ScheduledDispatchActorPort.cs src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchApplicationServiceTests.cs test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs
git commit -m "Persist scheduled owner LLM runtime selection"
```

Expected: all three suites PASS.

### Task 3: Fail Closed on Caller Authority and Payload/Fact Drift

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledServiceInvocationAuthorizationFailure.cs`
- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/Schedules/ScheduledServiceInvocationDispatchPort.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchServiceInvocationTests.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs`

**Interfaces:**
- Consumes: persisted fact, persisted chat payload, Agent Key auth, and workflow projection flag.
- Produces: stable typed failures and existing actor transition to `NeedsAuthorization`.

- [ ] **Step 1: Replace authority-less success with failing tests**

Cover null authority and blank `Platform`, `ExternalUserId`, `Scope`, or `BindingId`. Assert no invocation, exchange, or vault access:

```csharp
var failure = await act.Should().ThrowAsync<ScheduledServiceInvocationAuthorizationException>();
failure.Which.Code.Should().Be(ScheduledServiceInvocationAuthorizationFailureCode.CallerAuthorityInvalid);
failure.Which.StableCode.Should().Be("caller_authority_invalid");
invocationPort.Requests.Should().BeEmpty();
```

- [ ] **Step 2: Write failing payload/fact tests**

Cover missing/malformed required selection, exact service absent from grants, non-chat payload, route mismatch, and model mismatch. Prove a valid exact route/model dispatch unchanged.

- [ ] **Step 3: Run dispatch tests to prove red**

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter FullyQualifiedName~ScheduledDispatchServiceInvocationTests
```

Expected: FAIL because missing authority reaches invocation and route/model drift is unchecked.

- [ ] **Step 4: Add typed stable failure codes**

```csharp
CallerAuthorityInvalid = 8,
OwnerLLMSelectionInvalid = 9,
OwnerLLMPayloadMismatch = 10,
```

Map them to `caller_authority_invalid`, `owner_llm_selection_invalid`, and `owner_llm_payload_mismatch`.

- [ ] **Step 5: Validate before credential work**

```csharp
if (dispatch.ProjectNyxIdAccessTokenToWorkflowCallerCredential &&
    dispatch.Auth?.Source is ScheduledInvocationAgentKeyCredentialReference)
{
    ValidateCompleteCallerAuthority(dispatch.Auth.CallerAuthority);
    ValidateOwnerLLMSelectionAndPayload(dispatch.Request, fact);
}
```

Use `fact.Authority.OwnerLlmStateVersion > 0` as the requirement signal. Required selection must pass the shared policy; exact-service kind must appear in grants; route/model must exactly equal persisted `ChatRequestEvent.LlmControl`. With no owner-LLM source stamp, both route and model must be empty.

- [ ] **Step 6: Extend actor failure tests**

Add all three failure codes to the existing authorization failure theory and assert lifecycle `NeedsAuthorization`, cleared next-fire lease, and stable `LastAuthorizationErrorCode`.

- [ ] **Step 7: Verify and commit**

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter FullyQualifiedName~ScheduledDispatchServiceInvocationTests
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter FullyQualifiedName~ScheduledDispatchGAgentTests
git add src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledServiceInvocationAuthorizationFailure.cs src/platform/Aevatar.GAgentService.Infrastructure/Schedules/ScheduledServiceInvocationDispatchPort.cs test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchServiceInvocationTests.cs test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs
git commit -m "Fail closed on scheduled Agent Key drift"
```

Expected: both suites PASS.

### Task 4: Preserve Honest `Unspecified` UserConfig Semantics

**Files:**
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionUserConfigQueryPort.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/UserLlmPreferenceService.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/UserLlmSettingsViewBuilder.cs`
- Test: `test/Aevatar.Studio.Tests/ProjectionUserConfigQueryPortTests.cs`
- Test: `test/Aevatar.Studio.Tests/UserLlmSettingsViewBuilderTests.cs`
- Test: `test/Aevatar.Studio.Tests/UserConfigServiceTests.cs`
- Test: `test/Aevatar.Studio.Tests/UserConfigControllerSettingsTests.cs`
- Test: `test/Aevatar.Capabilities.Tests/StudioUserConfigOwnerLlmConfigSourceTests.cs`

**Interfaces:**
- Produces: `UserConfig(LlmSelection: null, PreferredLlmRoute: "")`, settings `SavedRouteKind = "unspecified"`, and no explicit runtime route.

- [ ] **Step 1: Write failing missing-state tests**

```csharp
config.LlmSelection.Should().BeNull();
config.PreferredLlmRoute.Should().BeEmpty();
view.SavedRouteKind.Should().Be(UserLlmSelectionKindWire.Unspecified);
view.SavedRoute.Should().BeEmpty();
view.EffectiveRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
```

- [ ] **Step 2: Run focused tests to prove red**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~ProjectionUserConfigQueryPortTests|FullyQualifiedName~UserLlmSettingsViewBuilderTests|FullyQualifiedName~UserConfigServiceTests|FullyQualifiedName~UserConfigControllerSettingsTests"
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter FullyQualifiedName~StudioUserConfigOwnerLlmConfigSourceTests
```

Expected: FAIL because query/settings promote empty route to Gateway.

- [ ] **Step 3: Keep compatibility route empty and branch on typed kind**

Remove query-port Gateway fallback:

```csharp
PreferredLlmRoute: document.PreferredLlmRoute ?? string.Empty,
```

and missing document:

```csharp
PreferredLlmRoute: string.Empty,
```

Do not normalize an empty saved route before resolving kind. Return `SavedSelection(Unspecified, string.Empty, null, null)` for missing/unspecified typed state, while retaining Gateway as the effective UI fallback only.

- [ ] **Step 4: Verify and commit**

Run Step 2 commands; expect PASS.

```bash
git add src/Aevatar.Studio.Projection/QueryPorts/ProjectionUserConfigQueryPort.cs src/Aevatar.Studio.Application/Studio/Services/UserLlmPreferenceService.cs src/Aevatar.Studio.Application/Studio/Services/UserLlmSettingsViewBuilder.cs test/Aevatar.Studio.Tests test/Aevatar.Capabilities.Tests/StudioUserConfigOwnerLlmConfigSourceTests.cs
git commit -m "Preserve unspecified owner LLM semantics"
```

### Task 5: Expose Runtime Evidence for Production Acceptance

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Projection/service_projection_read_models.proto`
- Modify: `src/platform/Aevatar.GAgentService.Projection/Projectors/ScheduledDispatchCurrentStateProjector.cs`
- Modify: `src/platform/Aevatar.GAgentService.Projection/Queries/ScheduledDispatchQueryPort.cs`
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledDispatchModels.cs`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Provisioning/StudioMemberWorkflowScheduleContracts.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs`
- Test: `test/Aevatar.GAgentService.Tests/Projection/ScheduledDispatchCurrentStateProjectorTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchApplicationServiceTests.cs`
- Test: `test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs`
- Test: `test/Aevatar.GAgentService.Integration.Tests/ScheduledDispatchEndpointsTests.cs`

**Interfaces:**
- Produces: route kind, route, exact service ID, slug, and model in schedule and Studio views. Caller authority remains excluded from projection/read-model payloads.

- [ ] **Step 1: Write failing projector/query tests**

```csharp
document.OwnerLlmRouteKind.Should().Be("nyx_id_user_service");
document.OwnerLlmRoute.Should().Be("/api/v1/proxy/s/chrono-llm-public");
document.OwnerLlmUserServiceId.Should().Be("us-chrono");
document.OwnerLlmServiceSlug.Should().Be("chrono-llm-public");
document.OwnerLlmModel.Should().Be("gpt-5.5");
```

- [ ] **Step 2: Run projection tests to prove red**

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "FullyQualifiedName~ScheduledDispatchCurrentStateProjectorTests|FullyQualifiedName~ScheduledDispatchApplicationServiceTests"
```

Expected: FAIL because acceptance fields do not exist.

- [ ] **Step 3: Add flat read-model fields**

```proto
string owner_llm_route_kind = 58;
string owner_llm_route = 59;
string owner_llm_user_service_id = 60;
string owner_llm_service_slug = 61;
string owner_llm_model = 62;
```

Project from `ActiveTeamAuthorizationFact ?? Target.ServiceInvocation.AuthorizationFact` and persisted auth only; do not recalculate or query UserConfig.

- [ ] **Step 4: Map query, Studio API, and Studio tool views**

Append source-compatible init properties:

```csharp
public string OwnerLLMRouteKind { get; init; } = "unspecified";
public string OwnerLLMRoute { get; init; } = string.Empty;
public string OwnerLLMUserServiceId { get; init; } = string.Empty;
public string OwnerLLMServiceSlug { get; init; } = string.Empty;
public string OwnerLLMModel { get; init; } = string.Empty;
```

- [ ] **Step 5: Add endpoint serialization tests**

Assert exact fields appear and raw credential material does not:

```csharp
payload.GetProperty("ownerLLMUserServiceId").GetString().Should().Be("us-chrono");
payload.GetProperty("ownerLLMModel").GetString().Should().Be("gpt-5.5");
json.Should().NotContain("full_key").And.NotContain("ciphertext");
```

- [ ] **Step 6: Verify and commit**

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "FullyQualifiedName~ScheduledDispatchCurrentStateProjectorTests|FullyQualifiedName~ScheduledDispatchApplicationServiceTests"
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter FullyQualifiedName~StudioMemberWorkflowSchedulePortTests
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --filter FullyQualifiedName~ScheduledDispatchEndpointsTests
git add src/platform/Aevatar.GAgentService.Projection src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledDispatchModels.cs src/Aevatar.Studio.Application.Abstractions/Provisioning/StudioMemberWorkflowScheduleContracts.cs src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs test/Aevatar.GAgentService.Tests/Projection test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchApplicationServiceTests.cs test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs test/Aevatar.GAgentService.Integration.Tests/ScheduledDispatchEndpointsTests.cs
git commit -m "Expose scheduled owner LLM runtime evidence"
```

Expected: all focused suites PASS.

### Task 6: Document Rollout, Verify, Review, and Push

**Files:**
- Modify: `docs/canon/nyxid-llm-integration.md`
- Modify: `docs/canon/scheduled-skill-runners.md`
- Create: `docs/operations/2026-07-23-scheduled-agent-key-runtime-integrity-rollout.md`

**Interfaces:**
- Produces: executable v1 drain, atomic deployment, production canary, revocation, and cleanup procedure.

- [ ] **Step 1: Document canonical runtime semantics**

```text
committed UserConfig selection
  -> digest-covered plan
  -> constrained Agent Key + Vault reference
  -> actor-owned fact + persisted ChatRequestEvent.LlmControl
  -> runtime caller/payload/fact cross-check
  -> workflow inbox
```

Explicitly forbid fire-time UserConfig query, host-default fill, legacy slug inference, missing-binding fallback, and v1 digest compatibility.

- [ ] **Step 2: Write the operational drain and canary**

Require: immediately re-audit zero `ProvisioningPending`/`ReplacementPending` v1 operations; pause/reauthorize any active schedule lacking authority; deploy plan/fact/state/projector/API together; reselect service ID `4061b904-62de-4cee-9125-5e3ec8365afd`, route `/api/v1/proxy/s/chrono-llm-public`, model `gpt-5.5`; confirm both allow-all flags false; create distinct Team/member/workflow/service/schedule IDs; record the verified binding used at creation without projecting caller authority; inspect the exact persisted LLM selection; run `simple_qa` so the runtime caller-authority guard proves the binding path; delete and verify NyxID key plus Vault secret revocation; retire all temporary resources and confirm an empty automation list.

- [ ] **Step 3: Run all affected suites**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo
```

Expected: zero failures.

- [ ] **Step 4: Run build and required guards**

```bash
dotnet build aevatar.slnx --nologo
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

Expected: build has zero errors and every guard exits `0`.

- [ ] **Step 5: Commit docs and request whole-branch reviews**

```bash
git add docs/canon/nyxid-llm-integration.md docs/canon/scheduled-skill-runners.md docs/operations/2026-07-23-scheduled-agent-key-runtime-integrity-rollout.md
git commit -m "Document scheduled Agent Key integrity rollout"
```

Review the full `origin/feature/integrate...HEAD` range for spec compliance and code quality. Resolve all High/Medium correctness or security findings and rerun affected tests plus Step 4.

- [ ] **Step 6: Fetch, rebase, verify, and push directly**

```bash
git fetch git@github.com:aevatarAI/aevatar.git refs/heads/feature/integrate:refs/remotes/origin/feature/integrate
git rebase origin/feature/integrate
dotnet build aevatar.slnx --nologo
git push git@github.com:aevatarAI/aevatar.git HEAD:feature/integrate
```

Expected: non-force push succeeds. If rebase changes code, rerun Tasks 6.3 and 6.4 before push.

- [ ] **Step 7: Execute the production canary after deployment**

Use local `nyxid` CLI and authenticated production Aevatar API. Capture only IDs, route/model, boolean policy flags, lifecycle status, HTTP codes, state versions, and stable errors. Finish only after automation deletion, exact NyxID key and Vault secret revocation, temporary-resource cleanup, and an empty automation list. Never print `~/.nyxid/access_token`, bearer tokens, raw keys, refresh tokens, or ciphertext.
