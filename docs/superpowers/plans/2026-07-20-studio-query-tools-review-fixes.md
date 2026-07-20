# Studio Query Tools Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the `feature/integrate -> dev` candidate production-ready by adding missing workflow catalog/detail tools and correcting Studio schedule query ownership, dependency, URL, and wire semantics.

**Architecture:** Workflow catalog tools live in the Workflow tool provider and depend only on `IWorkflowCatalogPort`. Studio schedule tools depend only on a new `IStudioMemberAutomationQueryPort`; the existing Studio schedule service implements both the narrow query interface and its mutation interface, with DI aliasing both contracts to one singleton.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, Microsoft.Extensions.DependencyInjection, System.Text.Json, repository shell architecture guards.

## Global Constraints

- Keep `apps/aevatar-console-web` byte-for-byte identical to `origin/dev`.
- Query tools use typed read-side ports only. No HTTP self-calls, actor reads, event replay, query-time projection priming, or process-local fact maps.
- Keep `scopeId`, `teamId`, `memberId`, `workflowName`, `workflowId`, `publishedServiceId`, and `scheduleId` as separate identities.
- Workflow tools query the global runnable catalog and must not claim to query Studio member drafts.
- Schedule ownership is `scope -> team -> member`; caller scope comes only from `AgentToolRequestContext`.
- Use TDD for every behavior change and run `bash tools/ci/test_stability_guards.sh` after modifying tests.
- Do not force-push `feature/integrate` without explicit user authorization; use `--force-with-lease` if authorization is granted.

---

### Task 1: Add The Narrow Studio Automation Query Boundary

**Files:**
- Modify: `src/Aevatar.Studio.Application.Abstractions/Provisioning/IStudioMemberWorkflowSchedulePort.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `test/Aevatar.Studio.Tests/StudioApplicationServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Produces: the exact `IStudioMemberAutomationQueryPort` signatures shown in Step 3.
- Changes: `IStudioMemberWorkflowSchedulePort : IStudioMemberAutomationQueryPort`.
- Produces: singleton DI aliases for query and mutation interfaces.

- [ ] **Step 1: Write the failing DI boundary test**

Add `using Aevatar.Studio.Application.Provisioning;` and this test:

```csharp
[Fact]
public void AddStudioApplication_ShouldAliasAutomationQueryAndMutationPortsToOneSingleton()
{
    var services = new ServiceCollection();
    services.AddStudioApplication();

    services.Should().ContainSingle(x =>
        x.ServiceType == typeof(StudioMemberWorkflowSchedulePort) &&
        x.Lifetime == ServiceLifetime.Singleton);
    services.Should().ContainSingle(x =>
        x.ServiceType == typeof(IStudioMemberAutomationQueryPort) &&
        x.Lifetime == ServiceLifetime.Singleton);
    services.Should().ContainSingle(x =>
        x.ServiceType == typeof(IStudioMemberWorkflowSchedulePort) &&
        x.Lifetime == ServiceLifetime.Singleton);
}
```

- [ ] **Step 2: Run the test and verify RED**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo \
  --filter FullyQualifiedName~StudioApplicationServiceCollectionExtensionsTests
```

Expected: compilation/test failure because the narrow interface and concrete singleton registration do not exist.

- [ ] **Step 3: Add the narrow interface and inherit it from the mutation contract**

```csharp
public interface IStudioMemberAutomationQueryPort
{
    Task<StudioMemberAutomationListResponse> ListAsync(
        string scopeId,
        string teamId,
        string memberId,
        int take = 50,
        string? cursor = null,
        bool includeTotalCount = false,
        CancellationToken ct = default);

    Task<StudioMemberAutomationView?> GetAsync(
        string scopeId,
        string teamId,
        string memberId,
        string scheduleId,
        CancellationToken ct = default);
}

