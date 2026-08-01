# Atomic LLM Selection and Workflow Invocation Admission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Make route and model one authoritative UserConfig fact, authorize durable LLM targets from committed exact catalog evidence, and reject incompatible workflow artifacts before any actor lifecycle mutation.

**Architecture:** Add one shared protobuf selection contract in Aevatar.AI.Abstractions, reuse UserConfigGAgent and NyxIdAuthorizationCatalogGAgent as the only authoritative owners, and keep queries projection-only. Reuse WorkflowDefinitionParser, WorkflowAuthorizationDependencyEvaluator, and WorkflowCapabilityAdmissionPlanIntegrity behind one Application preflight port that WorkflowRunActorPort calls before create, link, repair, bind, or dispatch.

**Tech Stack:** .NET 10, C# 14, Protobuf, actor-owned event-sourced state, CQRS current-state projections, xUnit, FluentAssertions, React 19, TypeScript strict, Umi Max, Ant Design, TanStack Query, Jest, Testing Library, pnpm.

## Global Constraints

- Route identity and model choice are one complete LLMSelection; no normal write may mutate an independent default-model string.
- memberId, workflowId, and publishedServiceId remain distinct; fixtures use m-alpha, wf-alpha, and svc-alpha when all three appear.
- Model identifiers use ordinal equality, are trimmed, contain no control characters, and are at most 256 UTF-8 bytes; one route contains at most 2,048 distinct model IDs.
- An empty model array is never an open identifier set. Do not add OpenIdentifier in this change.
- Reset writes a complete Unspecified selection and never selects Gateway.
- Gateway is selectable only after an explicit ready-and-allowed diagnostic; missing Gateway evidence is unavailable.
- Accepted user-config ACK means accepted-for-dispatch only. UI says update submitted until the exact selection appears in the current-state projection.
- Durable execution requires an exact route and an ExplicitModel contained in Enumerated committed catalog evidence.
- Catalog network destinations come only from configured NyxID authority plus canonical verified identity; request route strings, URLs, labels, and model prefixes never select a destination.
- Query, planner, and invocation paths perform no catalog lookup, replay, projection priming, repair, or external I/O.
- Workflow invocation preflight checks local structural compatibility only; it never enforces source freshness or calls RevalidatePersistedAsync.
- ExpectedExecutionMode is supplied by typed publish, deployment, chat, or fork context; never infer it from scheduleId, runOrigin, actor ID, route position, or another string convention.
- A rejected workflow artifact creates no definition actor, run actor, link, binding repair, or dispatch.
- Do not mutate, rerun, pause, delete, or repair production workflows, runs, schedules, or user configuration.
- Do not add dependencies or a parallel model registry, parser, authorization catalog, workflow validator, or projection pipeline.
- Every task starts RED, implements the minimum coherent change, ends GREEN, and commits independently.
- Run related .NET tests serially because parallel builds can contend on shared generated output.

---

### Task 1: Shared typed LLM selection and structural policy

**Files:**
- Create: src/Aevatar.AI.Abstractions/LLMProviders/llm_selection.proto
- Create: src/Aevatar.AI.Abstractions/LLMProviders/LLMSelectionPolicy.cs
- Modify: src/Aevatar.AI.Abstractions/Aevatar.AI.Abstractions.csproj
- Modify: test/Aevatar.AI.Tests/AIAbstractionsProtoCoverageTests.cs

**Interfaces:**
- Consumes: Google.Protobuf generated-message presence and deterministic serialization.
- Produces: LLMRouteKind, LLMModelSelectionKind, LLMModelCatalogCertainty, LLMModelCatalogDiagnosticKind, LLMModelSelection, LLMModelCatalog, LLMSelection, and LLMSelectionPolicy.ValidateSelection(LLMSelection), ValidateCatalog(LLMModelCatalog), NormalizeCatalog(IEnumerable<string?>, string?, LLMModelCatalogDiagnosticKind), IsExplicitModelEnumerated(LLMSelection, LLMModelCatalog), CompatibilityDefaultModel(LLMSelection), and CompatibilityRoute(LLMSelection).

- [ ] **Step 1: Write failing shared-contract tests**

Add these focused cases to AIAbstractionsProtoCoverageTests:

~~~csharp
[Fact]
public void LLMSelection_ShouldRoundTripExactRouteAndExplicitModel()
{
    var selection = new LLMSelection
    {
        RouteKind = LLMRouteKind.NyxIdUserService,
        RouteValue = "/api/v1/proxy/s/chrono-llm-public",
        NyxIdUserServiceId = "us-alpha",
        ServiceSlugSnapshot = "chrono-llm-public",
        ModelSelection = new LLMModelSelection
        {
            Kind = LLMModelSelectionKind.ExplicitModel,
            ModelId = "gpt-5.5",
        },
    };

    var copy = LLMSelection.Parser.ParseFrom(selection.ToByteArray());

    copy.Should().BeEquivalentTo(selection);
    LLMSelectionPolicy.ValidateSelection(copy);
    LLMSelectionPolicy.CompatibilityDefaultModel(copy).Should().Be("gpt-5.5");
}

[Theory]
[InlineData(" model-a")]
[InlineData("model-a ")]
[InlineData("model\u0001a")]
public void LLMSelection_ShouldRejectNonCanonicalExplicitModel(string modelId)
{
    var selection = GatewaySelection(LLMModelSelectionKind.ExplicitModel, modelId);
    FluentActions.Invoking(() => LLMSelectionPolicy.ValidateSelection(selection))
        .Should().Throw<InvalidOperationException>();
}

[Fact]
public void LLMModelCatalog_ShouldNotTreatEmptyEnumerationAsOpen()
{
    var catalog = LLMSelectionPolicy.NormalizeCatalog(
        [],
        null,
        LLMModelCatalogDiagnosticKind.NotPublished);

    catalog.Certainty.Should().Be(LLMModelCatalogCertainty.NotVerifiable);
    catalog.ModelIds.Should().BeEmpty();
}
~~~

Also add a 257-byte UTF-8 model ID case, a 2,049-model case, exact ordinal de-duplication (model-a and MODEL-A both survive), an invalid default-not-in-list case, and complete Unspecified, Gateway ProviderDefault, and user-service ExplicitModel cases.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

~~~bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter 'FullyQualifiedName~AIAbstractionsProtoCoverageTests.LLM'
~~~

Expected: compilation fails because the shared protobuf types and LLMSelectionPolicy do not exist.

- [ ] **Step 3: Add the shared protobuf contract**

Create llm_selection.proto with these exact field numbers and enum values:

~~~proto
syntax = "proto3";
package aevatar.ai;
option csharp_namespace = "Aevatar.AI.Abstractions";

enum LLMRouteKind {
  LLM_ROUTE_KIND_UNSPECIFIED = 0;
  LLM_ROUTE_KIND_GATEWAY = 1;
  LLM_ROUTE_KIND_NYX_ID_USER_SERVICE = 2;
}

enum LLMModelSelectionKind {
  LLM_MODEL_SELECTION_KIND_UNSPECIFIED = 0;
  LLM_MODEL_SELECTION_KIND_PROVIDER_DEFAULT = 1;
  LLM_MODEL_SELECTION_KIND_EXPLICIT_MODEL = 2;
}

message LLMModelSelection {
  LLMModelSelectionKind kind = 1;
  string model_id = 2;
}

enum LLMModelCatalogCertainty {
  LLM_MODEL_CATALOG_CERTAINTY_UNSPECIFIED = 0;
  LLM_MODEL_CATALOG_CERTAINTY_ENUMERATED = 1;
  LLM_MODEL_CATALOG_CERTAINTY_NOT_VERIFIABLE = 2;
  LLM_MODEL_CATALOG_CERTAINTY_UNAVAILABLE = 3;
}

enum LLMModelCatalogDiagnosticKind {
  LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_UNSPECIFIED = 0;
  LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_NOT_PUBLISHED = 1;
  LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_ROUTE_NOT_READY = 2;
  LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_ACCESS_DENIED = 3;
  LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_OBSERVATION_UNAVAILABLE = 4;
  LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_RESPONSE_INVALID = 5;
  LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_RESPONSE_TOO_LARGE = 6;
  LLM_MODEL_CATALOG_DIAGNOSTIC_KIND_PATTERN_ONLY = 7;
}

message LLMModelCatalog {
  LLMModelCatalogCertainty certainty = 1;
  repeated string model_ids = 2;
  string default_model_id = 3;
  LLMModelCatalogDiagnosticKind diagnostic_kind = 4;
}

message LLMSelection {
  LLMRouteKind route_kind = 1;
  string route_value = 2;
  string nyx_id_user_service_id = 3;
  string service_slug_snapshot = 4;
  LLMModelSelection model_selection = 5;
}
~~~

Register it as a Protobuf item with GrpcServices=None in Aevatar.AI.Abstractions.csproj.

- [ ] **Step 4: Implement the minimum structural policy**

Implement these constants and public methods in LLMSelectionPolicy:

~~~csharp
public static class LLMSelectionPolicy
{
    public const string GatewayRoute = "/api/v1/llm/gateway/v1";
    public const int MaxModelIdUtf8Bytes = 256;
    public const int MaxModelsPerCatalog = 2_048;

    public static void ValidateSelection(LLMSelection selection);
    public static void ValidateCatalog(LLMModelCatalog catalog);
    public static LLMModelCatalog NormalizeCatalog(
        IEnumerable<string?> rawModelIds,
        string? rawDefaultModelId,
        LLMModelCatalogDiagnosticKind emptyDiagnostic);
    public static bool IsExplicitModelEnumerated(
        LLMSelection selection,
        LLMModelCatalog catalog);
    public static string CompatibilityDefaultModel(LLMSelection selection);
    public static string CompatibilityRoute(LLMSelection selection);
}
~~~

Validate route/model coupling exactly: complete Unspecified has empty identity plus an Unspecified model sub-message; Gateway has the canonical Gateway route and no service fields; user-service has exact ID, slug, and /api/v1/proxy/s/{slug}; ProviderDefault and Unspecified have empty model_id; ExplicitModel has one canonical model ID. Normalize enumerated catalogs with ordinal distinct/sort, reject control characters, byte overflow, over 2,048 entries, wildcard or pattern IDs, and a default outside the exact list. Return NotVerifiable with the supplied diagnostic for a valid empty input; do not infer arbitrary-ID support.

- [ ] **Step 5: Run the focused tests and verify GREEN**

Run:

~~~bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter 'FullyQualifiedName~AIAbstractionsProtoCoverageTests.LLM'
~~~

Expected: all shared selection and catalog policy cases pass.

- [ ] **Step 6: Commit the shared contract**

~~~bash
git add src/Aevatar.AI.Abstractions/LLMProviders/llm_selection.proto src/Aevatar.AI.Abstractions/LLMProviders/LLMSelectionPolicy.cs src/Aevatar.AI.Abstractions/Aevatar.AI.Abstractions.csproj test/Aevatar.AI.Tests/AIAbstractionsProtoCoverageTests.cs
git commit -m "Add typed LLM selection contract"
~~~

### Task 2: UserConfig atomic state, command, event, and projection

**Files:**
- Modify: agents/Aevatar.GAgents.UserConfig/Aevatar.GAgents.UserConfig.csproj
- Modify: agents/Aevatar.GAgents.UserConfig/user_config_messages.proto
- Modify: agents/Aevatar.GAgents.UserConfig/UserConfigGAgent.cs
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IUserConfigStore.cs
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmSelectionRoute.cs
- Modify: src/Aevatar.Studio.Projection/Aevatar.Studio.Projection.csproj
- Modify: src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto
- Modify: src/Aevatar.Studio.Projection/CommandServices/ActorDispatchUserConfigCommandService.cs
- Modify: src/Aevatar.Studio.Projection/Projectors/UserConfigCurrentStateProjector.cs
- Modify: src/Aevatar.Studio.Projection/QueryPorts/ProjectionUserConfigQueryPort.cs
- Modify: src/Aevatar.Studio.Projection/QueryPorts/ProjectionScheduledInvocationAuthorityQueryPorts.cs
- Modify: test/Aevatar.Studio.Tests/UserConfigGAgentStateTests.cs
- Modify: test/Aevatar.Studio.Tests/ActorDispatchUserConfigCommandServiceTests.cs
- Modify: test/Aevatar.Studio.Tests/UserConfigCurrentStateProjectorTests.cs
- Modify: test/Aevatar.Studio.Tests/ProjectionUserConfigQueryPortTests.cs
- Modify: test/Aevatar.Studio.Tests/ProjectionScheduledInvocationAuthorityQueryPortTests.cs

**Interfaces:**
- Consumes: shared Aevatar.AI.Abstractions.LLMSelection and LLMSelectionPolicy.
- Produces: UserConfigUpdate(LLMSelection? LlmSelection, non-LLM deltas), UserConfig.LlmSelection as a cloned shared generated message, and UserConfigGAgent commits whose compatibility default_model and preferred_llm_route are derived only when a complete typed selection is supplied.

- [ ] **Step 1: Write failing UserConfig compatibility and atomicity tests**

Replace old split-write expectations with these behaviors:

~~~csharp
[Fact]
public void UpdateCommand_ShouldReserveLegacyDefaultModelMutation()
{
    UpdateUserConfigCommand.Descriptor.FindFieldByName("default_model").Should().BeNull();
    UpdateUserConfigCommand.Descriptor.FindFieldByNumber(1).Should().BeNull();
}

[Fact]
public void BuildUpdatedEvent_WithExplicitSelection_ShouldDeriveBothCompatibilityFields()
{
    var selection = UserServiceSelection("us-alpha", "chrono-llm-public", "gpt-5.5");
    var committed = UserConfigGAgent.BuildUpdatedEvent(
        new UserConfigGAgentState(),
        new UpdateUserConfigCommand { LlmSelection = selection });

    committed.LlmSelection.Should().BeEquivalentTo(selection);
    committed.DefaultModel.Should().Be("gpt-5.5");
    committed.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/chrono-llm-public");
}

