# Owner LLM Exact Service Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the owner's exact NyxID `UserService.id` in actor-owned UserConfig state so scheduled workflows can provision and run with a constrained Agent Key without query-time identity discovery.

**Architecture:** Authenticated write boundaries compose exact identity only from NyxID `/api/v1/user-services`, dispatch typed delta commands, and let `UserConfigGAgent` merge against authoritative state before emitting a full committed event. Projection copies the typed selection into the current-state read model; schedule preflight joins its exact ID against the existing authorization-catalog read model. Caller binding is stored separately from the Agent Key credential source and Gateway remains an explicit canonical route.

**Tech Stack:** .NET 10, C#, Protobuf, xUnit, FluentAssertions, React, TypeScript, TanStack Query, Ant Design, Jest, pnpm.

## Global Constraints

- Preserve `Domain / Application / Infrastructure / Host` dependency direction; API/Host only adapts and composes.
- All authoritative UserConfig state, commands, events, and read-model payloads use Protobuf.
- Writes are `Command -> Event`; queries read only committed read models.
- Application services never merge writes from an eventually consistent UserConfig read model.
- Exact LLM identity is minted only from strict `/api/v1/user-services` inventory provenance.
- `userServiceId`, route, owner scope, binding ID, and published service ID remain distinct fields and identities.
- Owner actor IDs remain `user-config-{scopeId}`; binding actor IDs are `channel-user-config-{bindingId}`.
- Preflight and planner issue no bearer token, call no live LLM catalog, and perform no slug-to-ID inference.
- Explicit Gateway is `/api/v1/llm/gateway/v1` and cannot fall through to `chrono-llm-public`.
- Preserve the 90-day constrained key policy with both `allow_all_services` and `allow_all_nodes` false.
- Keep #2912 verified binding propagation and remove its query-time owner LLM resolver.
- Do not include the generic `scheduled_agent_creator` missing-`AuthorizationFact` issue.
- Never log or persist bearer tokens, raw Agent Keys, vault ciphertext, or secret material.

---

### Task 1: Actor-Owned Typed Selection And Delta Command

**Files:**
- Modify: `agents/Aevatar.GAgents.UserConfig/user_config_messages.proto`
- Modify: `agents/Aevatar.GAgents.UserConfig/UserConfigGAgent.cs`
- Modify: `agents/Aevatar.GAgents.UserConfig/Aevatar.GAgents.UserConfig.csproj`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IUserConfigStore.cs`
- Create: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserConfigResourceKey.cs`
- Create: `test/Aevatar.Studio.Tests/UserConfigGAgentStateTests.cs`
- Modify: `test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj`

**Interfaces:**
- Produces: `UserLlmSelection`, `UpdateUserConfigCommand`, `UserLlmSelectionKind`, `UserConfigUpdate`, and `UserConfigResourceKey`.
- Leaves existing application ports temporarily intact so this contract commit remains buildable; Tasks 2 and 4 migrate and then delete the old full-state methods.

- [ ] **Step 1: Write failing actor and resource-key tests**

Add tests that lock Protobuf round-trip, command presence, actor-side merge, invalid selection rejection, and opaque resource separation:

```csharp
[Fact]
public void ResourceKeys_WithSimilarOpaqueValues_ShouldRemainDistinct()
{
    var owner = UserConfigResourceKey.ForOwnerScope("binding-alpha");
    var binding = UserConfigResourceKey.ForChannelBinding("alpha");

    owner.Kind.Should().Be(UserConfigResourceKind.OwnerScope);
    owner.Value.Should().Be("binding-alpha");
    binding.Kind.Should().Be(UserConfigResourceKind.ChannelBinding);
    binding.Value.Should().Be("alpha");
    owner.Should().NotBe(binding);
}

[Fact]
public void UserLlmSelection_NyxIdUserService_ShouldRoundTripThroughProtobuf()
{
    var selection = new Aevatar.GAgents.UserConfig.UserLlmSelection
    {
        RouteKind = UserLlmRouteKind.NyxIdUserService,
        RouteValue = "/api/v1/proxy/s/chrono-llm-public",
        NyxIdUserServiceId = "us-alpha",
        ServiceSlugSnapshot = "chrono-llm-public",
    };

    var roundTrip = Aevatar.GAgents.UserConfig.UserLlmSelection.Parser.ParseFrom(selection.ToByteArray());
    roundTrip.Should().BeEquivalentTo(selection);
}

[Fact]
public void BuildUpdatedEvent_ShouldPreserveOmittedFieldsAndReturnFullStateEvent()
{
    var state = new UserConfigGAgentState
    {
        DefaultModel = "gpt-5.4",
        RuntimeMode = "remote",
        LlmSelection = GatewaySelection(),
        PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
    };

    var committed = UserConfigGAgent.BuildUpdatedEvent(state, new UpdateUserConfigCommand
    {
        DefaultModel = "gpt-5.5",
    });

    committed.DefaultModel.Should().Be("gpt-5.5");
    committed.RuntimeMode.Should().Be("remote");
    committed.LlmSelection.Should().BeEquivalentTo(GatewaySelection());
    committed.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
}

private static Aevatar.GAgents.UserConfig.UserLlmSelection GatewaySelection() => new()
{
    RouteKind = UserLlmRouteKind.Gateway,
    RouteValue = UserConfigLlmRouteDefaults.Gateway,
};
```

Expose `BuildUpdatedEvent` to `Aevatar.Studio.Tests` with `InternalsVisibleTo`. Add a theory with the exact invalid cases: unspecified kind, noncanonical Gateway route, Gateway with ID, service selection missing route, service selection missing ID, service selection missing slug, route/slug mismatch, and whitespace around route/ID/slug. The public handler remains a one-line `PersistDomainEventAsync(BuildUpdatedEvent(State, command))`, so the pure test covers all merge and validation behavior without a second state owner.

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~UserConfigGAgentStateTests"
```

Expected: compilation fails because the typed selection, delta command, resource key, and actor handler do not exist.

- [ ] **Step 3: Add Protobuf state, command, and event contracts**

Append fields without renumbering existing fields:

```proto
enum UserLlmRouteKind {
  USER_LLM_ROUTE_KIND_UNSPECIFIED = 0;
  USER_LLM_ROUTE_KIND_GATEWAY = 1;
  USER_LLM_ROUTE_KIND_NYX_ID_USER_SERVICE = 2;
}

message UserLlmSelection {
  UserLlmRouteKind route_kind = 1;
  string route_value = 2;
  string nyx_id_user_service_id = 3;
  string service_slug_snapshot = 4;
}