public interface IStudioMemberWorkflowSchedulePort : IStudioMemberAutomationQueryPort
{
    Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync(
        StudioMemberWorkflowScheduleRequest request,
        CancellationToken ct = default);

    Task<StudioMemberWorkflowScheduleResult> CreateAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct = default);

    Task<StudioMemberWorkflowScheduleResult> ReauthorizeAsync(
        StudioMemberWorkflowScheduleRequest request,
        string confirmedPermissionDigest,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> UpdateAsync(
        StudioMemberAutomationUpdateCommand command,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> PauseAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> ResumeAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> RunNowAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> DeleteAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default);

    Task<StudioMemberAutomationMutationReceipt> RetryRevocationAsync(
        StudioMemberAutomationActionCommand command,
        CancellationToken ct = default) =>
        throw new NotSupportedException();
}
```

- [ ] **Step 4: Alias both interfaces to one implementation**

```csharp
services.TryAddSingleton<StudioMemberWorkflowSchedulePort>();
services.TryAddSingleton<IStudioMemberWorkflowSchedulePort>(provider =>
    provider.GetRequiredService<StudioMemberWorkflowSchedulePort>());
services.TryAddSingleton<IStudioMemberAutomationQueryPort>(provider =>
    provider.GetRequiredService<IStudioMemberWorkflowSchedulePort>());
```

The query alias resolves through the mutation interface so a host-provided
`IStudioMemberWorkflowSchedulePort` override remains authoritative.

- [ ] **Step 5: Run the focused test and verify GREEN**

Run Step 2 again. Expected: all selected tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Aevatar.Studio.Application.Abstractions/Provisioning/IStudioMemberWorkflowSchedulePort.cs \
  src/Aevatar.Studio.Application/Studio/DependencyInjection/ServiceCollectionExtensions.cs \
  test/Aevatar.Studio.Tests/StudioApplicationServiceCollectionExtensionsTests.cs
git commit -m "Narrow Studio automation queries"
```

---

### Task 2: Correct The Studio Schedule Tool Contract

**Files:**
- Modify: `src/Aevatar.AI.ToolProviders.StudioProvisioning/StudioQueryToolSources.cs`
- Modify: `src/Aevatar.AI.ToolProviders.StudioProvisioning/StudioQueryTools.cs`
- Modify: `test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/ProvisionWorkflowScheduleToolTests.cs`

**Interfaces:**
- Consumes: `IStudioMemberAutomationQueryPort` from Task 1.
- Keeps: public source name `StudioScheduleQueryToolSource`.
- Produces: list/get tools requiring the full Team member ownership path.

- [ ] **Step 1: Rewrite schedule tests around the narrow port**

Replace the query uses of `RecordingScheduledDispatchApplicationService` with a fixture implementing `IStudioMemberAutomationQueryPort`. It records `scopeId`, `teamId`, `memberId`, `scheduleId`, paging arguments, and returns `StudioMemberAutomationView` values.

The list call and wire assertions must be:

```csharp
var output = await tool.ExecuteAsync("""
    {
      "team_id": "team-alpha",
      "member_id": "m-alpha",
      "page_size": 15,
      "page_token": "page-4",
      "include_total_count": true
    }
    """);

port.LastScopeId.Should().Be("scope-current");
port.LastTeamId.Should().Be("team-alpha");
port.LastMemberId.Should().Be("m-alpha");

using var document = JsonDocument.Parse(output);
var schedule = document.RootElement.GetProperty("schedules")[0];
schedule.GetProperty("authorization_status").GetString().Should().Be("active");
schedule.GetProperty("schedule_url").GetString().Should().Be(
    "/api/scopes/scope-current/teams/team-alpha/members/m-alpha/automations/sched-alpha");
schedule.TryGetProperty("team_automation_lifecycle_status", out _).Should().BeFalse();
```

Add these exact cases:

- `ListSchedules_WhenTeamIdMissing_ShouldReturnInvalidArgumentsAndNotCallPort`
- `ListSchedules_WhenMemberIdMissing_ShouldReturnInvalidArgumentsAndNotCallPort`
- `ScheduleQueryTools_WhenModelSuppliesScope_ShouldRejectUnknownArgumentAndNotCallPort`
- `ScheduleQueryTools_WhenScopeMissing_ShouldReturnStructuredErrorAndNotCallPort`
- `GetSchedule_WhenMissing_ShouldReturnStructuredNotFound`
- `ScheduleQueryTools_WhenCanceled_ShouldRethrowCancellation`
- `ScheduleQueryTools_WhenProviderFails_ShouldReturnSafeStructuredError`

- [ ] **Step 2: Run the suite and verify RED**

```bash
dotnet test test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/Aevatar.AI.ToolProviders.StudioProvisioning.Tests.csproj --nologo
```

Expected: failures because the source still requires the broad service, `team_id` is rejected, the URL is generic, and the enum is numeric.

- [ ] **Step 3: Narrow the source and tool dependencies**

```csharp
private readonly IStudioMemberAutomationQueryPort? _schedules;

public StudioScheduleQueryToolSource(IStudioMemberAutomationQueryPort? schedules = null)
{
    _schedules = schedules;
}
```

Apply the same type change to both schedule tools. Keep discovery conditional.

- [ ] **Step 4: Require exact ownership arguments and call only the query port**

Allowed argument sets:

```csharp
["team_id", "member_id", "page_size", "page_token", "include_total_count"]
["team_id", "member_id", "schedule_id"]
```

Calls:

```csharp
var result = await _schedules.ListAsync(
    scopeId, teamId, memberId,
    args?.PageSize ?? 50,
    StudioQueryToolJson.Normalize(args?.PageToken),
    args?.IncludeTotalCount ?? false,
    ct);

var schedule = await _schedules.GetAsync(
    scopeId, teamId, memberId, scheduleId, ct);
```

- [ ] **Step 5: Map `StudioMemberAutomationView` to a stable tool DTO**

Use this complete DTO shape so the runtime enum and platform-only fields cannot
leak back into the wire contract:

```csharp
internal sealed record StudioScheduleResultJson(
    string ScopeId,
    string TeamId,
    string MemberId,
    string ScheduleId,
    string PublishedServiceId,
    string DisplayName,
    string Prompt,
    string ScheduleCron,
    string ScheduleTimezone,
    bool Enabled,
    string AuthorizationStatus,
    DateTimeOffset? CredentialExpiresAtUtc,
    string LastAuthorizationErrorCode,
    string OperationId,
    long CredentialGeneration,
    bool RevocationPending,
    DateTimeOffset? NextFireAt,
    DateTimeOffset? LastFireAt,
    long StateVersion,
    string CredentialSourceKind,
    DateTimeOffset UpdatedAt,
    string ScheduleUrl);
```

Map every constructor value directly from the same-named
`StudioMemberAutomationView` property. Build `ScheduleUrl` with:

```csharp
$"/api/scopes/{Uri.EscapeDataString(item.ScopeId)}/teams/{Uri.EscapeDataString(item.TeamId)}/members/{Uri.EscapeDataString(item.MemberId)}/automations/{Uri.EscapeDataString(item.ScheduleId)}"
```

Delete the direct `TeamAutomationLifecycleStatus`, `PermissionDigest`,
`PolicyVersion`, and `RecentFires` output members.

- [ ] **Step 6: Run the suite and verify GREEN**

Run Step 2 again. Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Aevatar.AI.ToolProviders.StudioProvisioning/StudioQueryToolSources.cs \
  src/Aevatar.AI.ToolProviders.StudioProvisioning/StudioQueryTools.cs \
  test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/ProvisionWorkflowScheduleToolTests.cs