[Fact]
public void BuildUpdatedEvent_WithNonLlmDelta_ShouldPreserveLegacyFieldsByteForByte()
{
    var committed = UserConfigGAgent.BuildUpdatedEvent(
        new UserConfigGAgentState
        {
            DefaultModel = " legacy-model ",
            PreferredLlmRoute = " legacy-route ",
        },
        new UpdateUserConfigCommand { GithubUsername = "octocat" });

    committed.DefaultModel.Should().Be(" legacy-model ");
    committed.PreferredLlmRoute.Should().Be(" legacy-route ");
    committed.LlmSelection.Should().BeNull();
}
~~~

Add Reset assertions for a complete Unspecified selection with empty compatibility fields; add a historical four-field selection byte payload that parses as LLMSelection with ModelSelection null and is classified as legacy/incomplete by readers; assert projectors clone all five typed fields; assert the scheduled authority query accepts only ExplicitModel and never merges document.DefaultModel into a typed route.

- [ ] **Step 2: Run focused Studio tests and verify RED**

Run serially:

~~~bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter 'FullyQualifiedName~UserConfigGAgentStateTests|FullyQualifiedName~ActorDispatchUserConfigCommandServiceTests|FullyQualifiedName~UserConfigCurrentStateProjectorTests|FullyQualifiedName~ProjectionUserConfigQueryPortTests|FullyQualifiedName~ProjectionScheduledInvocationAuthorityQueryPortTests'
~~~

Expected: tests fail because UserConfig still owns UserLlmSelection and permits default_model mutation.

- [ ] **Step 3: Replace the UserConfig-local route type without changing wire identity**

In user_config_messages.proto import LLMProviders/llm_selection.proto, delete UserLlmRouteKind and UserLlmSelection, and use aevatar.ai.LLMSelection at state field 8, event field 8, and command field 2. Preserve route field numbers 1-4 and enum values through the shared message. Change UpdateUserConfigCommand to:

~~~proto
message UpdateUserConfigCommand {
  reserved 1;
  reserved "default_model";
  aevatar.ai.LLMSelection llm_selection = 2;
  optional string runtime_mode = 3;
  optional string local_runtime_base_url = 4;
  optional string remote_runtime_base_url = 5;
  optional int32 max_tool_rounds = 6;
  optional string github_username = 7;
}
~~~

Add the Aevatar.AI.Abstractions project reference and AdditionalImportDirs entry to the UserConfig project. Add the same AI protobuf import directory to Studio.Projection because its read-model proto imports the transitive UserConfig contract.

- [ ] **Step 4: Make the actor and Application delta atomic**

Remove UserLlmSelectionKind, UserLlmSelectionValue, and UserConfigUpdate.DefaultModel. Use this shape:

~~~csharp
public sealed record UserConfigUpdate(
    LLMSelection? LlmSelection = null,
    string? RuntimeMode = null,
    string? LocalRuntimeBaseUrl = null,
    string? RemoteRuntimeBaseUrl = null,
    string? GithubUsername = null,
    int? MaxToolRounds = null);
~~~

UserConfigGAgent validates a supplied selection with LLMSelectionPolicy, clones it into the event, and derives both compatibility fields with CompatibilityDefaultModel and CompatibilityRoute. When LlmSelection is absent it clones the current typed selection and preserves both legacy strings unchanged. ActorDispatchUserConfigCommandService packs the shared selection directly and has no code path that assigns command.DefaultModel.

- [ ] **Step 5: Carry the shared message through the current-state projection and queries**

Change UserConfigCurrentStateDocument.llm_selection to aevatar.ai.LLMSelection at field 17 and add the import. Project and query cloned generated messages without rebuilding route enums. UserLlmSelectionRoute.Resolve accepts LLMSelection and returns only canonical Gateway or user-service routes. ProjectionScheduledInvocationOwnerLLMQueryPort reads ModelSelection.ModelId only when kind is ExplicitModel; absent typed selection, partial historical typed selection, and compatibility strings never become durable evidence.

- [ ] **Step 6: Run focused Studio tests and verify GREEN**

Run the command from Step 2 again.

Expected: all atomicity, compatibility, projection, and query tests pass.

- [ ] **Step 7: Commit the UserConfig migration**

~~~bash
git add agents/Aevatar.GAgents.UserConfig src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IUserConfigStore.cs src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmSelectionRoute.cs src/Aevatar.Studio.Projection test/Aevatar.Studio.Tests/UserConfigGAgentStateTests.cs test/Aevatar.Studio.Tests/ActorDispatchUserConfigCommandServiceTests.cs test/Aevatar.Studio.Tests/UserConfigCurrentStateProjectorTests.cs test/Aevatar.Studio.Tests/ProjectionUserConfigQueryPortTests.cs test/Aevatar.Studio.Tests/ProjectionScheduledInvocationAuthorityQueryPortTests.cs
git commit -m "Make UserConfig LLM selection atomic"
~~~

### Task 3: Closed LLM write intents and fresh authoritative saves

**Files:**
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserConfigContracts.cs
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmPreferenceWriteCore.cs
- Modify: src/Aevatar.Studio.Application/Studio/Services/UserConfigService.cs
- Modify: src/Aevatar.Studio.Application/Studio/Services/UserLlmPreferenceWriter.cs
- Modify: src/Aevatar.Studio.Hosting/Controllers/UserConfigController.cs
- Modify: src/Aevatar.Studio.Hosting/Controllers/UserLlmWireContracts.cs
- Modify: src/Aevatar.Studio.Hosting/NyxId/CachedNyxIdLlmCatalogPort.cs
- Modify: src/Aevatar.Studio.Hosting/NyxId/NyxIdLlmCatalogHttpClient.cs
- Modify: src/Aevatar.Studio.Hosting/StudioHostingServiceCollectionExtensions.cs
- Modify: test/Aevatar.Studio.Tests/UserConfigServiceTests.cs
- Modify: test/Aevatar.Studio.Tests/UserConfigControllerSettingsTests.cs
- Modify: test/Aevatar.Studio.Tests/CachedNyxIdLlmCatalogPortTests.cs

**Interfaces:**
- Consumes: IUserConfigCommandService.UpdateAsync(UserConfigResourceKey, UserConfigUpdate, CancellationToken) and current NyxID option identity.
- Produces: abstract UserLlmPreferenceIntent with ResetUserLlmPreferenceIntent, SelectGatewayUserLlmPreferenceIntent(LLMModelSelection), SelectUserServiceUserLlmPreferenceIntent(string UserServiceId, LLMModelSelection), and ActivateUserLlmPresetIntent(string PresetId); IUserLlmCatalogPort.GetFreshServicesAsync(string, CancellationToken); PUT /api/user-config/llm required action union.

- [ ] **Step 1: Write failing closed-union and freshness tests**

Add controller cases that send the exact JSON envelopes below and assert reset/select_gateway/select_user_service/activate_preset map one-to-one, while unknown action, missing matching payload, an extra payload from another action, unknown model kind, and duplicate action or userServiceId properties return 400 before catalog access:

~~~json
{"action":"reset"}
{"action":"select_gateway","gateway":{"model":{"kind":"provider_default"}}}
{"action":"select_user_service","userService":{"userServiceId":"us-alpha","model":{"kind":"explicit_model","modelId":"gpt-5.5"}}}
{"action":"activate_preset","preset":{"presetId":"chrono"}}
~~~

Add these service assertions:

~~~csharp
[Fact]
public async Task SaveAsync_WithGateway_ShouldUseFreshCatalogAndCommitWholeSelection()
{
    await writer.SaveAsync(
        UserConfigResourceKey.ForOwnerScope("scope-alpha"),
        "bearer",
        new SelectGatewayUserLlmPreferenceIntent(
            new LLMModelSelection { Kind = LLMModelSelectionKind.ProviderDefault }),
        default);

    catalog.FreshCalls.Should().Be(1);
    catalog.CachedCalls.Should().Be(0);
    commands.Updates.Single().Update.LlmSelection!.RouteKind.Should().Be(LLMRouteKind.Gateway);
}

[Fact]
public async Task GenericSave_ShouldRejectDefaultModelBeforeDispatch()
{
    var response = await client.PutAsJsonAsync(
        "/api/user-config",
        new { defaultModel = "gpt-5.5" });

    response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    commands.Updates.Should().BeEmpty();
}
~~~

Add Reset-without-bearer, exact inventory ID, duplicate slug with distinct IDs, explicit model absent from the exact option, preset without a model becoming ProviderDefault, and stale-cache-vs-fresh-inner cases.

- [ ] **Step 2: Run focused Studio tests and verify RED**

~~~bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter 'FullyQualifiedName~UserConfigServiceTests|FullyQualifiedName~UserConfigControllerSettingsTests|FullyQualifiedName~CachedNyxIdLlmCatalogPortTests'
~~~

Expected: failures show the nullable multi-purpose command, generic defaultModel mutation, and cached save path still exist.

- [ ] **Step 3: Replace the nullable write command with four sealed intents**

Use these exact declarations:

~~~csharp
public abstract record UserLlmPreferenceIntent;
public sealed record ResetUserLlmPreferenceIntent : UserLlmPreferenceIntent;
public sealed record SelectGatewayUserLlmPreferenceIntent(
    LLMModelSelection ModelSelection) : UserLlmPreferenceIntent;
public sealed record SelectUserServiceUserLlmPreferenceIntent(
    string UserServiceId,
    LLMModelSelection ModelSelection) : UserLlmPreferenceIntent;
public sealed record ActivateUserLlmPresetIntent(
    string PresetId) : UserLlmPreferenceIntent;
~~~

Remove SaveUserConfigCommand.DefaultModel and UserConfigUpdate.DefaultModel. UserConfigService.SaveAsync handles only runtime, GitHub, and max-tool-round deltas and rejects a JSON defaultModel member at the Host boundary.

- [ ] **Step 4: Add the fresh catalog operation and use it for every authoritative selection**

Extend IUserLlmCatalogPort with:

~~~csharp
Task<NyxIdLlmServicesResult> GetFreshServicesAsync(
    string bearerToken,
    CancellationToken ct);
~~~

NyxIdLlmCatalogHttpClient implements it by performing the same live NyxID fetch as GetServicesAsync. CachedNyxIdLlmCatalogPort.GetFreshServicesAsync calls the inner fresh method, updates the bounded presentation cache only after success, and never returns a stale entry. UserLlmPreferenceWriter uses fresh options for Gateway, exact user service, existing preset, and post-provision resolution. Reset writes a complete Unspecified selection without catalog access. Delete BuildSelectedOptionUpdate behavior that preserves or merges a prior model; every branch creates one complete LLMSelection.

- [ ] **Step 5: Implement the required action wire union**

Keep the transport types at the Host boundary:

~~~csharp
public sealed record SaveUserLlmSettingsRequest(
    string Action,
    SelectGatewayRequest? Gateway,
    SelectUserServiceRequest? UserService,
    ActivatePresetRequest? Preset);

public sealed record UserLlmModelSelectionRequest(string Kind, string? ModelId = null);
~~~

ToIntent accepts only reset, select_gateway, select_user_service, and activate_preset; requires exactly the matching payload and rejects every other non-null payload. Model kind accepts only provider_default with no modelId or explicit_model with a canonical modelId. Set JsonSerializerOptions.AllowDuplicateProperties=false in Studio hosting so duplicate action or identity members fail deserialization before Application code.

- [ ] **Step 6: Run focused Studio tests and verify GREEN**

Run the command from Step 2 again.

Expected: closed-union, exact-identity, fresh-save, Reset, and generic-config isolation tests pass.

- [ ] **Step 7: Commit the write boundary**

~~~bash
git add src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserConfigContracts.cs src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmPreferenceWriteCore.cs src/Aevatar.Studio.Application/Studio/Services/UserConfigService.cs src/Aevatar.Studio.Application/Studio/Services/UserLlmPreferenceWriter.cs src/Aevatar.Studio.Hosting test/Aevatar.Studio.Tests/UserConfigServiceTests.cs test/Aevatar.Studio.Tests/UserConfigControllerSettingsTests.cs test/Aevatar.Studio.Tests/CachedNyxIdLlmCatalogPortTests.cs
git commit -m "Close LLM preference write intents"
~~~

### Task 4: Typed catalog certainty, strict model normalization, and Gateway composition

**Files:**
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmCatalogNormalization.cs
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmPreferenceWriteCore.cs
- Modify: src/Aevatar.Studio.Application/Studio/Services/UserLlmPreferenceWriter.cs
- Modify: src/Aevatar.AI.ToolProviders.NyxId/LlmCatalog/NyxIdLlmServiceCatalogParser.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/LlmSelection/NyxIdLlmServiceCatalogClient.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/LlmSelection/StubNyxIdLlmServiceCatalogClient.cs
- Modify: test/Aevatar.Studio.Tests/NyxIdLlmServiceCatalogUserKeyMergeTests.cs
- Modify: test/Aevatar.Studio.Tests/UserConfigServiceTests.cs
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/NyxIdLlmServiceCatalogClientTests.cs

**Interfaces:**
- Consumes: LLMSelectionPolicy.NormalizeCatalog and current route readiness/allowed values.
- Produces: NyxIdLlmService.ModelCatalog and UserLlmOption.ModelCatalog as cloned LLMModelCatalog values; ComposeUserServiceInventory retains explicit Gateway diagnostics plus exact inventory-backed user services.

- [ ] **Step 1: Write failing certainty and composition tests**

Add cases with concrete generated values rather than empty-list inference:

~~~csharp
[Fact]
public void ComposeUserServiceInventory_ShouldRetainGatewayAndExactInventoryIdentity()
{
    var composed = NyxIdLlmServiceCatalogParser.ComposeUserServiceInventory(
        new NyxIdLlmServicesResult(
            [GatewayDiagnostic(EnumeratedCatalog("gateway-model")),
             UserServiceDiagnostic("shared", EnumeratedCatalog("service-model"))],
            null),
        Inventory(("us-alpha", "shared")));

    composed.Services.Should().ContainSingle(x =>
        x.Source == NyxIdLlmProviderSource.GatewayProvider &&
        x.ModelCatalog.Certainty == LLMModelCatalogCertainty.Enumerated);
    composed.Services.Should().ContainSingle(x =>
        x.Identity!.NyxIdUserServiceId == "us-alpha" &&
        x.ModelCatalog.ModelIds.Contains("service-model"));
}