message UpdateUserConfigCommand {
  optional string default_model = 1;
  UserLlmSelection llm_selection = 2;
  optional string runtime_mode = 3;
  optional string local_runtime_base_url = 4;
  optional string remote_runtime_base_url = 5;
  optional int32 max_tool_rounds = 6;
  optional string github_username = 7;
}
```

Add `UserLlmSelection llm_selection = 8;` to both `UserConfigGAgentState` and `UserConfigUpdatedEvent`. Keep `UserConfigGithubUsernameUpdatedEvent` only for replay compatibility; new writes use `UpdateUserConfigCommand`.

- [ ] **Step 4: Add application values and typed resource keys**

Set the canonical Gateway constant and add application values:

```csharp
public static class UserConfigLlmRouteDefaults
{
    public const string Gateway = "/api/v1/llm/gateway/v1";
}

public enum UserLlmSelectionKind
{
    Unspecified = 0,
    Gateway = 1,
    NyxIdUserService = 2,
}

public sealed record UserLlmSelectionValue(
    UserLlmSelectionKind Kind,
    string RouteValue,
    string NyxIdUserServiceId,
    string ServiceSlugSnapshot);

public sealed record UserConfigUpdate(
    string? DefaultModel = null,
    UserLlmSelectionValue? LlmSelection = null,
    string? RuntimeMode = null,
    string? LocalRuntimeBaseUrl = null,
    string? RemoteRuntimeBaseUrl = null,
    string? GithubUsername = null,
    int? MaxToolRounds = null);
```

Append `UserLlmSelectionValue? LlmSelection = null` to `UserConfig`. Implement the resource key as:

```csharp
public enum UserConfigResourceKind
{
    OwnerScope = 1,
    ChannelBinding = 2,
}

public readonly record struct UserConfigResourceKey(UserConfigResourceKind Kind, string Value)
{
    public static UserConfigResourceKey ForOwnerScope(string scopeId) =>
        new(UserConfigResourceKind.OwnerScope, Normalize(scopeId, nameof(scopeId)));

    public static UserConfigResourceKey ForChannelBinding(string bindingId) =>
        new(UserConfigResourceKind.ChannelBinding, Normalize(bindingId, nameof(bindingId)));

    private static string Normalize(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value.Trim();
    }
}
```

Do not change command/query port signatures in this task. They are migrated additively in Task 2 and narrowed after all callers move in Task 4.

- [ ] **Step 5: Implement actor validation, actor-side merge, and committed event creation**

Replace the event-as-command handler with:

```csharp
private const string GatewayRoute = "/api/v1/llm/gateway/v1";

[EventHandler(EndpointName = "updateConfigDelta")]
public async Task HandleUpdateConfig(UpdateUserConfigCommand command)
{
    ArgumentNullException.ThrowIfNull(command);
    await PersistDomainEventAsync(BuildUpdatedEvent(State, command));
}

internal static UserConfigUpdatedEvent BuildUpdatedEvent(
    UserConfigGAgentState state,
    UpdateUserConfigCommand command)
{
    ArgumentNullException.ThrowIfNull(state);
    ArgumentNullException.ThrowIfNull(command);
    if (command.LlmSelection is not null)
        ValidateSelection(command.LlmSelection);

    var selection = command.LlmSelection?.Clone() ?? state.LlmSelection?.Clone();
    var evt = new UserConfigUpdatedEvent
    {
        DefaultModel = command.HasDefaultModel ? command.DefaultModel : state.DefaultModel,
        PreferredLlmRoute = command.LlmSelection is null
            ? state.PreferredLlmRoute
            : command.LlmSelection.RouteValue,
        RuntimeMode = command.HasRuntimeMode ? command.RuntimeMode : state.RuntimeMode,
        LocalRuntimeBaseUrl = command.HasLocalRuntimeBaseUrl
            ? command.LocalRuntimeBaseUrl
            : state.LocalRuntimeBaseUrl,
        RemoteRuntimeBaseUrl = command.HasRemoteRuntimeBaseUrl
            ? command.RemoteRuntimeBaseUrl
            : state.RemoteRuntimeBaseUrl,
        MaxToolRounds = command.HasMaxToolRounds ? command.MaxToolRounds : state.MaxToolRounds,
        GithubUsername = command.HasGithubUsername ? command.GithubUsername : state.GithubUsername,
    };
    if (selection is not null)
        evt.LlmSelection = selection;
    return evt;
}

private static void ValidateSelection(Aevatar.GAgents.UserConfig.UserLlmSelection selection)
{
    var rawRoute = selection.RouteValue ?? string.Empty;
    var rawId = selection.NyxIdUserServiceId ?? string.Empty;
    var rawSlug = selection.ServiceSlugSnapshot ?? string.Empty;
    var route = rawRoute.Trim();
    var id = rawId.Trim();
    var slug = rawSlug.Trim();
    if (route != rawRoute || id != rawId || slug != rawSlug)
        throw new InvalidOperationException("user_llm_selection_not_canonical");

    switch (selection.RouteKind)
    {
        case UserLlmRouteKind.Gateway when route == GatewayRoute && id.Length == 0 && slug.Length == 0:
            return;
        case UserLlmRouteKind.NyxIdUserService
            when id.Length > 0 &&
                 slug.Length > 0 &&
                 !slug.Contains('/') &&
                 string.Equals(route, $"/api/v1/proxy/s/{slug}", StringComparison.Ordinal):
            return;
        default:
            throw new InvalidOperationException("user_llm_selection_invalid");
    }
}
```

Ensure `ApplyConfigUpdated` and the legacy GitHub transition clone and preserve `LlmSelection`.
Keep the existing `UserConfigUpdatedEvent` handler only until Task 4 migrates every writer; Task 4 deletes that event-as-command entry point.

- [ ] **Step 6: Run tests and commit**

Run the focused test command from Step 2 and expect PASS, then:

```bash
git add agents/Aevatar.GAgents.UserConfig src/Aevatar.Studio.Application.Abstractions test/Aevatar.Studio.Tests
git commit -m "Model actor-owned user LLM selection"
```

---

### Task 2: Delta Dispatch And Current-State Projection

**Files:**
- Modify: `src/Aevatar.Studio.Projection/Aevatar.Studio.Projection.csproj`
- Modify: `src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IUserConfigCommandService.cs`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IUserConfigQueryPort.cs`
- Modify: `src/Aevatar.Studio.Projection/CommandServices/ActorDispatchUserConfigCommandService.cs`
- Modify: `src/Aevatar.Studio.Projection/Projectors/UserConfigCurrentStateProjector.cs`
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionUserConfigQueryPort.cs`
- Modify: `src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedNyxIdUserLlmPreferencesStore.cs`
- Modify: `test/Aevatar.Studio.Tests/ActorDispatchUserConfigCommandServiceTests.cs`
- Create: `test/Aevatar.Studio.Tests/UserConfigCurrentStateProjectorTests.cs`

**Interfaces:**
- Consumes: Task 1 application values and actor Protobuf command.
- Produces: exact one-to-one delta dispatch and projection-backed typed reads as additive port methods; old methods remain only until Task 4 migrates all callers.

- [ ] **Step 1: Write failing adapter and projector tests**

Add an adapter test that checks Protobuf presence and disjoint actor IDs:

```csharp
[Fact]
public async Task UpdateAsync_ShouldMapOnlyPresentFieldsAndKeepResourceKindsDistinct()
{
    var dispatch = RecordingDispatchPort.Accepting();
    var service = CreateService(dispatch);
    var selection = new UserLlmSelectionValue(
        UserLlmSelectionKind.NyxIdUserService,
        "/api/v1/proxy/s/chrono-llm-public",
        "us-alpha",
        "chrono-llm-public");

    await service.UpdateAsync(
        UserConfigResourceKey.ForOwnerScope("binding-alpha"),
        new UserConfigUpdate(DefaultModel: "gpt-5.5", LlmSelection: selection));
    await service.UpdateAsync(
        UserConfigResourceKey.ForChannelBinding("alpha"),
        new UserConfigUpdate(DefaultModel: "claude-4"));

    dispatch.Dispatches.Select(x => x.ActorId).Should().Equal(
        "user-config-binding-alpha",
        "channel-user-config-alpha");
    var command = dispatch.Dispatches[0].Envelope.Payload.Unpack<UpdateUserConfigCommand>();
    command.HasDefaultModel.Should().BeTrue();
    command.HasRuntimeMode.Should().BeFalse();
    command.LlmSelection.NyxIdUserServiceId.Should().Be("us-alpha");
}
```

Add projector tests proving selection clone, `StateVersion`, and legacy absence.

- [ ] **Step 2: Run focused tests and verify failure**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~ActorDispatchUserConfigCommandServiceTests|FullyQualifiedName~UserConfigCurrentStateProjectorTests"
```

