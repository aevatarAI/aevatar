# Chat Workflow Resource Semantics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make unqualified Console Chat workflow inventory read current-scope Team-owned workflow members, while exposing the global public catalog only through explicitly named template tools.

**Architecture:** Add a narrow Studio workflow query adapter over the existing projection-backed `IStudioMemberQueryPort`; do not add a read model or query-time materialization. Rename the existing global catalog tool wire contract to workflow-template semantics, then encode the distinction in the Studio prompt, allowlist, Host composition, tests, and active documentation.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, `System.Text.Json`, Aevatar agent tool sources, Studio projection query ports, Workflow parser.

## Global Constraints

- `memberId`, `workflowId`, and `publishedServiceId` remain distinct typed identities and use different fixture values.
- Workspace workflow queries read only `IStudioMemberQueryPort`; they do not replay events, read actor state, prime projection, or call lifecycle helpers.
- Public catalog tools are named `aevatar_list_workflow_templates` and `aevatar_get_workflow_template`; no compatibility aliases remain.
- The canonical member workflow route is `/scopes/:scopeId/teams/:teamId/members/:memberId/workflow`.
- Tests are written and observed failing before production code changes.
- Existing catalog contamination is repaired only by a separate background materialization or operations migration, never in an online query.

---

### Task 1: Add The Workspace Workflow Query Tool

**Files:**
- Create: `test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/StudioWorkflowQueryToolsTests.cs`
- Create: `src/Aevatar.AI.ToolProviders.StudioProvisioning/StudioWorkflowQueryTools.cs`
- Create: `src/Aevatar.AI.ToolProviders.StudioProvisioning/StudioWorkflowQueryToolSource.cs`
- Modify: `src/Aevatar.AI.ToolProviders.StudioProvisioning/ServiceCollectionExtensions.cs`
- Modify: `test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/ProvisionWorkflowScheduleToolTests.cs`

**Interfaces:**
- Consumes: `IStudioMemberQueryPort.ListAsync(string, StudioMemberRosterPageRequest?, CancellationToken)`.
- Produces: `StudioWorkflowQueryToolSource : IAgentToolSource`.
- Produces: read-only `aevatar_list_workflows` with optional `team_id`, `page_size`, and `page_token`.

- [x] **Step 1: Write discovery, filtering, identity, validation, cancellation, and error tests**

Create a focused test fixture whose representative success assertion is:

```csharp
var output = await tool.ExecuteAsync(
    """{"team_id":"team-alpha","page_size":20,"page_token":"page-2"}""",
    callerToken);

port.LastScopeId.Should().Be("scope-current");
port.LastPage.Should().Be(new StudioMemberRosterPageRequest(20, "page-2", "team-alpha"));

using var document = JsonDocument.Parse(output);
var workflows = document.RootElement.GetProperty("workflows");
workflows.GetArrayLength().Should().Be(2);
var bound = workflows[0];
bound.GetProperty("team_id").GetString().Should().Be("team-alpha");
bound.GetProperty("member_id").GetString().Should().Be("m-alpha");
bound.GetProperty("workflow_id").GetString().Should().Be("wf-alpha");
bound.GetProperty("published_service_id").GetString().Should().Be("svc-alpha");
bound.GetProperty("workflow_url").GetString().Should().Be(
    "/scopes/scope-current/teams/team-alpha/members/m-alpha/workflow");
workflows[1].GetProperty("workflow_id").ValueKind.Should().Be(JsonValueKind.Null);
document.RootElement.GetProperty("next_page_token").GetString().Should().Be("next-page");
```

The recording port returns the two valid workflow members plus a script member,
an unassigned workflow member, and a cross-scope workflow member. Tests assert
that only the two Team-owned, current-scope workflow members remain. Additional
tests cover missing scope, unknown `scope_id`, malformed JSON, owner-scope
precedence, cancellation rethrow, and sanitized query failure.

Update existing DI/source discovery assertions to include
`StudioWorkflowQueryToolSource` and `aevatar_list_workflows`.

- [x] **Step 2: Run the tests and verify the expected RED state**

Run:

```bash
dotnet test test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/Aevatar.AI.ToolProviders.StudioProvisioning.Tests.csproj --nologo --no-restore
```