[Fact]
public void ParseServicesResult_WithEmptyModels_ShouldReturnNotVerifiable()
{
    var result = NyxIdLlmServiceCatalogParser.ParseServicesResult(
        """{"services":[{"slug":"chrono","status":"ready","models":[]}]}""");

    result.Services.Single().ModelCatalog.Certainty
        .Should().Be(LLMModelCatalogCertainty.NotVerifiable);
    result.Services.Single().ModelCatalog.DiagnosticKind
        .Should().Be(LLMModelCatalogDiagnosticKind.NotPublished);
}
~~~

Add malformed/control/over-256-byte model, 2,049 models, wildcard, default outside list, case-distinct IDs, authentication-denied Unavailable, and missing-Gateway-does-not-create-a-ready-Gateway cases. Add writer cases proving NotVerifiable permits only ProviderDefault interactively and Unavailable permits no new save.

- [ ] **Step 2: Run parser, channel-client, and writer tests and verify RED**

Run serially:

~~~bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter 'FullyQualifiedName~NyxIdLlmServiceCatalogUserKeyMergeTests|FullyQualifiedName~UserConfigServiceTests'
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --filter 'FullyQualifiedName~NyxIdLlmServiceCatalogClientTests'
~~~

Expected: current contracts still use DefaultModel plus Models and composition replaces Gateway diagnostics.

- [ ] **Step 3: Replace ambiguous model fields with LLMModelCatalog**

Use these record members and clone at every boundary:

~~~csharp
public sealed record NyxIdLlmService(
    string? CatalogEntryId,
    string ServiceSlug,
    string DisplayName,
    string RouteValue,
    LLMModelCatalog ModelCatalog,
    string Status,
    string Source,
    bool Allowed,
    string? Description,
    UserLlmServiceIdentity? Identity = null);

public sealed record UserLlmOption(
    string ServiceSlug,
    string DisplayName,
    string RouteValue,
    LLMModelCatalog ModelCatalog,
    string Status,
    string Source,
    bool Allowed,
    string? Description,
    UserLlmServiceIdentity? Identity = null);
~~~

Delete code that treats AvailableModels.Count == 0 as a capability decision. Map successful empty/missing models to NotVerifiable/NotPublished; invalid/default-mismatch to NotVerifiable/ResponseInvalid; over-limit to NotVerifiable/ResponseTooLarge; wildcard or pattern values to NotVerifiable/PatternOnly; denied or non-ready routes to Unavailable with a typed diagnostic.

- [ ] **Step 4: Preserve Gateway diagnostics during inventory composition**

Build the composed services as explicit Gateway diagnostics followed by exact eligible inventory services:

~~~csharp
var gateway = diagnostics.Services
    .Where(static service =>
        UserLlmCatalogNormalization.NormalizeSource(service.Source) ==
        UserLlmRouteSourceValue.GatewayProvider)
    .Select(static service => service with { ModelCatalog = service.ModelCatalog.Clone() });
var inventoryServices = inventory.Services
    .Where(IsEligible)
    .OrderBy(static service => service.Id, StringComparer.Ordinal)
    .Select(service => ComposeUserService(diagnostics.Services, service));
return diagnostics with { Services = gateway.Concat(inventoryServices).ToArray() };
~~~

Do not synthesize a ready Gateway when no Gateway diagnostic exists. User-service identity still comes only from the strict inventory ID; matching diagnostics by slug supplies readiness and catalog data but never authority.

- [ ] **Step 5: Enforce the interaction matrix in the shared writer**

For a ready route, ProviderDefault is valid when catalog certainty is Enumerated or NotVerifiable. ExplicitModel is valid only when certainty is Enumerated and the exact ordinal ID exists. Unavailable, unknown certainty, an arbitrary identifier, a prefix match, a model from another route, display labels, and slugs are rejected before dispatch.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run both commands from Step 2 again.

Expected: certainty, strict normalization, Gateway retention, exact identity, and write-matrix tests pass.

- [ ] **Step 7: Commit catalog certainty**

~~~bash
git add src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmCatalogNormalization.cs src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmPreferenceWriteCore.cs src/Aevatar.Studio.Application/Studio/Services/UserLlmPreferenceWriter.cs src/Aevatar.AI.ToolProviders.NyxId/LlmCatalog/NyxIdLlmServiceCatalogParser.cs agents/Aevatar.GAgents.NyxidChat/LlmSelection test/Aevatar.Studio.Tests/NyxIdLlmServiceCatalogUserKeyMergeTests.cs test/Aevatar.Studio.Tests/UserConfigServiceTests.cs test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/NyxIdLlmServiceCatalogClientTests.cs
git commit -m "Type LLM model catalog certainty"
~~~

### Task 5: Honest settings status and accepted-ACK semantics

**Files:**
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmCatalogNormalization.cs
- Modify: src/Aevatar.Studio.Application/Studio/Services/UserLlmPreferenceService.cs
- Modify: src/Aevatar.Studio.Application/Studio/Services/UserLlmSettingsViewBuilder.cs
- Modify: src/Aevatar.Studio.Hosting/Controllers/UserLlmWireContracts.cs
- Modify: test/Aevatar.Studio.Tests/UserLlmSettingsViewBuilderTests.cs
- Modify: test/Aevatar.Studio.Tests/UserConfigControllerSettingsTests.cs

**Interfaces:**
- Consumes: UserConfig typed selection plus legacy compatibility fields and the presentation catalog result.
- Produces: UserLlmSelectionStatus (SystemDefault, Ready, VerificationUnavailable, NeedsRepair, LegacyRepairRequired), UserLlmRemediationKind (None, RetryCatalog, ConnectProvider, ChooseReplacement, Reselect), and a settings contract with SavedSelection, SelectionStatus, CatalogDiagnostic, and Remediation but no EffectiveRoute, EffectiveRouteLabel, RouteFallbackActive, or FallbackReason.

- [ ] **Step 1: Write failing settings-status tests**

Add these high-value cases:

~~~csharp
[Fact]
public void BuildAvailable_WithSavedUnavailableService_ShouldPreserveSelectionAndRequireRepair()
{
    var saved = UserServiceSelection("us-alpha", "chrono-llm-public", "gpt-5.5");
    var view = builder.BuildAvailable(
        Services(UserService("us-alpha", "chrono-llm-public", UnavailableCatalog())),
        Config(saved));

    view.SavedSelection.Should().BeEquivalentTo(saved);
    view.SelectionStatus.Should().Be(UserLlmSelectionStatus.NeedsRepair);
    view.Remediation.Should().Be(UserLlmRemediationKind.ChooseReplacement);
}

[Fact]
public void BuildUnavailable_WithValidSavedSelection_ShouldReportVerificationUnavailable()
{
    var view = builder.BuildVerificationUnavailable(Config(GatewayProviderDefault()));

    view.SelectionStatus.Should().Be(UserLlmSelectionStatus.VerificationUnavailable);
    view.Remediation.Should().Be(UserLlmRemediationKind.RetryCatalog);
}
~~~

Add successful negative observation vs transport failure, genuine empty state vs Reset Unspecified, historical typed route with missing model sub-message, compatibility-only legacy strings, explicit Gateway missing from catalog, and JSON absence of all four deleted fallback properties. Assert receipt ackStage remains accepted and no response calls it committed, saved, or active.

- [ ] **Step 2: Run settings tests and verify RED**

~~~bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter 'FullyQualifiedName~UserLlmSettingsViewBuilderTests|FullyQualifiedName~UserConfigControllerSettingsTests'
~~~

Expected: tests fail because the builder computes an effective fallback route and conflates transport failure with route failure.

- [ ] **Step 3: Replace fallback fields with one typed selection status**

Use this Application shape:

~~~csharp
public sealed record UserLlmSettingsView(
    LLMSelection? SavedSelection,
    string SavedRouteLabel,
    UserLlmSelectionStatus SelectionStatus,
    LLMModelCatalogDiagnosticKind CatalogDiagnostic,
    UserLlmRemediationKind Remediation,
    IReadOnlyList<UserLlmRouteOption> RouteOptions,
    IReadOnlyList<UserLlmModelGroup> ModelGroupsByRoute,
    string CatalogStatus,
    UserLlmSettingsCapabilities Capabilities,
    UserLlmSetupHint? SetupHint);
~~~

Classify as follows: no typed selection and no legacy fields is SystemDefault; complete Unspecified is SystemDefault; missing model sub-message or compatibility-only route/model is LegacyRepairRequired; a complete saved target proven present is Ready; a successful negative catalog observation is NeedsRepair; catalog fetch failure is VerificationUnavailable. Always clone and retain the saved selection. Missing Gateway evidence is a disabled unavailable route option, never a ready fallback.

- [ ] **Step 4: Map stable wire values and safe remediation**

Map statuses to system_default, ready, verification_unavailable, needs_repair, and legacy_repair_required; remediation to none, retry_catalog, connect_provider, choose_replacement, and reselect. Route options expose modelCatalog certainty, normalized IDs, default, and diagnostic. Remove all effective-route and fallback properties from Application and Host response types.

- [ ] **Step 5: Keep the ACK contract honest**

Keep HTTP 202 and UserConfigSaveReceiptResponse unchanged structurally. Ensure server copy and XML comments describe accepted-for-dispatch only. Do not add a synchronous projection read, wait loop, committed boolean, query priming call, or actor-state read.

- [ ] **Step 6: Run settings tests and verify GREEN**

Run the command from Step 2 again.

Expected: typed status, retained target, transport/negative distinction, no-fallback, and ACK tests pass.

- [ ] **Step 7: Commit the settings contract**

~~~bash
git add src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmCatalogNormalization.cs src/Aevatar.Studio.Application/Studio/Services/UserLlmPreferenceService.cs src/Aevatar.Studio.Application/Studio/Services/UserLlmSettingsViewBuilder.cs src/Aevatar.Studio.Hosting/Controllers/UserLlmWireContracts.cs test/Aevatar.Studio.Tests/UserLlmSettingsViewBuilderTests.cs test/Aevatar.Studio.Tests/UserConfigControllerSettingsTests.cs
git commit -m "Expose honest LLM selection status"
~~~

### Task 6: Typed runtime preference ports and fail-closed application

**Files:**
- Modify: src/Aevatar.AI.Abstractions/LLMProviders/IOwnerLlmConfigSource.cs
- Modify: src/Aevatar.AI.Abstractions/LLMProviders/INyxIdUserLlmPreferencesStore.cs
- Modify: src/Aevatar.AI.Abstractions/LLMProviders/LLMSelectionPolicy.cs
- Create: src/Aevatar.AI.Abstractions/LLMProviders/LLMSelectionRepairRequiredException.cs
- Modify: src/Aevatar.AI.Core/LLMProviders/OwnerLlmConfigApplier.cs
- Modify: src/Aevatar.Mainnet.Host.Api/Hosting/StudioUserConfigOwnerLlmConfigSource.cs
- Modify: src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedNyxIdUserLlmPreferencesStore.cs
- Modify: src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCommandFacade.cs
- Modify: src/platform/Aevatar.GAgentService.Application/Responses/MessagesCommandFacade.cs
- Modify: src/platform/Aevatar.GAgentService.Application/Responses/ChatCompletionsCommandFacade.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/AgentRunReplyGenerationExecutor.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.cs
- Modify: agents/Aevatar.GAgents.StreamingProxy/StreamingProxyNyxParticipantCoordinator.cs
- Create: test/Aevatar.AI.Tests/OwnerLlmConfigApplierTests.cs
- Modify: test/Aevatar.Capabilities.Tests/StudioUserConfigOwnerLlmConfigSourceTests.cs
- Modify: test/Aevatar.Studio.Tests/ActorBackedNyxIdUserLlmPreferencesStoreTests.cs
- Modify: test/Aevatar.GAgentService.Tests/Application/ResponsesCommandFacadeTests.cs
- Modify: test/Aevatar.GAgentService.Tests/Application/MessagesCommandFacadeTests.cs
- Modify: test/Aevatar.GAgentService.Tests/Application/ChatCompletionsCommandFacadeTests.cs
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/ConversationReplyGeneratorTests.cs
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/AgentRunReplyGenerationExecutorSenderTokenTests.cs

**Interfaces:**
- Consumes: one persisted LLMSelection plus legacy compatibility-presence evidence.
- Produces: LLMSelectionPersistenceStatus (SystemDefault, Ready, LegacyRepairRequired), OwnerLlmConfig(LLMSelection Selection, LLMSelectionPersistenceStatus Status, int MaxToolRounds), NyxIdUserLlmPreferences with the same fields, and LLMSelectionPolicy.ApplyTo(LLMControlContext, LLMSelection).

- [ ] **Step 1: Write failing typed-runtime tests**

Add the following focused cases:

~~~csharp
[Fact]
public async Task ApplyAsync_WithExplicitModel_ShouldApplyExactRouteAndModel()
{
    var source = new StubSource(new OwnerLlmConfig(
        UserServiceSelection("us-alpha", "chrono-llm-public", "gpt-5.5"),
        LLMSelectionPersistenceStatus.Ready,
        7));

    var applied = await OwnerLlmConfigApplier.ApplyAsync(
        LLMControlContext.Empty, "scope-alpha", source, logger,
        "test", "actor-alpha", default);

    applied.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/chrono-llm-public");
    applied.ModelOverride.Should().Be("gpt-5.5");
    applied.MaxToolRoundsOverride.Should().Be(7);
}

[Fact]
public async Task ApplyAsync_WithLegacySelection_ShouldStopBeforeLlmCall()
{
    var source = new StubSource(new OwnerLlmConfig(
        new LLMSelection(),
        LLMSelectionPersistenceStatus.LegacyRepairRequired,
        0));

    var act = () => OwnerLlmConfigApplier.ApplyAsync(
        LLMControlContext.Empty, "scope-alpha", source, logger,
        "test", "actor-alpha", default);

    await act.Should().ThrowAsync<LLMSelectionRepairRequiredException>()
        .Where(ex => ex.Code == "llm_selection_repair_required");
}
~~~

Add ProviderDefault-applies-route-only, genuine SystemDefault-preserves configured request defaults, structurally valid saved targets are applied without a live catalog check, source transport failure preserves current control, and compatibility-only or four-field historical selection returns LegacyRepairRequired. In facade tests assert no caller reads OwnerLlmConfig.DefaultModel or PreferredLlmRoute because those properties no longer exist.