Expected: compilation failures for old `SaveAsync` and missing read-model selection.

- [ ] **Step 3: Add selection to the projection Protobuf**

Import the actor contract and add the field:

```proto
import "user_config_messages.proto";

message UserConfigCurrentStateDocument {
  string id = 1;
  string actor_id = 2;
  int64 state_version = 3;
  string last_event_id = 4;
  google.protobuf.Timestamp updated_at = 5;
  string default_model = 10;
  string preferred_llm_route = 11;
  string runtime_mode = 12;
  string local_runtime_base_url = 13;
  string remote_runtime_base_url = 14;
  int32 max_tool_rounds = 15;
  string github_username = 16;
  aevatar.gagents.user_config.UserLlmSelection llm_selection = 17;
}
```

Set `AdditionalImportDirs="../../agents/Aevatar.GAgents.UserConfig"` on the Projection `<Protobuf>` item.

- [ ] **Step 4: Implement one-to-one command mapping and typed actor IDs**

Add `UpdateAsync(UserConfigResourceKey, UserConfigUpdate, CancellationToken)` to `IUserConfigCommandService` while retaining the existing methods until Task 4. Add `GetAsync(UserConfigResourceKey, CancellationToken)` to `IUserConfigQueryPort` on the same staged basis. To keep existing test doubles source-compatible during the staged commit, give only the new methods temporary fail-fast default implementations:

```csharp
Task<UserConfigSaveReceipt> UpdateAsync(
    UserConfigResourceKey resource,
    UserConfigUpdate update,
    CancellationToken ct = default) =>
    throw new NotSupportedException("Typed UserConfig updates are not implemented by this adapter.");

Task<UserConfig> GetAsync(
    UserConfigResourceKey resource,
    CancellationToken ct = default) =>
    throw new NotSupportedException("Typed UserConfig reads are not implemented by this adapter.");
```

The production Projection adapter overrides both methods in this task. Do not forward these defaults to old full-state/string methods. Task 4 updates remaining fakes and deletes both defaults together with the old port surface.

Use one actor-ID mapper:

```csharp
private static string BuildActorId(UserConfigResourceKey resource) => resource.Kind switch
{
    UserConfigResourceKind.OwnerScope => $"user-config-{resource.Value}",
    UserConfigResourceKind.ChannelBinding => $"channel-user-config-{resource.Value}",
    _ => throw new ArgumentOutOfRangeException(nameof(resource)),
};
```

Map nullable application fields by assigning only when present; map `UserLlmSelectionValue` explicitly to generated `UserLlmSelection`. The new method dispatches `UpdateUserConfigCommand`, never `UserConfigUpdatedEvent`. Existing full-state methods remain unchanged in this commit and are removed in Task 4.

- [ ] **Step 5: Project and query the typed selection**

In the projector, clone selection only when it is present; generated Protobuf message setters must never receive null:

```csharp
var document = new UserConfigCurrentStateDocument
{
    Id = context.RootActorId,
    ActorId = context.RootActorId,
    StateVersion = stateEvent.Version,
    LastEventId = stateEvent.EventId ?? string.Empty,
    UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
    DefaultModel = state.DefaultModel,
    PreferredLlmRoute = state.PreferredLlmRoute,
    RuntimeMode = state.RuntimeMode,
    LocalRuntimeBaseUrl = state.LocalRuntimeBaseUrl,
    RemoteRuntimeBaseUrl = state.RemoteRuntimeBaseUrl,
    GithubUsername = state.GithubUsername,
    MaxToolRounds = state.MaxToolRounds,
};
if (state.LlmSelection is not null)
    document.LlmSelection = state.LlmSelection.Clone();
```

In `ProjectionUserConfigQueryPort`, map generated selection to `UserLlmSelectionValue`; a missing document returns `LlmSelection: null` and does not manufacture Gateway. Add the typed overload and use it from `ActorBackedNyxIdUserLlmPreferencesStore`; retain the string overload only for Task 4 migration. Ambient `GetAsync` uses `ForOwnerScope`, explicit binding reads use `ForChannelBinding`.

- [ ] **Step 6: Run tests and guards, then commit**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~UserConfig"
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
git add src/Aevatar.Studio.Application.Abstractions src/Aevatar.Studio.Projection src/Aevatar.Studio.Infrastructure test/Aevatar.Studio.Tests
git commit -m "Project typed user LLM selection"
```

---

### Task 3: NyxID Inventory Identity Provenance

**Files:**
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/LlmCatalog/NyxIdLlmServiceCatalogParser.cs`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmCatalogNormalization.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/LlmCatalog/NyxIdLlmServiceCatalogParser.cs`
- Modify: `src/Aevatar.Studio.Hosting/NyxId/NyxIdLlmCatalogHttpClient.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/LlmSelection/NyxIdLlmServiceCatalogClient.cs`
- Modify: `test/Aevatar.Studio.Tests/NyxIdLlmServiceCatalogUserKeyMergeTests.cs`
- Modify: `test/Aevatar.Studio.Tests/UserConfigControllerSettingsTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/NyxIdLlmServiceCatalogClientTests.cs`

**Interfaces:**
- Produces: `UserLlmServiceIdentity` with `NyxIdUserServicesInventory` authority.
- Produces: `UserLlmOption.Identity`; Task 4 removes the staged legacy generic ID after all consumers migrate.
- Consumes: existing strict `NyxIdApiAccessResponseParser.ParseUserServices`.

- [ ] **Step 1: Write failing provenance and duplicate-slug tests**

Add tests with deliberately conflicting IDs:

```csharp
[Fact]
public void ComposeInventory_ShouldMintOnlyInventoryIdsAndPreserveDuplicateSlugs()
{
    var diagnostics = new NyxIdLlmServicesResult(
        [Diagnostic("key-alpha", "chrono-llm-public")],
        null);
    var inventory = new NyxIdUserServices(
        [
            Inventory("us-alpha", "chrono-llm-public"),
            Inventory("us-beta", "chrono-llm-public"),
        ]);

    var result = NyxIdLlmServiceCatalogParser.ComposeUserServiceInventory(diagnostics, inventory);

    result.Services.Should().HaveCount(2);
    result.Services.Select(x => x.Identity!.NyxIdUserServiceId)
        .Should().Equal("us-alpha", "us-beta");
    result.Services.Should().OnlyContain(x =>
        x.Identity!.Authority == UserLlmIdentityAuthority.NyxIdUserServicesInventory);
    result.Services.Should().NotContain(x => x.Identity!.NyxIdUserServiceId == "key-alpha");
}