Expected: compilation or assertion failure because
`StudioWorkflowQueryToolSource` and its tool do not exist.

- [x] **Step 3: Implement the narrow source and tool**

The source discovers only the workspace workflow query:

```csharp
public sealed class StudioWorkflowQueryToolSource : IAgentToolSource
{
    private readonly IStudioMemberQueryPort? _memberQueryPort;

    public StudioWorkflowQueryToolSource(IStudioMemberQueryPort? memberQueryPort = null)
    {
        _memberQueryPort = memberQueryPort;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<IAgentTool>>(
            _memberQueryPort is null ? [] : [new ListStudioWorkflowsTool(_memberQueryPort)]);
    }
}
```

The execution path resolves the context scope, validates only the three declared
arguments, forwards the page request and caller token, then filters the returned
read-model summaries:

```csharp
var result = await _memberQueryPort.ListAsync(scopeId, page, ct);
var workflows = result.Members
    .Where(member =>
        string.Equals(member.ScopeId, scopeId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(member.TeamId)
        && string.Equals(
            member.ImplementationKind,
            MemberImplementationKindNames.Workflow,
            StringComparison.Ordinal))
    .Select(StudioWorkflowResultJson.From)
    .ToArray();
```

The wire record carries flat identity fields. Override the shared null-ignore
option for `WorkflowId` so an unbound member emits an explicit JSON null:

```csharp
internal sealed record StudioWorkflowResultJson(
    string ScopeId,
    string TeamId,
    string MemberId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? WorkflowId,
    string PublishedServiceId,
    string DisplayName,
    string Description,
    string LifecycleStage,
    string? WorkflowRevision,
    string? LastBoundRevisionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string WorkflowUrl);
```

Register the source with `TryAddEnumerable` in
`AddStudioProvisioningTools()`.

- [x] **Step 4: Run the Studio provisioning tests and verify GREEN**

Run the Task 1 test command again.

Expected: all Studio provisioning tool tests pass with zero failures.

- [x] **Step 5: Commit the workspace query change**

```bash
git add src/Aevatar.AI.ToolProviders.StudioProvisioning \
  test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests
git commit -m "Add workspace workflow query tool"
```

---

### Task 2: Rename The Global Catalog Contract To Templates

**Files:**
- Modify: `test/Aevatar.AI.ToolProviders.Workflow.Tests/WorkflowCatalogToolsTests.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Workflow/Tools/AevatarWorkflowCatalogTools.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Workflow/WorkflowCatalogAgentToolSource.cs`
- Modify: `src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowCatalogCurrentStateProjector.cs`

**Interfaces:**
- Consumes: existing `IWorkflowCatalogPort` list/detail read-model methods.
- Produces: `aevatar_list_workflow_templates` and `aevatar_get_workflow_template`.
- Produces: list property `templates`, detail property `template`, and input `template_name`.

- [x] **Step 1: Change tests to the explicit template wire contract**

Update discovery expectations and every invocation to use the new names. Assert
the list and detail wire fields directly:

```csharp
tools.Select(tool => tool.Name).Should().Equal(
    "aevatar_list_workflow_templates",
    "aevatar_get_workflow_template");

var listOutput = await listTool.ExecuteAsync("{}");
using var listDocument = JsonDocument.Parse(listOutput);
PropertyNames(listDocument.RootElement).Should().Equal("templates", "count");

var detailOutput = await getTool.ExecuteAsync(
    """{"template_name":"daily_digest"}""");
using var detailDocument = JsonDocument.Parse(detailOutput);
PropertyNames(detailDocument.RootElement).Should().Equal(
    "template", "yaml", "definition", "edges");
```

Tests also require `workflow_template_not_found` and
`workflow_template_query_failed`, and reject legacy `workflow_name`.

- [x] **Step 2: Run Workflow tool tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.AI.ToolProviders.Workflow.Tests/Aevatar.AI.ToolProviders.Workflow.Tests.csproj --nologo --no-restore
```

Expected: failures show the source still advertises the ambiguous workflow names
and the old wire properties.

- [x] **Step 3: Implement the template contract rename**

Rename the internal tool classes and change their public contract:

```csharp
public string Name => "aevatar_list_workflow_templates";