- [ ] **Step 2: Run focused runtime tests and verify RED**

Run serially:

~~~bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter 'FullyQualifiedName~OwnerLlmConfigApplierTests'
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~StudioUserConfigOwnerLlmConfigSourceTests'
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter 'FullyQualifiedName~ActorBackedNyxIdUserLlmPreferencesStoreTests'
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter 'FullyQualifiedName~ResponsesCommandFacadeTests|FullyQualifiedName~MessagesCommandFacadeTests|FullyQualifiedName~ChatCompletionsCommandFacadeTests'
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --filter 'FullyQualifiedName~ConversationReplyGeneratorTests|FullyQualifiedName~AgentRunReplyGenerationExecutorSenderTokenTests'
~~~

Expected: compilation or assertions fail because runtime ports still expose independent strings and legacy values silently fall through.

- [ ] **Step 3: Replace both runtime port records with a typed snapshot**

Use these exact types in AI Abstractions:

~~~csharp
public enum LLMSelectionPersistenceStatus
{
    Unspecified = 0,
    SystemDefault = 1,
    Ready = 2,
    LegacyRepairRequired = 3,
}

public sealed record OwnerLlmConfig(
    LLMSelection Selection,
    LLMSelectionPersistenceStatus Status,
    int MaxToolRounds)
{
    public static OwnerLlmConfig Empty { get; } = new(
        LLMSelectionPolicy.SystemDefaultSelection(),
        LLMSelectionPersistenceStatus.SystemDefault,
        0);
}

public sealed record NyxIdUserLlmPreferences(
    LLMSelection Selection,
    LLMSelectionPersistenceStatus Status,
    int MaxToolRounds = 0);
~~~

Add LLMSelectionPolicy.ClassifyPersisted(LLMSelection? selection, string? legacyRoute, string? legacyModel), SystemDefaultSelection(), and ApplyTo(LLMControlContext current, LLMSelection selection). Classify absent selection with both legacy strings empty as SystemDefault; any legacy value without a complete typed selection, a missing model sub-message, or structurally invalid typed selection as LegacyRepairRequired; complete Unspecified as SystemDefault; complete Gateway or user service as Ready.

- [ ] **Step 4: Fail closed only for semantic repair, not transport failure**

Create one typed exception:

~~~csharp
public sealed class LLMSelectionRepairRequiredException : InvalidOperationException
{
    public const string StableCode = "llm_selection_repair_required";
    public string Code => StableCode;
    public string Remediation => "reselect_llm";

    public LLMSelectionRepairRequiredException()
        : base("Select an LLM service and model again before continuing.") { }
}
~~~

OwnerLlmConfigApplier still catches source lookup failures and preserves the incoming control. After a successful read it throws this exception for LegacyRepairRequired, no-ops for SystemDefault, and uses LLMSelectionPolicy.ApplyTo for Ready. ProviderDefault sets route only; ExplicitModel sets route and exact model; neither performs a catalog query or switches providers.

- [ ] **Step 5: Migrate every direct consumer in the same commit**

Mainnet and Studio adapters clone config.LlmSelection and classify it with the compatibility strings. The three ingress facades derive a local LLMControlContext with LLMSelectionPolicy.ApplyTo and use its ModelOverride and NyxIdRoutePreference; they do not reconstruct from strings. NyxIdChat sender/owner layering applies one complete selection at a time and stops with the typed reselect error before calling ChatStreamAsync when a successfully read snapshot requires repair. Preserve explicit caller overrides according to the existing ingress precedence tests.

- [ ] **Step 6: Run focused runtime tests and verify GREEN**

Run all five commands from Step 2 again.

Expected: typed snapshots, fail-closed legacy handling, transport fallback, provider-default, explicit-model, and precedence cases pass.

- [ ] **Step 7: Commit typed runtime consumption**

~~~bash
git add src/Aevatar.AI.Abstractions/LLMProviders src/Aevatar.AI.Core/LLMProviders/OwnerLlmConfigApplier.cs src/Aevatar.Mainnet.Host.Api/Hosting/StudioUserConfigOwnerLlmConfigSource.cs src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedNyxIdUserLlmPreferencesStore.cs src/platform/Aevatar.GAgentService.Application/Responses agents/Aevatar.GAgents.NyxidChat agents/Aevatar.GAgents.StreamingProxy test/Aevatar.AI.Tests/OwnerLlmConfigApplierTests.cs test/Aevatar.Capabilities.Tests/StudioUserConfigOwnerLlmConfigSourceTests.cs test/Aevatar.Studio.Tests/ActorBackedNyxIdUserLlmPreferencesStoreTests.cs test/Aevatar.GAgentService.Tests/Application/ResponsesCommandFacadeTests.cs test/Aevatar.GAgentService.Tests/Application/MessagesCommandFacadeTests.cs test/Aevatar.GAgentService.Tests/Application/ChatCompletionsCommandFacadeTests.cs test/Aevatar.GAgents.ChannelRuntime.Tests/ConversationReplyGeneratorTests.cs test/Aevatar.GAgents.ChannelRuntime.Tests/AgentRunReplyGenerationExecutorSenderTokenTests.cs
git commit -m "Apply typed LLM selections at runtime"
~~~

### Task 7: Atomic Channel slash and card selection

**Files:**
- Modify: src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs
- Modify: src/Aevatar.Studio.Application/Studio/Services/ChannelUserLlmPreferencePort.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/LlmSelection/UserLlmSelectionContracts.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/LlmSelection/DefaultUserLlmSelectionService.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/LlmSelection/DefaultUserLlmOptionsService.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/Slash/ModelChannelSlashCommandHandler.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs
- Modify: agents/Aevatar.GAgents.NyxidChat/LlmSelection/TextUserLlmOptionsRenderer.cs
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ModelSlashCommandHandlerTests.cs
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/DefaultUserLlmOptionsServiceTests.cs
- Modify: test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelConversationTurnRunnerTests.cs

**Interfaces:**
- Consumes: IChannelUserLlmPreferencePort.SaveAsync(bindingId, bearerToken, UserLlmPreferenceIntent, ct).
- Produces: IUserLlmSelectionService.SetByServiceAsync(context, userServiceId, LLMModelSelection, ct), ApplyPresetAsync, and ResetAsync; no SetModelOverrideAsync or SaveSelectedOptionAsync surface.

- [ ] **Step 1: Write failing slash and card tests**

Add these observable behaviors:

~~~csharp
[Fact]
public async Task Use_ModelOnly_ShouldReturnUsageAndNeverWrite()
{
    var reply = await handler.HandleAsync(Context(), "use gpt-5.5", default);

    reply.Text.Should().Contain("/model use <service> [model]");
    selection.Commands.Should().BeEmpty();
}

[Fact]
public async Task Use_ExactServiceAndModel_ShouldWriteOneCompleteIntent()
{
    await handler.HandleAsync(Context(), "use 2 gpt-5.5", default);

    var command = selection.Commands.Should().ContainSingle().Which;
    command.UserServiceId.Should().Be("us-beta");
    command.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.ExplicitModel);
    command.ModelSelection.ModelId.Should().Be("gpt-5.5");
}
~~~

Add service-without-model becomes ProviderDefault rather than the first/default model, legacy model-only card action is rejected with a refresh/list hint and zero writes, select-service and preset cards use the shared intent writer, Reset writes Unspecified, and user-facing confirmation says submitted rather than saved or active.

- [ ] **Step 2: Run focused Channel tests and verify RED**

~~~bash
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --filter 'FullyQualifiedName~ModelSlashCommandHandlerTests|FullyQualifiedName~DefaultUserLlmOptionsServiceTests|FullyQualifiedName~ChannelConversationTurnRunnerTests'
~~~

Expected: model-only currently calls SetModelOverrideAsync, service omission promotes a default model, and direct SaveSelectedOptionAsync still exists.

- [ ] **Step 3: Delete both split-write interfaces**

Remove IChannelUserLlmPreferencePort.SaveSelectedOptionAsync and IUserLlmSelectionService.SetModelOverrideAsync. DefaultUserLlmSelectionService must issue the short-lived bearer and call only:

~~~csharp
await preferencePort.SaveAsync(
    RequireBindingId(context),
    bearerToken,
    new SelectUserServiceUserLlmPreferenceIntent(
        userServiceId.Trim(),
        modelSelection.Clone()),
    ct);
~~~

Preset and Reset use ActivateUserLlmPresetIntent and ResetUserLlmPreferenceIntent. No Channel service accepts a pre-resolved UserLlmOption as write authority.

- [ ] **Step 4: Make slash and card inputs express a whole selection**

Parse only /model use <service-number|service-name> [model-name]. Resolve one exact inventory option; omitted model creates ProviderDefault and a supplied model creates ExplicitModel. When input does not resolve a service, render list/usage and do not attempt a write. Accept only select_service, apply_preset, and list_page card actions; explicitly reject a legacy select_model or unknown action without reading UserConfig.

- [ ] **Step 5: Render honest post-dispatch state**

Return text equivalent to “selection update submitted; it becomes active after the updated setting is observed.” Do not fetch options immediately and call that result active. Existing list reads may show Current only when the projection contains the matching complete selection.

- [ ] **Step 6: Run focused Channel tests and verify GREEN**

Run the command from Step 2 again.

Expected: service/model atomics, ProviderDefault omission, Reset, card rejection, and submitted-copy tests pass.

- [ ] **Step 7: Commit Channel atomicity**

~~~bash
git add src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs src/Aevatar.Studio.Application/Studio/Services/ChannelUserLlmPreferencePort.cs agents/Aevatar.GAgents.NyxidChat test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ModelSlashCommandHandlerTests.cs test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/DefaultUserLlmOptionsServiceTests.cs test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelConversationTurnRunnerTests.cs
git commit -m "Make Channel LLM writes atomic"
~~~

### Task 8: Console typed contract and no-fallback UX

**Files:**
- Modify: apps/aevatar-console-web/src/shared/studio/models.ts
- Modify: apps/aevatar-console-web/src/shared/studio/api.ts
- Modify: apps/aevatar-console-web/src/shared/studio/api.test.ts
- Modify: apps/aevatar-console-web/src/pages/settings/userLlmSelection.ts
- Modify: apps/aevatar-console-web/src/pages/settings/userLlmSelection.test.ts
- Modify: apps/aevatar-console-web/src/pages/settings/userLlmSaveObservation.ts
- Modify: apps/aevatar-console-web/src/pages/settings/userLlmSaveObservation.test.ts
- Modify: apps/aevatar-console-web/src/pages/settings/index.tsx
- Modify: apps/aevatar-console-web/src/pages/settings/index.test.tsx
- Modify: apps/aevatar-console-web/src/pages/chat/chatConversationConfig.ts
- Modify: apps/aevatar-console-web/src/pages/chat/chatConversationConfig.test.ts
- Modify: apps/aevatar-console-web/src/pages/studio/index.tsx
- Modify: apps/aevatar-console-web/src/pages/studio/index.test.tsx
- Modify: apps/aevatar-console-web/src/locales/projectMessages.en-US.ts
- Modify: apps/aevatar-console-web/src/locales/projectMessages.zh-CN.ts

**Interfaces:**
- Consumes: the Task 5 settings response and Task 3 required-action write union.
- Produces: StudioLlmSelection, StudioLlmModelSelection, StudioLlmModelCatalog, StudioUserLlmSelectionStatus, and StudioSaveUserLlmIntent; API decoder rejects unknown enum values rather than treating them as Gateway or arbitrary strings.

- [ ] **Step 1: Write failing adapter and rendered-behavior tests**

In api.test.ts assert a complete response decodes these values and contains no effectiveRoute/fallback properties:

~~~typescript
expect(await studioApi.getUserLlmSettings()).toMatchObject({
  savedSelection: {
    routeKind: "nyx_id_user_service",
    routeValue: "/api/v1/proxy/s/chrono-llm-public",
    nyxIdUserServiceId: "us-alpha",
    modelSelection: { kind: "explicit_model", modelId: "gpt-5.5" },
  },
  selectionStatus: "needs_repair",
  remediation: "choose_replacement",
});
~~~

Add request-shape tests for all four actions. In settings/index.test.tsx render the real page and assert: System default is not labelled Gateway; Needs repair retains the exact unavailable service/model and says requests will not switch providers; Verification unavailable offers Retry without declaring the route broken; Provider default is a selectable explicit choice only on ready Enumerated/NotVerifiable routes; unavailable options remain visible/disabled; accepted ACK shows Update submitted plus command ID; Active appears only after a matching GET. Add chat and Studio consumer tests proving they no longer read effectiveRoute and do not silently substitute Gateway.

- [ ] **Step 2: Run focused frontend tests and verify RED**

~~~bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/shared/studio/api.test.ts src/pages/settings/userLlmSelection.test.ts src/pages/settings/userLlmSaveObservation.test.ts src/pages/settings/index.test.tsx src/pages/chat/chatConversationConfig.test.ts src/pages/studio/index.test.tsx
~~~

Expected: adapter and page tests fail because the frontend contract still depends on effectiveRoute, routeFallbackActive, a free-form model, and the old request shape.

- [ ] **Step 3: Replace the frontend transport model and decoder**

Use closed unions:

~~~typescript
export type StudioLlmModelSelection =
  | { readonly kind: "unspecified" }
  | { readonly kind: "provider_default" }
  | { readonly kind: "explicit_model"; readonly modelId: string };

export type StudioLlmSelection =
  | { readonly routeKind: "unspecified"; readonly modelSelection: { readonly kind: "unspecified" } }
  | { readonly routeKind: "gateway"; readonly routeValue: string; readonly modelSelection: Exclude<StudioLlmModelSelection, { kind: "unspecified" }> }
  | { readonly routeKind: "nyx_id_user_service"; readonly routeValue: string; readonly nyxIdUserServiceId: string; readonly serviceSlugSnapshot: string; readonly modelSelection: Exclude<StudioLlmModelSelection, { kind: "unspecified" }> };