private static NyxIdLlmService Diagnostic(string diagnosticId, string slug) => new(
    UserServiceId: diagnosticId,
    ServiceSlug: slug,
    DisplayName: "Chrono LLM",
    RouteValue: $"/api/v1/proxy/s/{slug}",
    DefaultModel: "gpt-5.5",
    Models: ["gpt-5.5"],
    Status: UserLlmRouteStatus.Ready,
    Source: NyxIdLlmProviderSource.ProxyService,
    Allowed: true,
    Description: null)
{
    Identity = null,
};

private static NyxIdUserService Inventory(string id, string slug) => new(
    Id: id,
    Slug: slug,
    Label: $"Inventory {id}",
    CatalogServiceName: "Chrono LLM",
    IsActive: true,
    CredentialSource: new NyxIdUserServiceCredentialSource(
        NyxIdUserServiceCredentialSourceKind.Personal));
```

Update client tests to expect calls to `/api/v1/user-services` and assert a key ID, proxy catalog ID, or `source = user_service` never appears in `Identity.NyxIdUserServiceId` without inventory proof.

- [ ] **Step 2: Run focused tests and verify failure**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~NyxIdLlmServiceCatalog|FullyQualifiedName~UserConfigControllerSettingsTests"
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --filter "FullyQualifiedName~NyxIdLlmServiceCatalogClientTests"
```

Expected: compilation fails because typed identity does not exist; after adding it, behavior fails until inventory composition preserves exact IDs and duplicate slugs.

- [ ] **Step 3: Introduce typed identity provenance**

Use these exact application types:

```csharp
public enum UserLlmIdentityAuthority
{
    Unspecified = 0,
    NyxIdUserServicesInventory = 1,
}

public sealed record UserLlmServiceIdentity(
    UserLlmIdentityAuthority Authority,
    string NyxIdUserServiceId);
```

Append `UserLlmServiceIdentity? Identity = null` as the final optional positional parameter on both `UserLlmOption` and `NyxIdLlmService`, without removing their legacy generic ID properties in this commit; existing constructors therefore remain source-compatible while identity provenance is introduced. `NyxIdLlmServiceMapping.ToOption` copies identity but never creates it from `Source`. Task 4 migrates every backend consumer and then deletes/renames the generic ID properties so they do not survive in final code or wire contracts.

- [ ] **Step 4: Compose one option per eligible inventory ID**

Implement `ComposeUserServiceInventory` with these invariants:

```csharp
private static bool IsEligible(NyxIdUserService service) =>
    service.IsActive &&
    (service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Personal ||
     service.CredentialSource.Kind == NyxIdUserServiceCredentialSourceKind.Organization &&
     service.CredentialSource.Allowed);

private static UserLlmServiceIdentity InventoryIdentity(NyxIdUserService service) =>
    new(UserLlmIdentityAuthority.NyxIdUserServicesInventory, service.Id);
```

Iterate inventory ordered by exact ID, fan out duplicate slugs, derive `/api/v1/proxy/s/{slug}`, and use matching diagnostics only for label/model/status enrichment. Do not pass inventory-backed options through `MergeRouteCandidates` or any route/slug deduplication.

- [ ] **Step 5: Fetch strict inventory in both adapters**

Studio calls `GET /api/v1/user-services`; channel calls `NyxIdApiClient.ListUserServicesAsync`. Parse both through `NyxIdApiAccessResponseParser.ParseUserServices`, reject malformed provider results, and compose inventory after existing diagnostic inputs. Do not cache identity beyond existing bounded HTTP/cache behavior.

- [ ] **Step 6: Run tests and commit**

Run both focused commands from Step 2 and expect PASS, then:

```bash
git add src/Aevatar.Studio.Application.Abstractions src/Aevatar.AI.ToolProviders.NyxId src/Aevatar.Studio.Hosting agents/Aevatar.GAgents.NyxidChat test/Aevatar.Studio.Tests test/Aevatar.GAgents.ChannelRuntime.Tests
git commit -m "Source LLM identity from NyxID inventory"
```

---

### Task 4: Exact-ID Write Boundary And API Contract