git commit -m "Correct Studio schedule queries"
```

---

### Task 3: Add Workflow Catalog And Detail Tools

**Files:**
- Create: `src/Aevatar.AI.ToolProviders.Workflow/WorkflowCatalogAgentToolSource.cs`
- Create: `src/Aevatar.AI.ToolProviders.Workflow/Tools/AevatarWorkflowCatalogTools.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Workflow/ServiceCollectionExtensions.cs`
- Create: `test/Aevatar.AI.ToolProviders.Workflow.Tests/WorkflowCatalogToolsTests.cs`

**Interfaces:**
- Consumes: `IWorkflowCatalogPort.ListWorkflowCatalogAsync` and `GetWorkflowDetailAsync`.
- Produces: public `WorkflowCatalogAgentToolSource`.
- Produces: `aevatar_list_workflows` and `aevatar_get_workflow`.

- [ ] **Step 1: Write discovery and wire-contract tests**

Create a recording `IWorkflowCatalogPort` and these exact tests:

- `Source_WithCatalogPort_ShouldDiscoverOnlyAevatarCatalogTools`
- `Source_WithoutCatalogPort_ShouldDiscoverNoTools`
- `ListWorkflows_ShouldReturnCatalogFreshness`
- `ListWorkflows_WhenArgumentsContainUnknownProperty_ShouldReturnInvalidArguments`
- `ListWorkflows_WhenJsonIsMalformed_ShouldReturnInvalidArguments`
- `GetWorkflow_ShouldReturnYamlDefinitionAndEdges`
- `GetWorkflow_WhenNameMissing_ShouldReturnInvalidArguments`
- `GetWorkflow_WhenMissing_ShouldReturnWorkflowNotFound`
- `WorkflowCatalogTools_WhenCanceled_ShouldRethrowCancellation`
- `WorkflowCatalogTools_WhenProviderFails_ShouldReturnSafeStructuredError`

Core assertions:

```csharp
var source = new WorkflowCatalogAgentToolSource(new RecordingWorkflowCatalogPort());
var tools = await source.DiscoverToolsAsync();

tools.Select(tool => tool.Name).Should().Equal(
    "aevatar_list_workflows",
    "aevatar_get_workflow");
tools.Should().OnlyContain(tool => tool.IsReadOnly && !tool.IsDestructive);

var output = await tools.Single(tool => tool.Name == "aevatar_list_workflows")
    .ExecuteAsync("{}");
using var document = JsonDocument.Parse(output);
var workflow = document.RootElement.GetProperty("workflows")[0];
workflow.GetProperty("name").GetString().Should().Be("daily_digest");
workflow.GetProperty("authority_state_version").GetInt64().Should().Be(7);
workflow.GetProperty("last_event_id").GetString().Should().Be("event-7");
```

- [ ] **Step 2: Run the Workflow tool suite and verify RED**

```bash
dotnet test test/Aevatar.AI.ToolProviders.Workflow.Tests/Aevatar.AI.ToolProviders.Workflow.Tests.csproj --nologo
```

Expected: compilation failure because the catalog source and tools do not exist.

- [ ] **Step 3: Implement the catalog-only source**

```csharp
public sealed class WorkflowCatalogAgentToolSource : IAgentToolSource
{
    private readonly IWorkflowCatalogPort? _catalog;