export type StudioSaveUserLlmIntent =
  | { readonly action: "reset" }
  | { readonly action: "select_gateway"; readonly gateway: { readonly model: StudioLlmModelSelection } }
  | { readonly action: "select_user_service"; readonly userService: { readonly userServiceId: string; readonly model: StudioLlmModelSelection } }
  | { readonly action: "activate_preset"; readonly preset: { readonly presetId: string } };
~~~

Decode route kind, model kind, catalog certainty, diagnostic, status, and remediation through exhaustive switches. Reject unknown values at the API adapter boundary. Remove EffectiveRoute, EffectiveRouteLabel, RouteFallbackActive, and FallbackReason from all frontend models and consumers.

- [ ] **Step 4: Make the settings form preserve exact saved and pending selections**

Store the draft as one complete StudioLlmSelection. Build model choices from the selected route’s modelCatalog: Provider default plus exact enumerated IDs; do not render free-form model input. Reset produces action reset. A save submits the route identity already selected in the form. The pending observation compares the full normalized selection, not separate route/model strings:

~~~typescript
isObserved: (settings) =>
  userLlmSelectionsEqual(settings.savedSelection, target.submittedSelection),
~~~

Keep the submitted form value during polling. Display “Update submitted · {commandId}” for accepted and accepted-unobserved phases, and “Active” only when the exact selection is observed.

- [ ] **Step 5: Render status-specific remediation without provider fallback**

SystemDefault displays System default. NeedsRepair retains and highlights the exact saved selection, disables it as a new choice, and offers Choose replacement or Reset. VerificationUnavailable retains the selection and offers Retry. LegacyRepairRequired offers Reselect. Remove copy promising automatic fallback and update both locale catalogs. Chat and Studio derive any display route only from savedSelection; incomplete or repair-required selections surface the typed action instead of selecting Gateway.

- [ ] **Step 6: Run focused tests and static checks and verify GREEN**

~~~bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/shared/studio/api.test.ts src/pages/settings/userLlmSelection.test.ts src/pages/settings/userLlmSaveObservation.test.ts src/pages/settings/index.test.tsx src/pages/chat/chatConversationConfig.test.ts src/pages/studio/index.test.tsx
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web exec biome lint src/shared/studio/models.ts src/shared/studio/api.ts src/shared/studio/api.test.ts src/pages/settings/userLlmSelection.ts src/pages/settings/userLlmSelection.test.ts src/pages/settings/userLlmSaveObservation.ts src/pages/settings/userLlmSaveObservation.test.ts src/pages/settings/index.tsx src/pages/settings/index.test.tsx src/pages/chat/chatConversationConfig.ts src/pages/chat/chatConversationConfig.test.ts src/pages/studio/index.tsx src/pages/studio/index.test.tsx src/locales/projectMessages.en-US.ts src/locales/projectMessages.zh-CN.ts
~~~

Expected: focused Jest, TypeScript, and affected-file Biome checks pass. Do not run the complete frontend suite.

- [ ] **Step 7: Commit the Console contract**

~~~bash
git add apps/aevatar-console-web/src/shared/studio apps/aevatar-console-web/src/pages/settings apps/aevatar-console-web/src/pages/chat/chatConversationConfig.ts apps/aevatar-console-web/src/pages/chat/chatConversationConfig.test.ts apps/aevatar-console-web/src/pages/studio/index.tsx apps/aevatar-console-web/src/pages/studio/index.test.tsx apps/aevatar-console-web/src/locales/projectMessages.en-US.ts apps/aevatar-console-web/src/locales/projectMessages.zh-CN.ts
git commit -m "Show exact LLM selection status"
~~~

### Task 9: Owner authorization catalog LLM evidence

**Files:**
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Aevatar.GAgentService.Abstractions.csproj
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Protos/scheduled_invocation_authorization_evidence.proto
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Protos/scheduled_invocation_authorization_plan.proto
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs
- Modify: src/platform/Aevatar.GAgentService.Core/Aevatar.GAgentService.Core.csproj
- Modify: src/platform/Aevatar.GAgentService.Core/Schedules/Authorization/nyxid_authorization_catalog_state.proto
- Modify: src/platform/Aevatar.GAgentService.Core/Schedules/Authorization/NyxIdAuthorizationCatalogGAgent.cs
- Modify: src/platform/Aevatar.GAgentService.Infrastructure/Schedules/Authorization/NyxIdAuthorizationCatalogCommandPort.cs
- Modify: src/platform/Aevatar.GAgentService.Projection/Aevatar.GAgentService.Projection.csproj
- Modify: src/platform/Aevatar.GAgentService.Projection/Authorization/nyxid_authorization_catalog_read_model.proto
- Modify: src/platform/Aevatar.GAgentService.Projection/Projectors/NyxIdAuthorizationCatalogCurrentStateProjector.cs
- Modify: src/platform/Aevatar.GAgentService.Projection/Queries/ProjectionNyxIdAuthorizationCatalogQueryPort.cs
- Modify: test/Aevatar.GAgentService.Tests/Authorization/NyxIdAuthorizationCatalogLifecycleTests.cs
- Modify: test/Aevatar.GAgentService.Tests/Authorization/ScheduledInvocationAuthorizationPlannerTests.cs
- Modify: test/Aevatar.GAgentService.Tests/Projection/NyxIdAuthorizationCatalogRefreshObservationInfrastructureTests.cs

**Interfaces:**
- Consumes: shared LLMRouteKind and LLMModelCatalog.
- Produces: NyxIdAuthorizationLLMTargetEvidence; optional NyxIdAuthorizationServiceEvidence.llm_target field 14; NyxIdAuthorizationCatalogContent.gateway_llm_target field 3; catalog snapshot/observation/state/read-model Gateway evidence; content digest covers both Gateway and per-service LLM evidence.

- [ ] **Step 1: Write failing catalog evidence and digest tests**

Add service and Gateway round-trip tests plus digest sensitivity:

~~~csharp
[Fact]
public void ComputeContentDigest_ShouldBindGatewayAndExactServiceModelEvidence()
{
    var services = new[] { ServiceEvidence("us-alpha", ServiceTarget("us-alpha", "gpt-5.5")) };
    var gateway = GatewayTarget("gateway-model-a");
    var first = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(Owner(), services, gateway);
    var second = NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(
        Owner(), services, GatewayTarget("gateway-model-b"));

    first.Should().NotBe(second);
}
~~~

Add actor rejection for invalid certainty/model invariants, ordinal ordering and duplicate models, user-service evidence whose exact ID/slug/route disagree with the parent service, and Gateway evidence carrying a service ID. Add projector/query tests proving evidence is cloned and source state_version is preserved. Add a legacy snapshot with absent LLM fields that still validates non-LLM grants.

- [ ] **Step 2: Run focused catalog tests and verify RED**

~~~bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter 'FullyQualifiedName~NyxIdAuthorizationCatalogLifecycleTests|FullyQualifiedName~ScheduledInvocationAuthorizationPlannerTests|FullyQualifiedName~NyxIdAuthorizationCatalogRefreshObservationInfrastructureTests'
~~~

Expected: shared LLM evidence fields, digest overload, state, and projection fields do not exist.

- [ ] **Step 3: Add the evidence protobuf and use the shared route enum**

Import LLMProviders/llm_selection.proto and define:

~~~proto
message NyxIdAuthorizationLLMTargetEvidence {
  aevatar.ai.LLMRouteKind route_kind = 1;
  string route_value = 2;
  string nyx_id_user_service_id = 3;
  string service_slug_snapshot = 4;
  aevatar.ai.LLMModelCatalog model_catalog = 5;
  google.protobuf.Timestamp observed_at = 6;
  google.protobuf.Timestamp fresh_until = 7;
  google.protobuf.Timestamp evaluated_at = 8;
  string authority_contract_version = 9;
  string authority_policy_version = 10;
}
~~~

Add llm_target=14 to NyxIdAuthorizationServiceEvidence and gateway_llm_target=3 to NyxIdAuthorizationCatalogContent. Change ScheduledInvocationOwnerLLMSelection.route_kind field 1 to aevatar.ai.LLMRouteKind and delete the local ScheduledInvocationOwnerLLMRouteKind enum; numeric values remain wire-compatible. Add AI import dirs to every project that compiles these transitive protos.

- [ ] **Step 4: Carry Gateway evidence through actor-owned state and committed events**

Use field 26 on NyxIdAuthorizationCatalogState, field 14 on ObserveNyxIdAuthorizationCatalogCommand, field 15 on NyxIdAuthorizationCatalogObservedEvent, and field 26 on NyxIdAuthorizationCatalogDocument for gateway_llm_target. Extend NyxIdAuthorizationCatalogObservation and NyxIdAuthorizationCatalogSnapshot with NyxIdAuthorizationLLMTargetEvidence? GatewayLLMTarget. Clone it through command port, actor event/state, projector, and query.

- [ ] **Step 5: Validate and hash all typed LLM evidence**

Change the digest signature to:

~~~csharp
public static string ComputeContentDigest(
    AuthorizationOwnerIdentity owner,
    IEnumerable<NyxIdAuthorizationServiceEvidence> services,
    NyxIdAuthorizationLLMTargetEvidence? gatewayLLMTarget);
~~~

The full-owner digest serializes owner, ordinal-sorted services including each optional llm_target, and optional Gateway target. Actor validation calls LLMSelectionPolicy.ValidateCatalog, requires Enumerated/NotVerifiable/Unavailable invariants, validates timestamps and authority versions, and requires exact user-service parent identity or canonical Gateway identity. Targeted merge updates only the covered service evidence and/or explicitly supplied Gateway target; absent fields in historical observations remain absent rather than synthesized.

- [ ] **Step 6: Run focused catalog tests and verify GREEN**

Run the command from Step 2 again.

Expected: typed evidence, compatibility, validation, digest, actor, projector, and query tests pass.

- [ ] **Step 7: Commit catalog evidence**

~~~bash
git add src/platform/Aevatar.GAgentService.Abstractions src/platform/Aevatar.GAgentService.Core src/platform/Aevatar.GAgentService.Infrastructure/Schedules/Authorization/NyxIdAuthorizationCatalogCommandPort.cs src/platform/Aevatar.GAgentService.Projection test/Aevatar.GAgentService.Tests/Authorization/NyxIdAuthorizationCatalogLifecycleTests.cs test/Aevatar.GAgentService.Tests/Authorization/ScheduledInvocationAuthorizationPlannerTests.cs test/Aevatar.GAgentService.Tests/Projection/NyxIdAuthorizationCatalogRefreshObservationInfrastructureTests.cs
git commit -m "Persist LLM authorization catalog evidence"
~~~

### Task 10: Targeted secure LLM catalog refresh

**Files:**
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs
- Modify: src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs
- Modify: src/Aevatar.AI.ToolProviders.NyxId/LlmCatalog/NyxIdLlmServiceCatalogParser.cs
- Modify: src/platform/Aevatar.GAgentService.Infrastructure/Schedules/Authorization/NyxIdAuthorizationCatalogRefreshPort.cs
- Modify: src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs
- Modify: test/Aevatar.GAgentService.Tests/Authorization/NyxIdAuthorizationCatalogRefreshPortTests.cs
- Modify: test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs

**Interfaces:**
- Consumes: exact verified owner, inventory service identity, and shared catalog parser.
- Produces: ScheduledInvocationLLMRefreshRequirement(RouteKind, RouteValue, NyxIdUserServiceId, ServiceSlugSnapshot, ExplicitModelId, UserConfigStateVersion), NyxIdAuthorizationCatalogRefreshRequest(RequiredServices, LLMTarget), and INyxIdAuthorizationCatalogRefreshPort.RefreshAsync(owner, bearerToken, request, ct).

- [ ] **Step 1: Write failing targeted-refresh and SSRF tests**

Add cases proving only the required target is fetched:

~~~csharp
[Fact]
public async Task RefreshAsync_ForExactUserService_ShouldFetchOnlyVerifiedInventoryRoute()
{
    var request = new NyxIdAuthorizationCatalogRefreshRequest(
        [new NyxIdUserServiceCapabilityRef
        {
            UserServiceId = "us-alpha",
            ServiceSlugSnapshot = "chrono-llm-public",
        }],
        new ScheduledInvocationLLMRefreshRequirement(
            LLMRouteKind.NyxIdUserService,
            "/api/v1/proxy/s/chrono-llm-public",
            "us-alpha",
            "chrono-llm-public",
            "gpt-5.5",
            17));

    await port.RefreshAsync(Owner(), "secret-bearer", request, default);

    client.BoundedProxyCalls.Should().ContainSingle()
        .Which.Should().Match(call => call.UserServiceId == "us-alpha" && call.Slug == "chrono-llm-public" && call.Path == "models");
}
~~~

Add Gateway-only target with no service grants, request RouteValue containing an absolute evil URL still results in configured-authority canonical Gateway path, mismatched inventory ID/slug fails before model fetch, response over 1 MiB maps ResponseTooLarge without persisting the body, over 2,048 models/pattern-only/invalid IDs become NotVerifiable, access denial becomes Unavailable, and transport timeout records a refresh failure without overwriting the previous committed target. Assert logs and actor commands contain no bearer or upstream body.

- [ ] **Step 2: Run refresh tests and verify RED**

Run serially:

~~~bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter 'FullyQualifiedName~NyxIdAuthorizationCatalogRefreshPortTests'
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter 'FullyQualifiedName~StudioMemberWorkflowSchedulePortTests'
~~~

Expected: refresh accepts only service IDs, has no typed target, and stores no LLM evidence.

- [ ] **Step 3: Add one bounded canonical route-model read to NyxIdApiClient**

Implement:

~~~csharp
public Task<NyxIdProxyTextResponse> GetLlmRouteModelsBoundedAsync(
    string token,
    LLMRouteKind routeKind,
    string? verifiedUserServiceId,
    string? verifiedServiceSlug,
    long maxBytes,
    CancellationToken ct);
~~~

Gateway builds {configured NyxID base}/api/v1/llm/gateway/v1/models. User service calls the existing bounded proxy builder with the verified inventory slug, exact verified userServiceId, and path models. Reject Unspecified, noncanonical IDs/slugs, slashes in slug, and all caller URLs. Set maxBytes to 1 MiB at the refresh caller. Reuse SendTextResponseAsync and do not log response content.

- [ ] **Step 4: Replace the subset overload with a typed refresh request**