**Files:**
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmPreferenceWriteCore.cs`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserLlmContracts.cs`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/UserConfigContracts.cs`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IUserConfigCommandService.cs`
- Modify: `src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/IUserConfigQueryPort.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/UserLlmPreferenceWriter.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/UserConfigService.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/UserLlmPreferenceService.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/UserLlmSettingsViewBuilder.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/StudioOwnerLLMServiceIdentityResolver.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/ChannelUserLlmPreferencePort.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/LlmSelection/DefaultUserLlmSelectionService.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/LlmSelection/DefaultUserLlmOptionsService.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/LlmSelection/UserLlmSelectionContracts.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/LlmSelection/StubNyxIdLlmServiceCatalogClient.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/LlmSelection/TextUserLlmOptionsRenderer.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/Slash/ModelChannelSlashCommandHandler.cs`
- Modify: `agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs`
- Modify: `src/Aevatar.Studio.Hosting/Controllers/UserLlmWireContracts.cs`
- Modify: `src/Aevatar.Studio.Hosting/Controllers/UserConfigController.cs`
- Modify: `src/Aevatar.Studio.Hosting/NyxId/CachedNyxIdLlmCatalogPort.cs`
- Modify: `agents/Aevatar.GAgents.UserConfig/UserConfigGAgent.cs`
- Modify: `src/Aevatar.Studio.Projection/CommandServices/ActorDispatchUserConfigCommandService.cs`
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionUserConfigQueryPort.cs`
- Modify: `test/Aevatar.Studio.Tests/UserConfigServiceTests.cs`
- Modify: `test/Aevatar.Studio.Tests/UserConfigControllerSettingsTests.cs`
- Modify: `test/Aevatar.Studio.Tests/CachedNyxIdLlmCatalogPortTests.cs`
- Modify: `test/Aevatar.Studio.Tests/ProjectionScheduledInvocationAuthorityQueryPortTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ModelSlashCommandHandlerTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ChannelConversationTurnRunnerTests.cs`
- Modify: `test/Aevatar.Capabilities.Tests/NyxIdResponsesModelsAggregatorTests.cs`
- Modify: `test/Aevatar.Capabilities.Tests/StudioUserConfigOwnerLlmConfigSourceTests.cs`
- Modify: `test/Aevatar.GAgentService.Integration.Tests/ScopeWorkflowEndpointsTests.cs`
- Modify: `test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpoints/ScopeServiceEndpointTestKit.cs`

**Interfaces:**
- Consumes: Task 3 identity-bearing options.
- Produces: `SaveUserLlmPreferenceCommand.UserServiceId` and JSON `userServiceId`.
- Produces: settings fields `savedRouteKind`, `savedUserServiceId`, and `savedServiceSlug`.

- [ ] **Step 1: Write failing no-read-merge and exact-ID tests**

Add these behavioral tests with recording command and catalog ports; the desired writer constructor has no query port:

```csharp
[Fact]
public async Task SaveLlmPreferenceAsync_WithExactInventoryId_ShouldDispatchDeltaWithoutReadingConfig()
{
    var commands = new RecordingUserConfigCommandService();
    var writer = CreateWriter(commands, InventoryService("us-beta", "shared-slug"));

    await writer.SaveAsync(
        UserConfigResourceKey.ForOwnerScope("scope-alpha"),
        "bearer",
        new SaveUserLlmPreferenceCommand(UserServiceId: "us-beta", Model: "gpt-5.5"),
        CancellationToken.None);

    var update = commands.Updates.Should().ContainSingle().Which.Update;
    update.LlmSelection!.NyxIdUserServiceId.Should().Be("us-beta");
    update.LlmSelection.ServiceSlugSnapshot.Should().Be("shared-slug");
    update.DefaultModel.Should().Be("gpt-5.5");
}

[Fact]
public async Task SaveLlmPreferenceAsync_WithRouteOnlyProxy_ShouldRejectWithoutDispatch()
{
    var commands = new RecordingUserConfigCommandService();
    var writer = CreateWriter(commands, InventoryService("us-alpha", "shared"));

    var act = () => writer.SaveAsync(
        UserConfigResourceKey.ForOwnerScope("scope-alpha"),
        "bearer",
        new SaveUserLlmPreferenceCommand(RouteValue: "/api/v1/proxy/s/shared"),
        CancellationToken.None);

    await act.Should().ThrowAsync<InvalidOperationException>()
        .WithMessage("userServiceId is required for a NyxID service selection.");
    commands.Updates.Should().BeEmpty();
}

private static UserLlmPreferenceWriter CreateWriter(
    RecordingUserConfigCommandService commands,
    params NyxIdLlmService[] services) =>
    new(commands, new StubUserLlmCatalogPort(new NyxIdLlmServicesResult(
        services,
        null)));

private static NyxIdLlmService InventoryService(string id, string slug) => new(
    CatalogEntryId: null,
    ServiceSlug: slug,
    DisplayName: slug,
    RouteValue: $"/api/v1/proxy/s/{slug}",
    DefaultModel: "gpt-5.5",
    Models: ["gpt-5.5"],
    Status: UserLlmRouteStatus.Ready,
    Source: UserLlmRouteSource.UserService,
    Allowed: true,
    Description: null,
    Identity: new UserLlmServiceIdentity(
        UserLlmIdentityAuthority.NyxIdUserServicesInventory,
        id));

private sealed class StubUserLlmCatalogPort(NyxIdLlmServicesResult result) : IUserLlmCatalogPort
{
    public Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct) =>
        Task.FromResult(result);

    public Task<NyxIdLlmService> ProvisionAsync(
        string bearerToken,
        string provisionEndpointId,
        CancellationToken ct) =>
        throw new NotSupportedException();
}

private sealed class RecordingUserConfigCommandService : IUserConfigCommandService
{
    public List<(UserConfigResourceKey Resource, UserConfigUpdate Update)> Updates { get; } = [];

    public Task<UserConfigSaveReceipt> UpdateAsync(
        UserConfigResourceKey resource,
        UserConfigUpdate update,
        CancellationToken ct = default)
    {
        Updates.Add((resource, update));
        return Task.FromResult(new UserConfigSaveReceipt(
            true,
            "command-alpha",
            UserConfigCommandAckStage.Accepted,
            resource.Value,
            "correlation-alpha",
            DateTimeOffset.UnixEpoch));
    }
}
```

Cover duplicate slug exact-ID selection, diagnostic-only ID rejection, Gateway aliases without catalog calls, model-only omission of selection, prefixed-model rejection, reset to Gateway, generic PUT without route, and binding-scoped channel writes. Add service and controller tests named `SaveAsync_WithRoutePrefixedDefaultModel_ShouldRejectWithoutDispatch` and `Save_WithRoutePrefixedDefaultModel_ShouldReturnBadRequest`; use `chrono-llm-public/gpt-5.5` and assert no delta dispatch.

Add `GetSettingsAsync_WithDuplicateRoute_ShouldPreserveExactInventoryChoices` with `us-alpha` and `us-beta` sharing one route; assert both `UserServiceId` values are returned. Add `GetSettingsAsync_WhenSavedIdDisappearsButSameRouteRemains_ShouldMarkSavedSelectionUnavailable`; assert `us-alpha` on the same route does not make missing `us-beta` look available.

- [ ] **Step 2: Run focused tests and verify failure**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~UserConfigServiceTests|FullyQualifiedName~UserConfigControllerSettingsTests"
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo --filter "FullyQualifiedName~ModelSlashCommandHandlerTests"
```

Expected: old route-based writes read UserConfig and accept non-exact matches.

- [ ] **Step 3: Replace broad matching in authoritative writes**

Use exact ordinal identity matching only:

```csharp
private static UserLlmOption RequireInventoryOption(
    IReadOnlyList<UserLlmOption> options,
    string userServiceId)
{
    var id = userServiceId.Trim();
    var matches = options.Where(option =>
        option.Identity is
        {
            Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory,
        } identity &&
        string.Equals(identity.NyxIdUserServiceId, id, StringComparison.Ordinal)).ToArray();
    return matches.Length == 1
        ? matches[0]
        : throw new InvalidOperationException($"LLM user service '{id}' is not selectable.");
}
```

Remove `UserLlmPreferenceWriter._queryPort` and every write-side `GetAsync`. Build `UserConfigUpdate` values directly. Model-only commands omit `LlmSelection`; route-prefixed model-only commands reject. Gateway/reset writes the canonical typed selection without loading catalog. Provisioning reloads inventory and requires the returned exact ID there before dispatch.