    public WorkflowCatalogAgentToolSource(IWorkflowCatalogPort? catalog = null)
    {
        _catalog = catalog;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _catalog is null
                ? []
                : [new ListAevatarWorkflowsTool(_catalog), new GetAevatarWorkflowTool(_catalog)]);
    }
}
```

- [ ] **Step 4: Implement strict JSON and structured errors**

Add a local `WorkflowCatalogToolJson` helper with snake_case serializer options, blank-input normalization, exact unknown-property detection through `JsonDocument`, string normalization, and this error envelope:

```csharp
private sealed record WorkflowCatalogToolErrorJson(WorkflowCatalogToolErrorBody Error);
private sealed record WorkflowCatalogToolErrorBody(string Code, string Message);
```

`ListAevatarWorkflowsTool` accepts no properties, calls `ListWorkflowCatalogAsync`, and returns `workflows` plus `count`. `GetAevatarWorkflowTool` accepts only `workflow_name`, calls `GetWorkflowDetailAsync`, returns the typed catalog/detail fields under snake_case names, and maps null to `workflow_not_found`. Both rethrow `OperationCanceledException`; unexpected failures return `workflow_query_failed` with `ex.GetType().Name`, not `ex.Message`.

- [ ] **Step 5: Register the selectively composable source**

In `AddWorkflowTools` add:

```csharp
services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IAgentToolSource, WorkflowCatalogAgentToolSource>());
```

Do not add these tools to `WorkflowAgentToolSource`; that source also owns unrelated execution inspection and legacy definition mutation adapters.

- [ ] **Step 6: Run the suite and verify GREEN**

Run Step 2 again. Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Aevatar.AI.ToolProviders.Workflow/WorkflowCatalogAgentToolSource.cs \
  src/Aevatar.AI.ToolProviders.Workflow/Tools/AevatarWorkflowCatalogTools.cs \
  src/Aevatar.AI.ToolProviders.Workflow/ServiceCollectionExtensions.cs \
  test/Aevatar.AI.ToolProviders.Workflow.Tests/WorkflowCatalogToolsTests.cs
git commit -m "Add workflow catalog query tools"
```

---

### Task 4: Compose Query Tools Into Mainnet And Studio

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`
- Modify: `src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionCatalog.cs`
- Modify: `test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs`
- Modify: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowDefinitionCatalogTests.cs`

**Interfaces:**
- Consumes: `WorkflowCatalogAgentToolSource` from Task 3.
- Verifies: schedule query and mutation interfaces resolve to one singleton.
- Produces: `workspace.default` and Studio-role visibility for workflow queries.

- [ ] **Step 1: Write failing composition and allowlist tests**

Extend `AddAevatarMainnetHost_ShouldRegisterDefaultToolSets`:

```csharp
workspace.Sources.Should().Contain(source => source is StudioTeamQueryToolSource);
workspace.Sources.Should().Contain(source => source is StudioMemberQueryToolSource);
workspace.Sources.Should().Contain(source => source is StudioScheduleQueryToolSource);
workspace.Sources.Should().Contain(source => source is WorkflowCatalogAgentToolSource);

var scheduleQueries = app.Services.GetRequiredService<IStudioMemberAutomationQueryPort>();
var scheduleMutations = app.Services.GetRequiredService<IStudioMemberWorkflowSchedulePort>();
scheduleQueries.Should().BeSameAs(scheduleMutations);
```

Extend the built-in Studio workflow test:

```csharp
allowed.Should().Contain("aevatar_list_workflows");
allowed.Should().Contain("aevatar_get_workflow");
allowed.Should().NotContain("workflow_list_defs");
allowed.Should().NotContain("workflow_read_def");
```

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo \
  --filter FullyQualifiedName~AddAevatarMainnetHost_ShouldRegisterDefaultToolSets
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo \
  --filter FullyQualifiedName~BuiltInStudioYaml_ShouldParseAsMemberProvisionStudioRoleWithToolAllowlist
```

Expected: workflow source and allowlist assertions fail.

- [ ] **Step 3: Compose the Workflow catalog source**

Add a direct Mainnet project reference to `Aevatar.AI.ToolProviders.Workflow`, import its namespace, and insert this source beside the existing workflow start/observe/read sources:

```csharp
CreateToolSource<WorkflowCatalogAgentToolSource>,
```

- [ ] **Step 4: Add only the new workflow query tools to the Studio allowlist**

```yaml
- aevatar_list_workflows
- aevatar_get_workflow
```

Keep `workflow_list_defs`, `workflow_read_def`, and all workflow definition mutation tools excluded.

- [ ] **Step 5: Run the focused tests and verify GREEN**

Run Step 2 again. Expected: both commands pass.

- [ ] **Step 6: Commit**

```bash
git add src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj \
  src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs \
  src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionCatalog.cs \
  test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs \
  test/Aevatar.Workflow.Host.Api.Tests/WorkflowDefinitionCatalogTests.cs