Keep the full-owner maintenance overload, but make authorization recovery call:

~~~csharp
Task<NyxIdAuthorizationCatalogRefreshResult> RefreshAsync(
    AuthorizationOwnerIdentity owner,
    string bearerToken,
    NyxIdAuthorizationCatalogRefreshRequest request,
    CancellationToken ct = default);
~~~

Normalize RequiredServices by exact ID. For a user-service LLM target, require the same ID and slug in the verified inventory and include only that service in the scope plan. For Gateway, allow zero service grants and fetch only Gateway models. Never fan out to unrelated providers.

- [ ] **Step 5: Parse and commit one typed observation**

Normalize the bounded OpenAI-compatible models response through LLMSelectionPolicy.NormalizeCatalog. Map successful negative observations into NyxIdAuthorizationLLMTargetEvidence with observation/fresh/evaluated timestamps and versions, attach it to the exact service or Gateway, and compute the full/targeted digest as required by Task 9. Transport errors call RecordRefreshFailureAsync and preserve the prior snapshot; authorization denial commits an Unavailable target only when the denial itself is a successful authoritative observation, otherwise records failure.

- [ ] **Step 6: Run focused refresh tests and verify GREEN**

Run both commands from Step 2 again.

Expected: exact target, Gateway, bounds, SSRF, timeout preservation, redaction, and one-refresh integration tests pass.

- [ ] **Step 7: Commit targeted refresh**

~~~bash
git add src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs src/Aevatar.AI.ToolProviders.NyxId/LlmCatalog/NyxIdLlmServiceCatalogParser.cs src/platform/Aevatar.GAgentService.Infrastructure/Schedules/Authorization/NyxIdAuthorizationCatalogRefreshPort.cs src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs test/Aevatar.GAgentService.Tests/Authorization/NyxIdAuthorizationCatalogRefreshPortTests.cs test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs
git commit -m "Refresh exact LLM catalog targets"
~~~

### Task 11: Durable LLM admission and runtime exact-match enforcement

**Files:**
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Protos/scheduled_invocation_authorization_plan.proto
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledServiceInvocationAuthorizationFailure.cs
- Modify: src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationPlanner.cs
- Modify: src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationRevalidator.cs
- Modify: src/platform/Aevatar.GAgentService.Infrastructure/Schedules/ScheduledServiceInvocationDispatchPort.cs
- Modify: test/Aevatar.GAgentService.Tests/Authorization/ScheduledInvocationAuthorizationPlannerTests.cs
- Modify: test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchServiceInvocationTests.cs

**Interfaces:**
- Consumes: shared LLMRouteKind, committed UserConfig owner selection, exact NyxIdAuthorizationLLMTargetEvidence, and catalog actor state version from Tasks 2, 9, and 10.
- Produces: ScheduledInvocationAuthorizationFailureCode.OwnerLLMRouteUnavailable, OwnerLLMModelNotVerifiable, and OwnerLLMModelUnavailable; ScheduledInvocationAuthorizationPlanResult.LLMRefreshRequirement; schema version scheduled-invocation-authorization/v3; runtime stable code owner_llm_payload_mismatch.

- [ ] **Step 1: Write failing planner, digest, and runtime tests**

Add planner cases for Gateway and user service proving that a durable owner target succeeds only when the saved selection is ExplicitModel and the exact route evidence is Enumerated with that ordinal model ID:

~~~csharp
[Fact]
public async Task PlanAsync_WithExactEnumeratedUserServiceModel_ShouldBindCatalogVersionAndModel()
{
    ownerLLMQuery.Result = OwnerLLMEvidence(
        stateVersion: 17,
        routeKind: LLMRouteKind.NyxIdUserService,
        routeValue: "/api/v1/proxy/s/chrono-llm-public",
        serviceId: "us-alpha",
        slug: "chrono-llm-public",
        model: "gpt-5.5");
    catalogQuery.Result = CatalogSnapshot(
        stateVersion: 29,
        serviceEvidence: EnumeratedServiceTarget("us-alpha", "chrono-llm-public", "gpt-5.5"));

    var result = await planner.PlanAsync(Request());

    result.Success.Should().BeTrue();
    result.Plan!.OwnerLlmSelection.Model.Should().Be("gpt-5.5");
    result.Plan.CatalogAuthority.ActorStateVersion.Should().Be(29);
    ScheduledInvocationAuthorizationPlanIntegrity.IsValid(result.Plan).Should().BeTrue();
}
~~~

Add separate cases for route Unavailable, NotVerifiable, Enumerated without the exact model, Gateway with no catalog, and ProviderDefault. Assert the three typed failure values, safe details, and one ScheduledInvocationLLMRefreshRequirement containing the original UserConfig state version. Recompute the permission digest after mutating the route, model, exact service ID, service slug, catalog state version, or target evidence digest and assert it changes.

Add runtime tests where the authorization fact contains route "/api/v1/proxy/s/chrono-llm-public" and model "gpt-5.5" but the payload uses Gateway, a case-different model, or a different service. Each must throw ScheduledServiceInvocationAuthorizationException with OwnerLLMPayloadMismatch before vault resolution, token exchange, actor lookup, or dispatch. An exact payload proceeds once.

- [ ] **Step 2: Run focused authorization tests and verify RED**

Run serially:

~~~bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter 'FullyQualifiedName~ScheduledInvocationAuthorizationPlannerTests'
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter 'FullyQualifiedName~ScheduledInvocationAuthorizationPlannerTests|FullyQualifiedName~ScheduledDispatchServiceInvocationTests'
~~~

Expected: the planner has only one generic DurableAuthorizationUnavailable outcome, Gateway bypasses the catalog when no service grant is required, and plan results cannot carry a typed LLM refresh requirement.

- [ ] **Step 3: Add typed planner failures and refresh requirement**

Append these protobuf values without renumbering existing fields:

~~~proto
SCHEDULED_INVOCATION_AUTHORIZATION_FAILURE_CODE_OWNER_LLM_ROUTE_UNAVAILABLE = 15;
SCHEDULED_INVOCATION_AUTHORIZATION_FAILURE_CODE_OWNER_LLM_MODEL_NOT_VERIFIABLE = 16;
SCHEDULED_INVOCATION_AUTHORIZATION_FAILURE_CODE_OWNER_LLM_MODEL_UNAVAILABLE = 17;
~~~

Extend ScheduledInvocationAuthorizationPlanResult and ScheduledInvocationAuthorizationValidationResult with:

~~~csharp
ScheduledInvocationLLMRefreshRequirement? LLMRefreshRequirement = null
~~~

The planner constructs the requirement directly from the committed LLMSelection evidence: shared route kind, canonical route, exact service ID and slug snapshot when applicable, explicit model ID, and UserConfig state version. Do not encode it in Detail or infer it from required service grants.

- [ ] **Step 4: Require exact committed model evidence in the planner**

Replace CanAuthorizeWithoutServiceCatalog with:

~~~csharp
private static bool CanAuthorizeWithoutCatalog(TargetEvidenceResolution evidence) =>
    evidence.RequiredServices.Count == 0 &&
    evidence.ServiceGrantRequirement == AuthorizationGrantRequirement.NotRequired &&
    evidence.OwnerLLMSelection is null;
~~~

For every non-null owner LLM selection, require ExplicitModel, then select GatewayLLMTarget or the exact parent service evidence by ordinal ID and slug. Map Unavailable/missing to OwnerLLMRouteUnavailable, NotVerifiable to OwnerLLMModelNotVerifiable, and Enumerated without the ordinal model to OwnerLLMModelUnavailable. Build the plan only from matching evidence, keep the catalog authority stamp, and bump ScheduledInvocationAuthorizationContractVersions.Schema to scheduled-invocation-authorization/v3. Revalidator repeats the same committed-evidence checks and returns the same typed outcomes; it performs no refresh or network access.

- [ ] **Step 5: Keep runtime validation before credentials and dispatch**

Keep ValidateOwnerLLMSelectionAndPayload before BuildInvocationRequestAsync. Use shared LLMSelectionPolicy model validation and ordinal equality for route, exact service identity, slug, and model. Map malformed frozen facts to OwnerLLMSelectionInvalid and any payload difference to OwnerLLMPayloadMismatch with safe stable code owner_llm_payload_mismatch. Do not log the payload, bearer, secret reference, upstream body, or model response.

- [ ] **Step 6: Run focused authorization tests and verify GREEN**

Run both commands from Step 2 again.

Expected: typed planner/revalidator failures, Gateway evidence, digest binding, refresh requirement, and pre-credential exact-match tests pass.

- [ ] **Step 7: Commit durable LLM admission**

~~~bash
git add src/platform/Aevatar.GAgentService.Abstractions/Protos/scheduled_invocation_authorization_plan.proto src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledServiceInvocationAuthorizationFailure.cs src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationPlanner.cs src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationRevalidator.cs src/platform/Aevatar.GAgentService.Infrastructure/Schedules/ScheduledServiceInvocationDispatchPort.cs test/Aevatar.GAgentService.Tests/Authorization/ScheduledInvocationAuthorizationPlannerTests.cs test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchServiceInvocationTests.cs
git commit -m "Enforce durable LLM target evidence"
~~~

### Task 12: Pure workflow artifact compatibility and local preflight

**Files:**
- Modify: src/workflow/Aevatar.Workflow.Abstractions/WorkflowCapabilityAdmissionPlanIntegrity.cs
- Modify: src/workflow/Aevatar.Workflow.Application.Abstractions/ExternalCapabilities/ExternalWorkflowCapabilityPorts.cs
- Create: src/workflow/Aevatar.Workflow.Application/ExternalCapabilities/WorkflowArtifactCompatibilityPreflight.cs
- Modify: src/workflow/Aevatar.Workflow.Application/DependencyInjection/ServiceCollectionExtensions.cs
- Create: test/Aevatar.Workflow.Application.Tests/WorkflowCapabilityAdmissionPlanIntegrityTests.cs
- Create: test/Aevatar.Workflow.Application.Tests/WorkflowArtifactCompatibilityPreflightTests.cs

**Interfaces:**
- Consumes: WorkflowDefinitionParser-compatible IWorkflowDefinitionParser, WorkflowAuthorizationDependencyEvaluator output, persisted WorkflowCapabilityAdmissionPlan, and explicit ExternalCapabilityExecutionMode.
- Produces: WorkflowCapabilityAdmissionCompatibilityFailure enum, WorkflowCapabilityAdmissionCompatibilityResult, WorkflowCapabilityAdmissionPlanIntegrity.CheckCompatibility(...), WorkflowArtifactCompatibilityRequest, and IWorkflowArtifactCompatibilityPreflight.ValidateAsync(request, ct).

- [ ] **Step 1: Write failing pure-integrity and preflight tests**

Add one table-driven integrity test that mutates exactly one dimension of a valid v4 plan: schema, execution mode, definition digest, call-site count/order, selector mapping, request/grant digest, durable owner, required source, or admission digest. Assert a typed failure instead of exception-text parsing:

~~~csharp
[Theory]
[InlineData("schema", WorkflowCapabilityAdmissionCompatibilityFailure.SchemaMismatch)]
[InlineData("mode", WorkflowCapabilityAdmissionCompatibilityFailure.ExecutionModeMismatch)]
[InlineData("definition", WorkflowCapabilityAdmissionCompatibilityFailure.DefinitionDigestMismatch)]
[InlineData("call-site", WorkflowCapabilityAdmissionCompatibilityFailure.InvocationMismatch)]
[InlineData("digest", WorkflowCapabilityAdmissionCompatibilityFailure.AdmissionDigestMismatch)]
public void CheckCompatibility_WithMutatedPlan_ShouldReturnTypedFailure(
    string mutation,
    WorkflowCapabilityAdmissionCompatibilityFailure expected)
{
    var fixture = ValidFixture();
    fixture.Mutate(mutation);

    var result = WorkflowCapabilityAdmissionPlanIntegrity.CheckCompatibility(
        fixture.Plan,
        fixture.WorkflowYaml,
        fixture.InlineWorkflowYamls,
        fixture.ExecutionMode,
        fixture.ExpectedInvocations,
        fixture.WorkflowId,
        fixture.RevisionId);

    result.Succeeded.Should().BeFalse();
    result.Failure.Should().Be(expected);
}
~~~

Add preflight cases for invalid root YAML, invalid distinct inline YAML, retired direct nyxid_*__* authoring, no external invocation with absent plan, no external invocation with an empty matching plan, external invocation with absent plan, legacy v2/v3 plan, and a mismatched v4 plan. Parser failures retain WORKFLOW_DEFINITION_INVALID or NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED; all plan-integrity failures map to CAPABILITY_ADMISSION_REBIND_REQUIRED. Assert zero capability source, network, event store, runtime, projection lifecycle, replay, repair, and RevalidatePersistedAsync calls.

- [ ] **Step 2: Run focused workflow Application tests and verify RED**

~~~bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter 'FullyQualifiedName~WorkflowCapabilityAdmissionPlanIntegrityTests|FullyQualifiedName~WorkflowArtifactCompatibilityPreflightTests'
~~~

Expected: no typed compatibility result or local preflight port exists, and ValidateOrThrow exposes integrity failures only as exceptions.

- [ ] **Step 3: Extract one typed, pure compatibility result**

Add:

~~~csharp
public enum WorkflowCapabilityAdmissionCompatibilityFailure
{
    None = 0,
    RebindRequiredSchema = 1,
    SchemaMismatch = 2,
    ExecutionModeMismatch = 3,
    DefinitionDigestMismatch = 4,
    InvocationMismatch = 5,
    InvocationOrderingInvalid = 6,
    AdmissionProofInvalid = 7,
    DurableOwnerInvalid = 8,
    RequiredSourceMissing = 9,
    AdmissionDigestMismatch = 10,
}

public sealed record WorkflowCapabilityAdmissionCompatibilityResult(
    WorkflowCapabilityAdmissionCompatibilityFailure Failure)
{
    public bool Succeeded => Failure == WorkflowCapabilityAdmissionCompatibilityFailure.None;
}
~~~