After migrating backend consumers, remove `UserLlmOption.ServiceId`. Rename `NyxIdLlmService.UserServiceId` to nullable `CatalogEntryId`; only `Identity.NyxIdUserServiceId` carries an authoritative user-service ID. Update all remaining constructors and tests in the affected projects in the same commit so the repository never ends a task with a dual-meaning ID field.

The #2912 resolver still exists until Task 5, so migrate its catalog access to `service.Identity?.NyxIdUserServiceId` in this commit solely to keep the staged tree buildable; do not add fallback behavior or preserve the generic field for it. Update its `ProjectionScheduledInvocationAuthorityQueryPortTests` catalog fixtures to the renamed diagnostic field. Task 5 deletes the resolver, interface, and those resolver-specific tests immediately afterward.

Rename `UseExistingService.ServiceId` to `UserServiceId`. The external setup-hint adapter may read NyxID's boundary field `service_id`, but immediately maps it into the precisely named internal field. Preset activation exact-matches that ID against one inventory-backed option; a catalog-only preset ID fails before dispatch. Rename `UseExistingServiceResponse.serviceId` to JSON `userServiceId`.

- [ ] **Step 4: Make all command paths typed and delta-based**

`UserConfigService` resolves `ForOwnerScope` from `IAppScopeResolver` and dispatches only specified normalized fields. Before dispatching generic `DefaultModel`, it rejects any value for which `UserConfigLlmModel.TryParseRouteModel` succeeds; a route-prefixed model must go through `/api/user-config/llm` with exact `userServiceId`. `ChannelUserLlmPreferencePort` resolves `ForChannelBinding`; rename its string parameters from `scopeId` to `bindingId`. `DefaultUserLlmSelectionService` selects exact inventory identity and never calls broad `FindOption` for a save.

After all callers use typed resource keys and `UserConfigUpdate`, delete the old `IUserConfigCommandService.SaveAsync`/`SaveGithubUsernameAsync` methods, the string `IUserConfigQueryPort.GetAsync` overload, their adapter implementations, and the actor's `UserConfigUpdatedEvent` event-handler entry point. Keep the domain event and legacy transition for replay. Remove `PreferredLlmRoute` from `SaveUserConfigCommand`; the read-side `UserConfig.PreferredLlmRoute` compatibility view remains.

Update every command/query port fake in `UserConfigServiceTests`, `UserConfigControllerSettingsTests`, `ModelSlashCommandHandlerTests`, `StudioUserConfigOwnerLlmConfigSourceTests`, `ScopeWorkflowEndpointsTests`, and `ScopeServiceEndpointTestKit` to implement only the final typed surface. Owner fixtures use `ForOwnerScope`; channel fixtures use `ForChannelBinding`. Delete the temporary default interface implementations introduced in Task 2.

- [ ] **Step 5: Update HTTP and settings-view contracts**

Use:

```csharp
public sealed record SaveUserLlmSettingsRequest(
    [property: JsonPropertyName("userServiceId")] string? UserServiceId = null,
    [property: JsonPropertyName("routeValue")] string? RouteValue = null,
    [property: JsonPropertyName("model")] string? Model = null);
```

Add `savedRouteKind`, `savedUserServiceId`, and `savedServiceSlug` to the response. Rename option response identity to `userServiceId`; populate it only from inventory identity. Remove `preferredLlmRoute` from generic PUT. Reject conflicting `userServiceId + Gateway`, route-only proxy, unknown ID, and an empty command.

Remove `UserLlmSettingsViewBuilder` route-based deduplication for identity-bearing options. Preserve one route option per `Identity.NyxIdUserServiceId`, deduplicate catalog-only diagnostics separately, and resolve saved availability/labels by `UserConfig.LlmSelection.NyxIdUserServiceId`. Route equality is used only for model-group enrichment and display, never to decide which saved service exists.

- [ ] **Step 6: Run tests and commit**

Run both focused commands from Step 2 and expect PASS, then:

```bash
dotnet build aevatar.slnx --nologo
git add src/Aevatar.Studio.Application.Abstractions src/Aevatar.AI.ToolProviders.NyxId src/Aevatar.Studio.Application src/Aevatar.Studio.Hosting src/Aevatar.Studio.Projection agents/Aevatar.GAgents.UserConfig agents/Aevatar.GAgents.NyxidChat test/Aevatar.Studio.Tests test/Aevatar.GAgents.ChannelRuntime.Tests test/Aevatar.Capabilities.Tests test/Aevatar.GAgentService.Integration.Tests
git commit -m "Persist exact owner LLM identity"
```

---

### Task 5: Pure Read-Model Authorization And Explicit Gateway

**Files:**
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionScheduledInvocationAuthorityQueryPorts.cs`
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs`
- Modify: `src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationPlanner.cs`
- Delete: `src/Aevatar.Studio.Application/Studio/Services/StudioOwnerLLMServiceIdentityResolver.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/Aevatar.Studio.Hosting/StudioHostingServiceCollectionExtensions.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/StudioUserConfigOwnerLlmConfigSource.cs`
- Modify: `src/Aevatar.AI.LLMProviders.NyxId/NyxIdLLMProvider.cs`
- Modify: `tools/ci/query_projection_priming_guard.sh`
- Modify: `test/Aevatar.Studio.Tests/ProjectionScheduledInvocationAuthorityQueryPortTests.cs`
- Modify: `test/Aevatar.GAgentService.Tests/Authorization/ScheduledInvocationAuthorizationPlannerTests.cs`
- Modify: `test/Aevatar.GAgents.ChannelRuntime.Tests/ScheduledAgentCreatorToolTests.cs`
- Modify: `test/Aevatar.Capabilities.Tests/StudioUserConfigOwnerLlmConfigSourceTests.cs`
- Modify: `test/Aevatar.AI.Tests/NyxIdLLMProviderRoutingTests.cs`

**Interfaces:**
- Consumes: projected Task 2 selection.
- Produces: exact `ScheduledInvocationOwnerLLMEvidence` from read model only.
- Removes: `IScheduledInvocationOwnerLLMServiceIdentityResolver` and planner fallback.

- [ ] **Step 1: Write failing query, planner, and Gateway regression tests**

Add query tests for exact service, typed Gateway, legacy route-only failure, missing document, and binding-scope isolation. Add a theory where `LlmSelection` is absent and legacy `PreferredLlmRoute` is empty, `gateway`, or `/api/v1/llm/gateway/v1`; every case must return fail-closed/unspecified evidence, never `NotRequired`. Only typed `LlmSelection.Kind == Gateway` may waive a service grant. Add:

```csharp
[Fact]
public async Task ResolveRouteAsync_ExplicitGateway_ShouldBeatConfiguredProxyDefault()
{
    var provider = CreateProviderWithDefaultRoute("chrono-llm-public");

    var route = await provider.ResolveRouteAsync(
        CreateRequest(routePreference: "/api/v1/llm/gateway/v1"));

    route.RouteName.Should().Be("/api/v1/llm/gateway/v1");
    route.Endpoint.Should().Be(new Uri("https://nyx.example.com/api/v1/llm/gateway/v1"));
}
```