public string Description =>
    "List public workflow templates in the global Aevatar template library. " +
    "This does not list workflows owned by Teams in the caller's workspace.";
```

```csharp
private static readonly string[] s_allowedProperties = ["template_name"];
public string Name => "aevatar_get_workflow_template";
```

Return `WorkflowTemplateCatalogListJson(Templates, Count)` and
`WorkflowTemplateCatalogDetailJson(Template, Yaml, Definition, Edges)`. Keep the
existing `ShowInLibrary` filter for list calls and exact-name behavior for detail
calls. Update source and projector comments to name the template tools.

- [x] **Step 4: Run Workflow tool tests and verify GREEN**

Run the Task 2 test command again.

Expected: all Workflow tool tests pass with zero failures.

- [x] **Step 5: Commit the template contract change**

```bash
git add src/Aevatar.AI.ToolProviders.Workflow \
  src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowCatalogCurrentStateProjector.cs \
  test/Aevatar.AI.ToolProviders.Workflow.Tests/WorkflowCatalogToolsTests.cs
git commit -m "Rename public workflow catalog tools"
```

---

### Task 3: Align Studio Prompt And Host Composition

**Files:**
- Modify: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowDefinitionCatalogTests.cs`
- Modify: `test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs`
- Modify: `src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionCatalog.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`

**Interfaces:**
- Consumes: `StudioWorkflowQueryToolSource` and `WorkflowCatalogAgentToolSource`.
- Produces: Studio-role visibility and routing instructions for workspace workflows versus public templates.
- Produces: `workspace.default` composition containing the new Studio source.

- [x] **Step 1: Write prompt, allowlist, and Host composition assertions**

Extend the built-in Studio workflow test:

```csharp
role.SystemPrompt.Should().Contain(
    "Without a template qualifier, workflow means a Team-owned workflow member in the current workspace");
role.SystemPrompt.Should().Contain("follow `next_page_token`");
role.SystemPrompt.Should().Contain("public templates, examples, or the template library");
role.SystemPrompt.Should().Contain("`member_id`, `workflow_id`, and `published_service_id`");

allowed.Should().Contain("aevatar_list_workflows");
allowed.Should().Contain("aevatar_list_workflow_templates");
allowed.Should().Contain("aevatar_get_workflow_template");
allowed.Should().NotContain("aevatar_get_workflow");
```

Extend Mainnet composition assertions:

```csharp
workspace.Sources.Should().Contain(source => source is StudioWorkflowQueryToolSource);
workspace.Sources.Should().Contain(source => source is WorkflowCatalogAgentToolSource);
```

- [x] **Step 2: Run the two focused tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~BuiltInStudioYaml_ShouldParseAsMemberProvisionStudioRoleWithToolAllowlist
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~AddAevatarMainnetHost_ShouldRegisterDefaultToolSets
```

Expected: failures show missing prompt rules, allowlist names, and Host source.

- [x] **Step 3: Implement Studio semantic steering and composition**

Add a `Resource semantics` block before the existing authoring instructions:

```text
- Without a template qualifier, workflow means a Team-owned workflow member in the current workspace.
  Use `aevatar_list_workflows`; when the user asks for all workflows, follow `next_page_token` until absent.
- Only use `aevatar_list_workflow_templates` or `aevatar_get_workflow_template` when the user explicitly
  asks for public templates, examples, or the template library.
- Keep `member_id`, `workflow_id`, and `published_service_id` distinct; never derive or substitute them.
```

Replace the ambiguous catalog allowlist entry with the two template names, keep
the workspace list name, and add `CreateToolSource<StudioWorkflowQueryToolSource>`
to `workspace.default`.

- [x] **Step 4: Run both focused tests and verify GREEN**

Run the two Task 3 commands again.

Expected: both test filters pass with zero failures.

- [x] **Step 5: Commit the Chat composition change**

```bash
git add src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionCatalog.cs \
  src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs \
  test/Aevatar.Workflow.Host.Api.Tests/WorkflowDefinitionCatalogTests.cs \
  test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs
git commit -m "Align Studio chat workflow semantics"
```

---

### Task 4: Make The Product Decision Durable And Verify The Branch

**Files:**
- Modify: `docs/canon/workflow-catalog-visibility.md`
- Create: `docs/operations/2026-07-23-workflow-catalog-contamination-repair.md`
- Modify: `docs/superpowers/specs/2026-07-23-chat-workflow-resource-semantics-design.md`
- Modify: `docs/superpowers/plans/2026-07-23-chat-workflow-resource-semantics.md`

**Interfaces:**
- Documents: authoritative resource ownership, tool vocabulary, and historical-data repair boundary.
- Verifies: no query-time repair and no active ambiguous tool contract.

- [x] **Step 1: Update active canon and add the operations boundary**

Document this table as the active contract:

```markdown
| Chat product resource | Agent tool | Fact source |
| --- | --- | --- |
| Team-owned workspace workflow | `aevatar_list_workflows` | Studio member current-state read model |
| Public workflow template | `aevatar_list_workflow_templates` / `aevatar_get_workflow_template` | Global workflow catalog read model |
```

Replace the former unresolved product question with the approved default. The
operations document states that legacy catalog rows are assessed and removed by
an explicit background migration with backup, dry-run inventory, idempotent
deletion, and post-migration verification. It explicitly forbids adding replay,
priming, mutation, or cleanup to `IWorkflowCatalogPort` query calls and notes that
no online repair command is introduced by this change.

- [x] **Step 2: Search for stale production and active-document semantics**

Run:

```bash
rg -n 'aevatar_get_workflow|aevatar_list_workflows' \
  src test docs/canon docs/operations \
  -g '!**/bin/**' -g '!**/obj/**'
```

Expected: `aevatar_list_workflows` refers only to the Studio workspace tool;
`aevatar_get_workflow` has no exact production or active-doc occurrence; public
catalog references use the two template names. A test may retain the exact old
name only as a negative regression assertion. Historical superseded specs and
plans may retain their original record.

- [x] **Step 3: Run targeted suites and mandatory guards**

Run serially to avoid shared build-output contention:

```bash
dotnet test test/Aevatar.AI.ToolProviders.StudioProvisioning.Tests/Aevatar.AI.ToolProviders.StudioProvisioning.Tests.csproj --nologo --no-restore
dotnet test test/Aevatar.AI.ToolProviders.Workflow.Tests/Aevatar.AI.ToolProviders.Workflow.Tests.csproj --nologo --no-restore
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~WorkflowDefinitionCatalogTests
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~AddAevatarMainnetHost_ShouldRegisterDefaultToolSets
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/workflow_catalog_query_port_guard.sh
bash tools/docs/lint.sh
bash tools/ci/architecture_guards.sh
dotnet build src/Aevatar.AI.ToolProviders.StudioProvisioning/Aevatar.AI.ToolProviders.StudioProvisioning.csproj --nologo --no-restore
dotnet build src/Aevatar.AI.ToolProviders.Workflow/Aevatar.AI.ToolProviders.Workflow.csproj --nologo --no-restore
dotnet build src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj --nologo --no-restore
```

Expected: every command exits 0; tests report zero failures. Existing repository
warnings are recorded but do not count as failures.

- [x] **Step 4: Review the final diff and commit documentation**

```bash
git diff --check
git status --short
git diff --stat origin/feature/integrate...HEAD
git diff origin/feature/integrate...HEAD
git add docs/canon/workflow-catalog-visibility.md \
  docs/operations/2026-07-23-workflow-catalog-contamination-repair.md \
  docs/superpowers/specs/2026-07-23-chat-workflow-resource-semantics-design.md \
  docs/superpowers/plans/2026-07-23-chat-workflow-resource-semantics.md
git commit -m "Document workflow resource ownership"
```

- [ ] **Step 5: Synchronize and push without force**

```bash
git fetch origin feature/integrate
git rebase origin/feature/integrate
```

If rebase changes `HEAD`, rerun the Task 4 verification commands. Then push:

```bash
git push origin HEAD:feature/integrate
```

Expected: a normal fast-forward update; never use `--force` or
`--force-with-lease`.