Implement CheckCompatibility with the current ValidateOrThrow rules and constant-time digest comparisons, but no time, source-freshness, I/O, or mutation. Make ValidateOrThrow call CheckCompatibility and preserve its existing public exception behavior for current callers. Every branch must return a failure enum; do not classify by exception message.

- [ ] **Step 4: Add the narrow local preflight service**

Define:

~~~csharp
public sealed record WorkflowArtifactCompatibilityRequest(
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls,
    WorkflowCapabilityAdmissionPlan? CapabilityAdmissionPlan,
    ExternalCapabilityExecutionMode ExpectedExecutionMode,
    string WorkflowId = "",
    string RevisionId = "");

public interface IWorkflowArtifactCompatibilityPreflight
{
    Task ValidateAsync(WorkflowArtifactCompatibilityRequest request, CancellationToken ct = default);
}
~~~

WorkflowArtifactCompatibilityPreflight parses the root and every distinct inline document through IWorkflowDefinitionParser, gathers the evaluator-produced ExternalInvocations, and applies the absent-plan matrix from the design. For plan failures throw WorkflowExternalCapabilityAdmissionException with AdmissionRebindRequired readiness, code CAPABILITY_ADMISSION_REBIND_REQUIRED, safe message "Saved workflow and capability admission no longer match.", and UpdateAndRebind remediation. Pass through parser-provided typed readiness unchanged. Reject ExpectedExecutionMode.Unspecified before parsing. Register the service as the single Application implementation.

- [ ] **Step 5: Run focused workflow Application tests and verify GREEN**

Run the command from Step 2 again.

Expected: every structural dimension has a typed result; absent-plan rules and safe error mapping pass with zero external or lifecycle work.

- [ ] **Step 6: Commit local compatibility preflight**

~~~bash
git add src/workflow/Aevatar.Workflow.Abstractions/WorkflowCapabilityAdmissionPlanIntegrity.cs src/workflow/Aevatar.Workflow.Application.Abstractions/ExternalCapabilities/ExternalWorkflowCapabilityPorts.cs src/workflow/Aevatar.Workflow.Application/ExternalCapabilities/WorkflowArtifactCompatibilityPreflight.cs src/workflow/Aevatar.Workflow.Application/DependencyInjection/ServiceCollectionExtensions.cs test/Aevatar.Workflow.Application.Tests/WorkflowCapabilityAdmissionPlanIntegrityTests.cs test/Aevatar.Workflow.Application.Tests/WorkflowArtifactCompatibilityPreflightTests.cs
git commit -m "Add local workflow artifact preflight"
~~~

### Task 13: Explicit execution mode propagation and pre-actor lifecycle gate

**Files:**
- Modify: src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto
- Modify: src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowRunPorts.cs
- Modify: src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowChatRunModels.cs
- Modify: src/workflow/Aevatar.Workflow.Core/workflow_state.proto
- Modify: src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs
- Modify: src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs
- Modify: src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.IdentityProvisioning.cs
- Modify: src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs
- Modify: src/workflow/Aevatar.Workflow.Projection/workflow_actor_binding_document.proto
- Modify: src/workflow/Aevatar.Workflow.Projection/workflow_projection_transport.proto
- Modify: src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowActorBindingProjector.cs
- Modify: src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionCurrentStateProjector.cs
- Modify: src/workflow/Aevatar.Workflow.Projection/Orchestration/ProjectionWorkflowActorBindingReader.cs
- Modify: src/workflow/Aevatar.Workflow.Projection/ReadModels/WorkflowRunForkSeedReadModelMapper.cs
- Modify: src/platform/Aevatar.GAgentService.Infrastructure/Activation/DefaultServiceRuntimeActivator.cs
- Modify: src/platform/Aevatar.GAgentService.Infrastructure/Dispatch/DefaultServiceInvocationDispatcher.cs
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Protos/service_revision.proto
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Services/WorkflowServiceRevisionArtifactBuilder.cs
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Services/WorkflowServiceDeploymentPlanIntegrity.cs
- Modify: src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs
- Modify: src/workflow/Aevatar.Workflow.Application/RunForks/WorkflowForkRunCommandTargetResolver.cs
- Modify: src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatRunRequestNormalizer.cs
- Modify: src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowWebhookIngressRequestBuilder.cs
- Modify: src/Aevatar.AI.ToolProviders.AevatarInvocation/AevatarInvocationDispatcher.cs
- Modify: src/Aevatar.Mainnet.Host.Api/Skills/UserSkillRunService.cs
- Modify: src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs
- Modify: src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeWorkflowEndpoints.cs
- Modify: src/workflow/Aevatar.Workflow.Core/Primitives/SubWorkflowOrchestrator.cs
- Modify: test/Aevatar.Workflow.Host.Api.Tests/WorkflowRunActorPortBranchTests.cs
- Modify: test/Aevatar.Workflow.Host.Api.Tests/WorkflowActorBindingProjectorTests.cs
- Modify: test/Aevatar.Workflow.Host.Api.Tests/RuntimeWorkflowActorBindingReaderTests.cs
- Modify: test/Aevatar.GAgentService.Tests/Infrastructure/DefaultServiceInvocationDispatcherTests.cs
- Modify: test/Aevatar.GAgentService.Tests/Infrastructure/DefaultServiceRuntimeActivatorTests.cs
- Modify: test/Aevatar.GAgentService.Tests/Application/WorkflowServiceRevisionArtifactBuilderTests.cs
- Modify: test/Aevatar.AI.ToolProviders.AevatarInvocation.Tests/AevatarInvocationToolSourceTests.cs
- Modify: test/Aevatar.Capabilities.Tests/UserSkillRunServiceTests.cs
- Modify: test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpoints/ScopeServiceContractEndpointTests.cs
- Modify: test/Aevatar.GAgentService.Integration.Tests/ScopeWorkflowEndpointsTests.cs
- Modify: test/Aevatar.Workflow.Application.Tests/WorkflowRunActorResolverTests.cs
- Modify: test/Aevatar.Workflow.Application.Tests/WorkflowChatRunRequestSeedTests.cs
- Modify: test/Aevatar.Workflow.Application.Tests/WorkflowForkRunCommandDispatchTests.cs
- Modify: test/Aevatar.Workflow.Host.Api.Tests/WorkflowCapabilityEndpointsCoverageTests.cs
- Modify: test/Aevatar.Workflow.Host.Api.Tests/WorkflowWebhookIngressEndpointsTests.cs

**Interfaces:**
- Consumes: IWorkflowArtifactCompatibilityPreflight from Task 12 and explicit ExternalCapabilityExecutionMode from publish, service deployment, chat, and fork contexts.
- Produces: non-optional WorkflowDefinitionBinding.ExpectedExecutionMode and WorkflowActorBinding.ExpectedExecutionMode; expected_execution_mode on definition/run binding events, actor state, and binding read model; one WorkflowRunActorPort gate before create, link, bind, repair, or dispatch.

- [ ] **Step 1: Write failing zero-mutation gate and propagation tests**

For EnsureDefinitionAsync, CreateRunAsync, EnsureRunAsync, EnsureRunAndDispatchAsync, and BindWorkflowDefinitionAsync add invalid-YAML, legacy NyxID authoring, absent-plan, mismatched-plan, and Unspecified-mode cases. Use a recording runtime and preflight:

~~~csharp
[Fact]
public async Task EnsureRunAndDispatchAsync_WhenArtifactNeedsRebind_ShouldMutateNoLifecycleState()
{
    var runtime = new RecordingActorRuntime();
    var dispatch = new RecordingActorDispatchPort();
    var preflight = new RejectingArtifactPreflight("CAPABILITY_ADMISSION_REBIND_REQUIRED");
    var port = CreatePort(runtime, dispatch, preflight);

    var act = () => port.EnsureRunAndDispatchAsync(
        DurableBinding(LegacyPlan()),
        "run-alpha",
        Request(),
        "cmd-alpha",
        "corr-alpha",
        default);

    await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
    runtime.CreateRequests.Should().BeEmpty();
    runtime.LinkRequests.Should().BeEmpty();
    runtime.DestroyRequests.Should().BeEmpty();
    dispatch.Dispatches.Should().BeEmpty();
}
~~~

Add an existing-definition case: allow one read-only binding read, reject when request mode Durable differs from bound Interactive, and assert zero runtime create/link/bind/repair/dispatch. Add an accepted case and assert the preflight is called once with the authoritative existing binding payload and mode before the first mutation.

Add protobuf/state/projector/reader tests proving Interactive and Durable survive definition event -> actor state -> committed state event -> WorkflowActorBindingDocument -> WorkflowActorBinding, and that run ensure rejects a mode different from its first binding. Add request-seed tests proving every WorkflowChatRunRequest carries a non-optional ExpectedExecutionMode and rejects Unspecified. Add artifact-builder tests proving WorkflowServiceDeploymentPlan carries the same explicit mode as its capability plan and rejects Unspecified or disagreement. Use distinct memberId m-alpha, workflowId wf-alpha, and publishedServiceId svc-alpha fixtures.

- [ ] **Step 2: Run focused lifecycle tests and verify RED**

Run serially:

~~~bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter 'FullyQualifiedName~WorkflowRunActorPortBranchTests|FullyQualifiedName~WorkflowActorBindingProjectorTests|FullyQualifiedName~RuntimeWorkflowActorBindingReaderTests'
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter 'FullyQualifiedName~DefaultServiceInvocationDispatcherTests|FullyQualifiedName~DefaultServiceRuntimeActivatorTests|FullyQualifiedName~WorkflowServiceRevisionArtifactBuilderTests'
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter 'FullyQualifiedName~WorkflowRunActorResolverTests|FullyQualifiedName~WorkflowChatRunRequestSeedTests|FullyQualifiedName~WorkflowForkRunCommandDispatchTests'
~~~

Expected: binding records and protobufs have no ExpectedExecutionMode, WorkflowRunActorPort has no preflight dependency, and invalid artifacts can reach actor creation.

- [ ] **Step 3: Add the execution mode to authoritative binding contracts**

Add expected_execution_mode at field 10 on BindWorkflowDefinitionEvent and field 10 on BindWorkflowRunDefinitionEvent. Add expected_execution_mode at field 22 on WorkflowState, field 44 on WorkflowRunState, field 18 on WorkflowActorBindingDocument, and field 35 on WorkflowExecutionCurrentStateDocument. Add execution_mode at field 9 on WorkflowServiceDeploymentPlan; WorkflowServiceRevisionArtifactBuilder copies capabilityAdmissionPlan.ExecutionMode after rejecting Unspecified, and WorkflowServiceDeploymentPlanIntegrity requires exact equality. Add non-optional ExternalCapabilityExecutionMode ExpectedExecutionMode to WorkflowChatRunRequest immediately after Source, and require every Host/Application ingress builder to supply it explicitly. The required producers are AevatarInvocationDispatcher, UserSkillRunService, ScopeServiceEndpoints, ScopeWorkflowEndpoints, ChatRunRequestNormalizer, WorkflowWebhookIngressRequestBuilder, WorkflowRunActorResolver, and WorkflowForkRunCommandTargetResolver; update their focused contract tests so no producer relies on a constructor default. Add ExternalCapabilityExecutionMode expectedExecutionMode immediately before CancellationToken on IWorkflowDefinitionProvisioningPort.BindWorkflowDefinitionAsync, WorkflowRunActorPort.BindWorkflowDefinitionAsync, and WorkflowGAgent.BindWorkflowDefinitionAsync; update all implementations, test doubles, and direct callers. Extend the two C# binding records as follows and update every constructor call:

~~~csharp
public sealed record WorkflowDefinitionBinding(
    string DefinitionActorId,
    string WorkflowName,
    string WorkflowYaml,
    IReadOnlyDictionary<string, string> InlineWorkflowYamls,
    ExternalCapabilityExecutionMode ExpectedExecutionMode,
    string ScopeId = "",
    string RunOrigin = "",
    string ScheduleId = "",
    string SourceKind = "",
    WorkflowCapabilityAdmissionPlan? CapabilityAdmissionPlan = null,
    string WorkflowId = "",
    string RevisionId = "");
~~~

WorkflowActorBinding gets the same non-optional property after InlineWorkflowYamls. Actor bind handlers reject Unspecified, persist the exact value, and existing definition/run binding equality includes it. The projector copies it from committed events and the reader maps it without inference. No overload retaining an implicit mode remains.

- [ ] **Step 4: Supply mode from each typed producer**

DefaultServiceRuntimeActivator and DefaultServiceInvocationDispatcher copy WorkflowServiceDeploymentPlan.ExecutionMode and reject Unspecified; they do not assign a mode from service context. WorkflowRunActorResolver copies WorkflowChatRunRequest.ExpectedExecutionMode and rejects Unspecified. Interactive HTTP/chat ingress builders set Interactive as their typed protocol fact; durable schedule/service builders set Durable as their typed protocol fact. WorkflowExecutionCurrentStateDocument and WorkflowRunForkSeedView carry the committed run ExpectedExecutionMode, and WorkflowForkRunCommandTargetResolver copies that exact value. SubWorkflowOrchestrator copies the parent run state's ExpectedExecutionMode into every child BindWorkflowRunDefinitionEvent. Publish/bind producers pass their existing admission request ExecutionMode. Do not derive mode from RunOrigin, ScheduleId, actor ID, workflow name, route, or presence of a capability plan.

- [ ] **Step 5: Gate every WorkflowRunActorPort lifecycle entry once**

Inject IWorkflowArtifactCompatibilityPreflight. At the start of EnsureDefinitionAsync and CreateRunAsync call a shared ValidateArtifactAsync. EnsureRunAsync and EnsureRunAndDispatchAsync call it once through EnsureRunCoreAsync. BindWorkflowDefinitionAsync calls it before runtime lookup or dispatch. When a requested definition actor already exists, read its current binding, require the requested and bound modes to match, and preflight the authoritative bound YAML/inline bundle/plan. Complete preflight before CreateAsync, LinkAsync, bind envelope dispatch, healing/repair, or run execution dispatch. Do not catch and downgrade WorkflowExternalCapabilityAdmissionException.

- [ ] **Step 6: Run focused lifecycle tests and verify GREEN**

Run all three commands from Step 2 again.

Expected: exact mode round-trip, producer propagation, mismatch rejection, accepted artifact, and zero-mutation rejection tests pass.