Delete the planner test that expects missing ID to be repaired by resolver; strengthen the stable fail-closed test.

- [ ] **Step 2: Run focused tests and verify failure**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~ProjectionScheduledInvocationAuthorityQueryPortTests"
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "FullyQualifiedName~ScheduledInvocationAuthorizationPlannerTests"
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter "FullyQualifiedName~NyxIdLLMProviderRoutingTests"
```

Expected: owner query still infers slug/default route and planner still invokes live resolver.

- [ ] **Step 3: Map only typed read-model evidence**

`ProjectionScheduledInvocationOwnerLLMQueryPort` reads only `user-config-{scopeId}`. Typed Gateway returns `NotRequired`; typed `NyxIdUserService` returns exact ID/slug/route; legacy proxy returns `Required` with empty ID so the planner emits the stable exact-identity failure; legacy Gateway-shaped or unspecified state returns fail-closed `Unspecified` evidence. Missing document returns null. Remove `ScheduledInvocationOwnerLLMRouteOptions` and host-default fallback. Never elevate compatibility `PreferredLlmRoute` to typed Gateway authority.

- [ ] **Step 4: Remove query-time resolver completely**

Delete its file, interface, planner field/constructor/fallback, DI registration, unavailable stub, test doubles, owner-context query parameter, and `NyxIdRoute` fallback property. Keep stable `owner_llm_exact_service_identity_unavailable` when required evidence lacks exact ID. Preserve #2912 binding code.

- [ ] **Step 5: Make Gateway explicit in runtime**

Treat `gateway` and `/api/v1/llm/gateway/v1` as explicit canonical Gateway; only null/absent preference may use configured `DefaultRoute`. `StudioUserConfigOwnerLlmConfigSource` returns canonical Gateway only when `UserConfig.LlmSelection.Kind == Gateway`; when selection is null/unspecified it returns a null route and leaves the interactive host-default path intact, regardless of the compatibility route view. Add both source tests.

- [ ] **Step 6: Harden the architecture guard**

Extend `query_projection_priming_guard.sh` to fail if the deleted resolver type returns or if the owner query/planner references `IUserLlmCatalogPort`, `GetServicesAsync`, or token issuance.

- [ ] **Step 7: Run tests, guard, and commit**

Run all Step 2 commands plus:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
bash tools/ci/query_projection_priming_guard.sh
git add src test tools/ci/query_projection_priming_guard.sh
git commit -m "Read owner LLM identity from projection"
```

---

### Task 6: Preserve Verified Caller Binding With Agent Key Credential

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Schedules/ScheduledDispatchModels.cs`
- Modify: `src/platform/Aevatar.GAgentService.Application/Schedules/ScheduledDispatchApplicationService.cs`
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/scheduled_dispatch_state.proto`
- Modify: `src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs`
- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/Schedules/ScheduledDispatchActorPort.cs`
- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/Schedules/ScheduledServiceInvocationDispatchPort.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs`
- Modify: `test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs`
- Modify: `test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchServiceInvocationTests.cs`
- Modify: `test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchApplicationServiceTests.cs`
- Modify: `test/Aevatar.GAgentService.Integration.Tests/ScheduledDispatchEndpointsTests.cs`
- Modify: `test/Aevatar.Workflow.Core.Tests/WorkflowCallerCredentialToolContextTests.cs`

**Interfaces:**
- Produces: `ScheduledServiceInvocationAuth.CallerAuthority`, separate from credential source.
- Preserves: exact verified binding into `ScheduledCallerNyxIdAuthority.BindingId` and workflow tool context.

- [ ] **Step 1: Write failing end-to-end binding tests**

Add a Studio schedule test with distinct IDs and exact binding:

```csharp
request.AuthenticatedOwner.VerifiedBindingId.Should().Be("bnd-owner-alpha");
var configuration = scheduleService.Created.Should().ContainSingle().Which.Configuration;
configuration.Target.ServiceInvocation!.Auth!.Source
    .Should().BeOfType<ScheduledInvocationAgentKeyCredentialReference>();
configuration.Target.ServiceInvocation.Auth.CallerAuthority!.BindingId
    .Should().Be("bnd-owner-alpha");
```

Add a scheduled fire test proving an Agent Key source still projects `bnd-owner-alpha` into `ChatRequestEvent.CallerDurableCredential.ScheduledCallerNyxIdAuthority.BindingId`. Existing workflow mapper tests then assert the same value in `WorkflowCallerNyxIdAuthority` and tool context.

Add `NormalizeConfiguration_AgentKey_ShouldPreserveCallerAuthority` to `ScheduledDispatchApplicationServiceTests`; pass an Agent Key source plus caller authority and assert the normalized configuration retains an equivalent cloned authority before actor dispatch.

- [ ] **Step 2: Run focused tests and verify failure**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter "FullyQualifiedName~StudioMemberWorkflowSchedulePortTests"
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "FullyQualifiedName~ScheduledDispatchServiceInvocationTests|FullyQualifiedName~ScheduledDispatchApplicationServiceTests"
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter "FullyQualifiedName~WorkflowCallerCredentialToolContextTests"
```

Expected: Agent Key auth has no committed caller authority and binding becomes empty.

- [ ] **Step 3: Separate caller authority from credential source**

Add `ScheduledCallerNyxIdAuthority? CallerAuthority` to `ScheduledServiceInvocationAuth`. Add field 8 to state, outside the credential oneof:

```proto
message ScheduledServiceInvocationAuthState {
  ScheduledServiceInvocationNyxIdCredentialSourceState sender_nyx_id = 1 [deprecated = true];
  string durable_sender_bearer_token = 2 [deprecated = true];
  ScheduledServiceInvocationScopeOwnerNyxIdCredentialSourceState scope_owner_nyx_id = 3 [deprecated = true];
  bool legacy_durable_sender_bearer_blocked = 4;
  oneof source {
    ScheduledServiceInvocationNyxIdCredentialSourceState nyx_id = 5;
    ScheduledServiceInvocationDurableCredentialReferenceState durable = 6;
    ScheduledInvocationAgentKeyCredentialReferenceState scheduled_invocation_agent_key = 7;
  }
  aevatar.credentials.ScheduledCallerNyxIdAuthority caller_authority = 8;
}
```

Clone and normalize caller authority in `ScheduledDispatchApplicationService.NormalizeServiceInvocationAuth`, actor-port mapping, actor normalization, replacement-pending credential selection, and runtime mapping regardless of source kind. Every reconstruction of `ScheduledServiceInvocationAuth` must copy `CallerAuthority`; no credential-source branch may drop it.

- [ ] **Step 4: Build authority only from verified owner context**

Change Studio `BuildScheduleAuth` to accept `AuthenticatedAuthorizationOwnerContext` and build:

```csharp
new ScheduledCallerNyxIdAuthority
{
    Platform = owner.SubjectPlatform.Trim(),
    Tenant = owner.SubjectTenant?.Trim() ?? string.Empty,
    ExternalUserId = owner.SubjectExternalUserId.Trim(),
    Scope = "proxy",
    BindingId = owner.VerifiedBindingId.Trim(),
}
```

Fail closed on missing verified binding. Do not derive it from subject or scope. `ScheduledServiceInvocationDispatchPort` uses committed `CallerAuthority` when projecting workflow caller credentials, including Agent Key runs.

- [ ] **Step 5: Run tests and binding guard, then commit**

Run Step 2 commands plus:

```bash
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ScheduledDispatchEndpointsTests"
bash tools/ci/workflow_binding_boundary_guard.sh
git add src test
git commit -m "Preserve scheduled caller binding with agent key"
```

---

### Task 7: Console And Relay Exact Selection UI

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/studio/models.ts`
- Modify: `apps/aevatar-console-web/src/shared/studio/api.ts`
- Modify: `apps/aevatar-console-web/src/shared/studio/api.test.ts`
- Create: `apps/aevatar-console-web/src/pages/settings/userLlmSelection.ts`
- Create: `apps/aevatar-console-web/src/pages/settings/userLlmSelection.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/settings/index.tsx`
- Modify: `apps/aevatar-console-web/src/pages/settings/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatConversationConfig.ts`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatConversationConfig.test.ts`
- Modify: `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html`

**Interfaces:**
- Consumes: Task 4 JSON `userServiceId` and saved typed identity fields.
- Produces: settings draft keyed by Gateway or exact user-service ID, never route.

- [ ] **Step 1: Write failing decoder and selection tests**

Define exact UI identity:

```ts
export type UserLlmSelectionDraft =
  | { readonly kind: 'gateway'; readonly routeValue: string }
  | {
      readonly kind: 'nyx_id_user_service';
      readonly userServiceId: string;
      readonly routeValue: string;
    };