git commit -m "Compose Studio query tools"
```

---

### Task 5: Verify, Review, And Prepare Clean History

**Files:**
- Verify: all changed source, tests, guards, and docs.
- Do not modify: `apps/aevatar-console-web/**`.

**Interfaces:**
- Verifies: the complete design and repository gates.
- Produces: a clean local candidate based on current `origin/dev`.
- Does not update: remote `feature/integrate` without explicit authorization.

- [ ] **Step 1: Run focused suites and mandatory guards**

```bash
dotnet test test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/Aevatar.AI.ToolProviders.StudioProvisioning.Tests.csproj --nologo
dotnet test test/Aevatar.AI.ToolProviders.Workflow.Tests/Aevatar.AI.ToolProviders.Workflow.Tests.csproj --nologo
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/architecture_guards.sh
```

Expected: every command exits 0.

- [ ] **Step 2: Run full build and test**

```bash
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo --no-build
```

Expected: 0 build errors and 0 test failures. Report existing warnings and skips.

- [ ] **Step 3: Verify frontend and diff invariants**

```bash
git diff --quiet origin/dev -- apps/aevatar-console-web
git diff --check origin/dev..HEAD
git status --short --branch
```

Expected: frontend diff exits 0, diff check is clean, and no uncommitted files remain.

- [ ] **Step 4: Request a fresh read-only code review**

Review `origin/dev..HEAD` against the design document. Fix every Critical or Important finding through a new red/green cycle, then rerun affected focused tests and global guards.

- [ ] **Step 5: Refresh refs and build a clean local candidate**

```bash
git fetch origin --prune
git rev-parse origin/dev origin/feature/integrate
git rev-list --count origin/dev..HEAD
```

If either remote ref moved, integrate the new `origin/dev`, restore the frontend
from `origin/dev`, and repeat Steps 1-4 before proceeding. When refs are stable,
create a one-commit clean candidate whose tree is exactly the reviewed tree:

```bash
REVIEWED_TREE=$(git rev-parse HEAD^{tree})
CLEAN_HEAD=$(printf '%s\n\n%s\n' \
  'Add Studio query tools' \
  'Expose scoped Studio reads, workflow catalog queries, and enforce identity guards.' \
  | git commit-tree "$REVIEWED_TREE" -p origin/dev)
git branch -f chore/2026-07-20_realign-feature-integrate-clean "$CLEAN_HEAD"
test "$(git rev-parse HEAD^{tree})" = "$(git rev-parse "$CLEAN_HEAD^{tree}")"
test "$(git rev-list --count origin/dev.."$CLEAN_HEAD")" = "1"
```

- [ ] **Step 6: Obtain explicit force-push authorization**

Explain that squash merge #2610 left old feature ancestry unreachable from `dev`, so a normal merge still advertises hundreds of commits. Ask permission for this exact guarded operation:

```bash
EXPECTED_OLD_SHA=$(git rev-parse origin/feature/integrate)
CLEAN_HEAD=$(git rev-parse chore/2026-07-20_realign-feature-integrate-clean)
git push --force-with-lease=refs/heads/feature/integrate:"$EXPECTED_OLD_SHA" \
  origin "$CLEAN_HEAD":refs/heads/feature/integrate
```

Do not run it without explicit approval.

- [ ] **Step 7: Push and create the PR after authorization**

Verify `origin/dev` is the ancestry base, confirm GitHub reports only intentional commits/files and no conflicts, then create `feature/integrate -> dev`. Include problem/solution, affected paths, verification results, and the design document in the PR body.