- [ ] **Step 7: Run mandatory workflow boundary guards**

~~~bash
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
~~~

Expected: all guards exit 0; the preflight uses the binding read model only and no query-time priming or local version is introduced.

- [ ] **Step 8: Commit the pre-actor gate**

~~~bash
git add src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowRunPorts.cs src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowChatRunModels.cs src/workflow/Aevatar.Workflow.Core src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatRunRequestNormalizer.cs src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowWebhookIngressRequestBuilder.cs src/workflow/Aevatar.Workflow.Projection src/platform/Aevatar.GAgentService.Abstractions/Protos/service_revision.proto src/platform/Aevatar.GAgentService.Abstractions/Services/WorkflowServiceRevisionArtifactBuilder.cs src/platform/Aevatar.GAgentService.Abstractions/Services/WorkflowServiceDeploymentPlanIntegrity.cs src/platform/Aevatar.GAgentService.Infrastructure/Activation/DefaultServiceRuntimeActivator.cs src/platform/Aevatar.GAgentService.Infrastructure/Dispatch/DefaultServiceInvocationDispatcher.cs src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeWorkflowEndpoints.cs src/Aevatar.AI.ToolProviders.AevatarInvocation/AevatarInvocationDispatcher.cs src/Aevatar.Mainnet.Host.Api/Skills/UserSkillRunService.cs src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs src/workflow/Aevatar.Workflow.Application/RunForks/WorkflowForkRunCommandTargetResolver.cs test/Aevatar.Workflow.Host.Api.Tests/WorkflowRunActorPortBranchTests.cs test/Aevatar.Workflow.Host.Api.Tests/WorkflowActorBindingProjectorTests.cs test/Aevatar.Workflow.Host.Api.Tests/RuntimeWorkflowActorBindingReaderTests.cs test/Aevatar.Workflow.Host.Api.Tests/WorkflowCapabilityEndpointsCoverageTests.cs test/Aevatar.Workflow.Host.Api.Tests/WorkflowWebhookIngressEndpointsTests.cs test/Aevatar.GAgentService.Tests/Infrastructure/DefaultServiceInvocationDispatcherTests.cs test/Aevatar.GAgentService.Tests/Infrastructure/DefaultServiceRuntimeActivatorTests.cs test/Aevatar.GAgentService.Tests/Application/WorkflowServiceRevisionArtifactBuilderTests.cs test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpoints/ScopeServiceContractEndpointTests.cs test/Aevatar.GAgentService.Integration.Tests/ScopeWorkflowEndpointsTests.cs test/Aevatar.AI.ToolProviders.AevatarInvocation.Tests/AevatarInvocationToolSourceTests.cs test/Aevatar.Capabilities.Tests/UserSkillRunServiceTests.cs test/Aevatar.Workflow.Application.Tests/WorkflowRunActorResolverTests.cs test/Aevatar.Workflow.Application.Tests/WorkflowChatRunRequestSeedTests.cs test/Aevatar.Workflow.Application.Tests/WorkflowForkRunCommandDispatchTests.cs
git commit -m "Reject incompatible workflows before actor lifecycle"
~~~

### Task 14: Attribute scheduled admission rejection to the schedule with zero Run artifacts

**Files:**
- Modify: src/workflow/Aevatar.Workflow.Application.Abstractions/ExternalCapabilities/ExternalWorkflowCapabilityPorts.cs
- Modify: src/platform/Aevatar.GAgentService.Infrastructure/Dispatch/DefaultServiceInvocationDispatcher.cs
- Modify: src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto
- Modify: src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs
- Modify: src/platform/Aevatar.GAgentService.Projection/service_projection_read_models.proto
- Modify: src/platform/Aevatar.GAgentService.Projection/Projectors/ScheduledDispatchCurrentStateProjector.cs
- Modify: src/platform/Aevatar.GAgentService.Projection/Queries/ScheduledDispatchQueryPort.cs
- Modify: src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledDispatchModels.cs
- Modify: test/Aevatar.GAgentService.Tests/Infrastructure/DefaultServiceInvocationDispatcherTests.cs
- Modify: test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs
- Modify: test/Aevatar.GAgentService.Tests/Projection/ScheduledDispatchCurrentStateProjectorTests.cs

**Interfaces:**
- Consumes: WorkflowExternalCapabilityAdmissionException with typed ExternalCapabilityReadiness and WorkflowRunActorPort's pre-mutation rejection from Task 13.
- Produces: safe StableCode on WorkflowExternalCapabilityAdmissionException; ScheduledDispatchFireFailedEvent.error_code; schedule LastError safe message plus LastErrorCode, incremented FailureCount, and no workflow actor or service-run registration.

- [ ] **Step 1: Write failing zero-Run and schedule evidence tests**

Add an exact requested-run service invocation test whose injected local preflight rejects with NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED before service-run registration or workflow actor lifecycle:

~~~csharp
[Fact]
public async Task WorkflowAdmissionRejection_ShouldCreateNoWorkflowOrServiceRun()
{
    var workflowPort = new RecordingWorkflowRunActorPort();
    var preflight = RejectingPreflight(
        "NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED",
        "Workflow uses a retired NyxID tool contract.");
    var registrations = new RecordingServiceRunRegistrationPort();
    var dispatcher = CreateDispatcher(workflowPort, registrations, preflight);

    var act = () => dispatcher.DispatchAsync(DurableWorkflowTarget(), ScheduledRequest("run-alpha"));

    var error = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
    error.Which.StableCode.Should().Be("NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED");
    workflowPort.Calls.Should().BeEmpty();
    registrations.Records.Should().BeEmpty();
}
~~~

Cover WORKFLOW_DEFINITION_INVALID and CAPABILITY_ADMISSION_REBIND_REQUIRED the same way. Add schedule actor tests that inject each rejection and assert FireCount=1, FailureCount=1, LastError equals the safe message, LastErrorCode equals the stable code, the fire record is Failed, and no target receipt exists. Assert actor state, projected document, logs, and API summary contain no YAML, bearer, secret reference, upstream error body, stack trace, or exception type.

- [ ] **Step 2: Run focused rejection tests and verify RED**

Run serially:

~~~bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter 'FullyQualifiedName~DefaultServiceInvocationDispatcherTests'
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter 'FullyQualifiedName~ScheduledDispatchGAgentTests'
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter 'FullyQualifiedName~ScheduledDispatchCurrentStateProjectorTests'
~~~

Expected: exact-run dispatch registers a service Run before the workflow port admits it, and schedule failure has only an untyped exception message.

- [ ] **Step 3: Expose one safe stable workflow admission code**

Add StableCode and SafeMessage to WorkflowExternalCapabilityAdmissionException. StableCode is the first non-empty typed blocker code, restricted to WORKFLOW_DEFINITION_INVALID, NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED, or CAPABILITY_ADMISSION_REBIND_REQUIRED for this path; an unknown value maps to WORKFLOW_ADMISSION_REJECTED. SafeMessage is the blocker SafeMessage or "Workflow admission was rejected.". Neither property includes YAML, selectors, service response content, credentials, or the exception stack.

- [ ] **Step 4: Preflight exact runs before service-run registration**

Inject the same IWorkflowArtifactCompatibilityPreflight into DefaultServiceInvocationDispatcher and validate the exact WorkflowDefinitionBinding before RegisterRunAsync. Only after this pure local check succeeds may the existing order continue: register service Run, then call EnsureRunAndDispatchAsync. Keep WorkflowRunActorPort's pre-mutation gate as the authoritative final guard; the dispatcher check exists only to prevent the surrounding service-run record from preceding that guard. The random-run branch already calls CreateRunAsync before registration, so its compatibility rejection remains registration-free. Do not move registration after execution start and do not erase a Run that was genuinely accepted.

- [ ] **Step 5: Persist typed schedule failure evidence**

Add string error_code=6 to ScheduledDispatchFireFailedEvent, string error_code=10 to ScheduledDispatchFireRecordState (field 9 is the existing status), string last_error_code=66 to ScheduledDispatchState (field 65 is the existing delete reason), string last_error_code=64 to ScheduledDispatchDocument, and string error_code=9 to ScheduledDispatchFireRecordDocument. Add LastErrorCode to ScheduledDispatchSummary immediately after LastError. When catching WorkflowExternalCapabilityAdmissionException, call PersistFireFailedAsync with StableCode and SafeMessage. Store LastError as the safe message, LastErrorCode as the stable code, increment FireCount and FailureCount once, and keep the schedule enabled for operator repair. For generic exceptions use error_code=scheduled_dispatch_failed and the existing sanitized message. Project the typed code through the existing current-state projector and query summary without adding a second failure store.

- [ ] **Step 6: Run focused rejection tests and verify GREEN**

Run all three commands from Step 2 again.

Expected: all three stable workflow admission outcomes produce zero workflow/service Run artifacts and one schedule-owned failed fire with redacted typed evidence.

- [ ] **Step 7: Commit schedule rejection evidence**

~~~bash
git add src/workflow/Aevatar.Workflow.Application.Abstractions/ExternalCapabilities/ExternalWorkflowCapabilityPorts.cs src/platform/Aevatar.GAgentService.Infrastructure/Dispatch/DefaultServiceInvocationDispatcher.cs src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs src/platform/Aevatar.GAgentService.Projection/service_projection_read_models.proto src/platform/Aevatar.GAgentService.Projection/Projectors/ScheduledDispatchCurrentStateProjector.cs src/platform/Aevatar.GAgentService.Projection/Queries/ScheduledDispatchQueryPort.cs src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledDispatchModels.cs test/Aevatar.GAgentService.Tests/Infrastructure/DefaultServiceInvocationDispatcherTests.cs test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs test/Aevatar.GAgentService.Tests/Projection/ScheduledDispatchCurrentStateProjectorTests.cs
git commit -m "Record workflow admission failure on schedules"
~~~

### Task 15: Canonical documentation and release verification

**Files:**
- Modify: docs/canon/workflow-runtime.md
- Modify: docs/canon/nyxid-llm-integration.md
- Modify: docs/canon/scheduled-skill-runners.md
- Generated: docs/README.md via tools/docs/build-index.sh
- Verify: docs/superpowers/specs/2026-08-01-llm-workflow-admission-design.md

**Interfaces:**
- Consumes: all contracts and behavior from Tasks 1-14.
- Produces: one documented route/model authority, one durable catalog evidence path, one local workflow preflight path, rollout order, operator remediation, and a fully verified release candidate.

- [ ] **Step 1: Record the pre-change documentation gap**

Run these bounded checks and record that the canonical pages do not yet state the new contract:

~~~bash
rg -n 'LLMSelection|enumerated committed catalog|ExpectedExecutionMode|zero Run' docs/canon/workflow-runtime.md docs/canon/nyxid-llm-integration.md docs/canon/scheduled-skill-runners.md
~~~

Expected: at least one of the four required phrases is absent. This is the documentation gap; do not add a new linter or architecture guard for prose wording.

- [ ] **Step 2: Update only canonical documentation**

Document these exact semantics in the three canonical pages: LLMSelection is the atomic UserConfig route/model fact; Reset is System default, not Gateway; durable LLM execution needs an explicit model in Enumerated committed catalog evidence; workflow compatibility completes before actor lifecycle; ExpectedExecutionMode is explicit; rejection belongs to the schedule and creates no Run. Also document the UserConfig -> projection selection write, authorization catalog refresh -> actor -> projection evidence path, planner/runtime exact match, typed error/remediation table, and deployment order. State that rollout performs a read-only audit and no automatic production migration, rerun, pause, delete, or repair. Explicitly reject empty-list-as-open-catalog, accepted-ACK-as-active, silent Gateway fallback, query-time catalog reads, and invocation-time RevalidatePersistedAsync.

Use this required Mermaid header and quoted labels:

~~~mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
  A["Typed selection or persisted workflow"] --> B["Committed read models"]
  B --> C["Local admission"]
  C -->|"accepted"| D["Actor inbox"]
  C -->|"rejected"| E["Typed repair action; zero Run"]
~~~

Regenerate docs/README.md with tools/docs/build-index.sh after editing canonical pages.

- [ ] **Step 3: Verify the canonical contract and docs lint**

~~~bash
rg -n 'LLMSelection|enumerated committed catalog|ExpectedExecutionMode|zero Run' docs/canon/workflow-runtime.md docs/canon/nyxid-llm-integration.md docs/canon/scheduled-skill-runners.md
bash tools/docs/lint.sh
~~~

Expected: all four required phrases are present across the canonical pages and docs lint exits 0.

- [ ] **Step 4: Run focused tests and mandatory guards serially**

~~~bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo
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
~~~

Expected: every command exits 0. Run .NET commands one at a time.

- [ ] **Step 5: Run full restore, build, test, and frontend verification**

~~~bash
dotnet restore aevatar.slnx --nologo
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web test --runInBand
pnpm --dir apps/aevatar-console-web build
~~~

Expected: every command exits 0. Run the .NET commands serially and do not use port 5000 or 5050.

- [ ] **Step 6: Audit the final diff for forbidden behavior and secrets**

~~~bash
rg -n 'DefaultModel\s*=|SetModelOverrideAsync|SaveSelectedOptionAsync|RevalidatePersistedAsync|Task\.Delay\(|WaitUntilAsync\(' agents src test apps/aevatar-console-web/src
rg -n 'bearer|refresh_token|agent[_ -]?key|vault ciphertext' docs/superpowers/specs/2026-08-01-llm-workflow-admission-design.md docs/canon/workflow-runtime.md docs/canon/nyxid-llm-integration.md docs/canon/scheduled-skill-runners.md
git diff --check
git status --short
~~~

Expected: first search contains only documented compatibility reads, explicit publish/reauthorize uses of RevalidatePersistedAsync, and pre-existing allowlisted polling; no removed model-only write survives. Second search contains only redaction/security policy prose and no credential value. git diff --check exits 0, and status contains only intended implementation/docs files.

- [ ] **Step 7: Commit canonical docs**

~~~bash
git add docs/canon/workflow-runtime.md docs/canon/nyxid-llm-integration.md docs/canon/scheduled-skill-runners.md docs/README.md tools/docs test
git commit -m "Document LLM workflow admission boundaries"
~~~