```

Add tests proving two options with the same route but `us-alpha` and `us-beta` remain distinct, diagnostics without `userServiceId` are not selectable, and saving `us-beta` sends `{ userServiceId: 'us-beta', model }`.

- [ ] **Step 2: Run frontend tests and verify failure**

```bash
pnpm --dir apps/aevatar-console-web test --runInBand --runTestsByPath src/shared/studio/api.test.ts src/pages/settings/userLlmSelection.test.ts src/pages/settings/index.test.tsx src/pages/chat/chatConversationConfig.test.ts
```

Expected: decoder and settings still use route plus generic `serviceId`.

- [ ] **Step 3: Update TypeScript contracts and API decoder**

Add `savedRouteKind`, `savedUserServiceId`, `savedServiceSlug`; rename route option `serviceId` to `userServiceId`. Save with:

```ts
saveUserLlmSettings(input: {
  userServiceId?: string | null;
  routeValue?: string | null;
  model?: string | null;
}): Promise<StudioUserConfigSaveReceipt> {
  return requestDecodedJson(
    "/api/user-config/llm",
    decodeStudioUserConfigSaveReceipt,
    {
      method: "PUT",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        userServiceId: trimOptional(input.userServiceId),
        routeValue: input.routeValue?.trim() ?? null,
        model: input.model?.trim() ?? "",
      }),
    }
  );
}
```

- [ ] **Step 4: Make Settings selection ID-based without changing route-based chat semantics**

Create focused helpers that encode select values as `gateway` or `user-service:{encodeURIComponent(id)}`. Store `UserLlmSelectionDraft` in Settings; use its route only for model groups and runtime preview. Do not use `buildConversationRouteOptions` or route equality to decide persisted selection. Keep conversation-level route overrides route-based, but change the explicit Gateway constant to `/api/v1/llm/gateway/v1`.

- [ ] **Step 5: Update the relay settings page**

In `channels.html`, retain `userServiceId` with each option, exclude diagnostics without it from service selection, keep a dedicated Gateway option, and send either `{ userServiceId, model }` or `{ routeValue: "/api/v1/llm/gateway/v1", model }`.

- [ ] **Step 6: Run frontend verification and commit**

```bash
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web test --runInBand
pnpm --dir apps/aevatar-console-web build
git add apps/aevatar-console-web agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html
git commit -m "Select owner LLM service by exact ID"
```

---

### Task 8: Full Verification, Integration Push, And Production Canary

**Files:**
- Modify only if a failing verification reveals a task-scoped defect.
- Verify: `docs/superpowers/specs/2026-07-22-owner-llm-exact-service-identity-design.md`
- Verify: `docs/superpowers/plans/2026-07-22-owner-llm-exact-service-identity.md`

**Interfaces:**
- Consumes: Tasks 1-7.
- Produces: verified commits on `origin/feature/integrate` and production acceptance evidence with no secrets.

- [ ] **Step 1: Run focused and full backend tests**

```bash
dotnet build aevatar.slnx --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo
```

Expected: all commands exit 0 with no failed tests.

- [ ] **Step 2: Run all required guards and frontend checks**

```bash
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web test --runInBand
pnpm --dir apps/aevatar-console-web build
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
git diff --check
```

Expected: every guard and frontend command exits 0.

- [ ] **Step 3: Request independent final code review**

Review for architecture compliance, lost-update risk, ID provenance, secret leakage, exact binding propagation, and test gaps. Task-level subagent reviews must already have resolved findings before this gate; expected result is no blocker/high finding and no new worktree changes.

- [ ] **Step 4: Confirm the reviewed branch is clean**

```bash
git status --short
git log -8 --oneline
```

Expected: `git status --short` prints nothing and the log contains the design, plan, and Tasks 1-7 commits.

- [ ] **Step 5: Integrate onto the latest remote branch and push**

```bash
git fetch origin feature/integrate
git rebase origin/feature/integrate
git push origin HEAD:feature/integrate
```

Expected: fast-forward push succeeds. Re-run the full verification set if rebase changes task-owned files.

- [ ] **Step 6: Wait for deployment, then run the production canary**

Using non-secret output only:

1. Read the current model and legacy route.
2. Reselect `chrono-llm-public` by exact production `UserService.id`.
3. Confirm UserConfig read model contains the same route and exact ID.
4. Create temporary Team/member/workflow fixtures with distinct `teamId`, `memberId`, `workflowId`, and `publishedServiceId`.
5. Refresh the NyxID authorization catalog and confirm ready/observed/ready.
6. Confirm preflight emits exact constrained grants with both `allow_all_*` flags false.
7. Create the automation and confirm credential source is `scheduled_invocation_agent_key`.
8. Run now and confirm a completed successful `simple_qa` workflow execution.
9. Delete the automation and confirm the exact key and vault secret are revoked.
10. Retire the revision, delete the member, archive the Team, and verify no temporary resources remain.

Never print the bearer token, Agent Key value, or vault ciphertext.
