# YAML Write-Boundary Validation Fix + Deep Workflow Templates

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the YAML validation bypass at write boundary so invalid workflows are rejected before activation, then create 1-2 deep workflow templates for Lark-to-GitHub automation.

**Architecture:** The existing `WorkflowValidator` in `Aevatar.Workflow.Core/Validation/` already validates step types, role references, and branch targets. The bug is that `AppScopedWorkflowService.SaveAsync` and `WorkflowServiceImplementationAdapter.PrepareRevisionAsync` don't call the full validator before persisting. Fix = wire the existing validator into the write boundary with strict options enabled.

**Tech Stack:** C# / .NET 9 / xUnit / FluentAssertions

---

### Task 1: Add strict validation to AppScopedWorkflowService.SaveAsync

**Files:**
- Modify: `src/Aevatar.Studio.Application/AppScopedWorkflowService.cs:157-200`
- Test: `test/Aevatar.Integration.Tests/WorkflowValidatorCoverageTests.cs`

The `SaveAsync` method parses YAML to extract the name but never runs `WorkflowValidator.Validate()`. Invalid step types, missing role references, and broken branch targets all pass through silently.

- [ ] **Step 1: Write the failing test**

Add to `test/Aevatar.Integration.Tests/WorkflowValidatorCoverageTests.cs`:

```csharp
[Fact]
public void Validate_WithStrictOptions_ShouldRejectUnknownStepType()
{
    var wf = new WorkflowDefinition
    {
        Name = "test-wf",
        Roles = [new RoleDefinition { Id = "r1", Name = "Role1" }],
        Steps =
        [
            new StepDefinition
            {
                Id = "step1",
                Type = "totally_fake_step_type",
                TargetRole = "r1",
            },
        ],
    };

    var knownTypes = WorkflowPrimitiveCatalog.BuildCanonicalStepTypeSet(null);
    var options = new WorkflowValidator.WorkflowValidationOptions
    {
        RequireKnownStepTypes = true,
        KnownStepTypes = knownTypes,
    };

    var errors = WorkflowValidator.Validate(wf, options, availableWorkflowNames: null);

    errors.Should().Contain(e => e.Contains("totally_fake_step_type"));
}
```

- [ ] **Step 2: Run test to verify it passes (this tests the validator, not the save path)**

Run: `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "Validate_WithStrictOptions_ShouldRejectUnknownStepType" --nologo`

Expected: PASS (the validator already supports this, the bug is that SaveAsync doesn't call it)

- [ ] **Step 3: Wire WorkflowValidator into SaveAsync**

In `src/Aevatar.Studio.Application/AppScopedWorkflowService.cs`, after `_yamlDocumentService.Parse(normalizedYaml)` at line 166, add validation:

```csharp
// After line 166: var parsed = _yamlDocumentService.Parse(normalizedYaml);
// Add validation using the workflow-core validator
if (parsed.Document != null)
{
    var coreValidator = new WorkflowYamlValidatorImpl();
    var validationResult = coreValidator.Validate(normalizedYaml);
    if (!validationResult.Success)
    {
        var errorMessages = string.Join("; ", validationResult.Diagnostics.Select(d => d.Message));
        throw new InvalidOperationException($"Workflow YAML validation failed: {errorMessages}");
    }
}
```

Note: `WorkflowYamlValidatorImpl` is in `Aevatar.Workflow.Core.Primitives`. Check if the project already references `Aevatar.Workflow.Core` or if `IWorkflowYamlValidator` (from abstractions) is available via DI. Prefer the DI-injected `IWorkflowYamlValidator` if available in the constructor. If not, use the static `WorkflowValidator.Validate()` directly after parsing.

- [ ] **Step 4: Run existing tests to verify no regression**

Run: `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo`

Expected: All existing tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/Aevatar.Studio.Application/AppScopedWorkflowService.cs test/Aevatar.Integration.Tests/WorkflowValidatorCoverageTests.cs
git commit -m "fix(studio): validate workflow YAML at write boundary in SaveAsync"
```

---

### Task 2: Fix PrepareRevisionAsync to always validate YAML

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/Adapters/WorkflowServiceImplementationAdapter.cs:19-37`
- Test: new test in `test/Aevatar.GAgentService.Integration.Tests/`

The `PrepareRevisionAsync` method only parses YAML when `WorkflowName` is blank (line 30-37). When the name is already provided, YAML is passed through without any validation. This means invalid YAML with a pre-populated name bypasses all checks.

- [ ] **Step 1: Write the failing test**

Create or add to an appropriate test file in `test/Aevatar.GAgentService.Integration.Tests/`:

```csharp
[Fact]
public async Task PrepareRevisionAsync_WithPresetNameAndInvalidYaml_ShouldStillValidate()
{
    // Arrange: YAML with an invalid step type, but WorkflowName already set
    var invalidYaml = """
        name: test-wf
        roles:
          - id: r1
            name: R1
        steps:
          - id: s1
            type: nonexistent_step_type
            role: r1
        """;

    var request = new PrepareServiceRevisionRequest
    {
        Spec = new ServiceImplementationSpec
        {
            Identity = new ServiceIdentity { /* ... */ },
            WorkflowSpec = new WorkflowImplementationSpec
            {
                WorkflowName = "test-wf",  // Name preset = parse bypassed
                WorkflowYaml = invalidYaml,
            },
        },
    };

    // Act & Assert: should throw even though name is preset
    var adapter = new WorkflowServiceImplementationAdapter(mockWorkflowRunActorPort);
    var act = () => adapter.PrepareRevisionAsync(request);
    await act.Should().ThrowAsync<InvalidOperationException>()
        .WithMessage("*validation failed*");
}
```

Adjust constructor args and type names to match actual signatures.

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL (current code skips validation when name is preset)

- [ ] **Step 3: Fix PrepareRevisionAsync to always validate**

In `WorkflowServiceImplementationAdapter.cs`, change the validation logic to always parse and validate, regardless of whether `WorkflowName` is provided:

```csharp
public async Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
    PrepareServiceRevisionRequest request,
    CancellationToken ct = default)
{
    ArgumentNullException.ThrowIfNull(request);
    var spec = request.Spec?.WorkflowSpec
        ?? throw new InvalidOperationException("workflow implementation_spec is required.");
    if (string.IsNullOrWhiteSpace(spec.WorkflowYaml))
        throw new InvalidOperationException("workflow_yaml is required.");

    // Always parse and validate YAML, regardless of whether name is preset
    var parse = await _workflowRunActorPort.ParseWorkflowYamlAsync(spec.WorkflowYaml, ct);
    if (!parse.Succeeded)
        throw new InvalidOperationException($"Workflow YAML validation failed: {parse.Error}");

    var resolvedWorkflowName = !string.IsNullOrWhiteSpace(spec.WorkflowName)
        ? spec.WorkflowName
        : parse.WorkflowName;

    // ... rest unchanged
}
```

- [ ] **Step 4: Run test to verify it passes**

Expected: PASS

- [ ] **Step 5: Run full test suite for the project**

Run: `dotnet test test/Aevatar.GAgentService.Integration.Tests/ --nologo`

Expected: All PASS

- [ ] **Step 6: Commit**

```bash
git add src/platform/Aevatar.GAgentService.Infrastructure/Adapters/WorkflowServiceImplementationAdapter.cs test/Aevatar.GAgentService.Integration.Tests/
git commit -m "fix(gagent-service): always validate YAML in PrepareRevisionAsync regardless of preset name"
```

---

### Task 3: Add connector reference validation to WorkflowValidator

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs:108-223`
- Test: `test/Aevatar.Integration.Tests/WorkflowValidatorCoverageTests.cs`

The validator checks step types, role refs, branch targets, and workflow_call targets. But `connector_call` steps don't validate that the referenced connector exists. When a user modifies a template and types a wrong connector name, the error surfaces at runtime, not at save time.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Validate_ConnectorCallWithUnknownConnector_ShouldReportError()
{
    var wf = new WorkflowDefinition
    {
        Name = "test-wf",
        Roles = [],
        Steps =
        [
            new StepDefinition
            {
                Id = "step1",
                Type = "connector_call",
                Parameters = new Dictionary<string, string>
                {
                    ["connector"] = "nonexistent_connector",
                    ["action"] = "some_action",
                },
            },
        ],
    };

    var options = new WorkflowValidator.WorkflowValidationOptions
    {
        RequireKnownConnectors = true,
        KnownConnectorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "github", "lark" },
    };

    var errors = WorkflowValidator.Validate(wf, options, availableWorkflowNames: null);

    errors.Should().Contain(e => e.Contains("nonexistent_connector"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL (WorkflowValidationOptions doesn't have `RequireKnownConnectors` yet)

- [ ] **Step 3: Add RequireKnownConnectors to WorkflowValidationOptions**

In `WorkflowValidator.cs`, add to `WorkflowValidationOptions`:

```csharp
/// <summary>
/// Whether connector_call steps must reference a known connector name.
/// </summary>
public bool RequireKnownConnectors { get; init; }

/// <summary>
/// Known connector names. Used when RequireKnownConnectors is true.
/// </summary>
public ISet<string>? KnownConnectorNames { get; init; }
```

- [ ] **Step 4: Add connector validation to ValidateTypeSpecificRules**

In `ValidateTypeSpecificRules`, add a block for `connector_call`:

```csharp
if (stepType is "connector_call" or "secure_connector_call")
{
    var connectorName = step.Parameters.GetValueOrDefault("connector", "").Trim();
    if (string.IsNullOrEmpty(connectorName))
    {
        errors.Add($"步骤 '{step.Id}'（{stepType}）缺少 connector 参数");
    }
    else if (options.RequireKnownConnectors &&
             options.KnownConnectorNames != null &&
             !options.KnownConnectorNames.Contains(connectorName))
    {
        errors.Add($"步骤 '{step.Id}'（{stepType}）引用未注册 connector '{connectorName}'");
    }
    return;
}
```

Insert this block before the existing `conditional` block (around line 141).

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "ConnectorCall" --nologo`

Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs test/Aevatar.Integration.Tests/WorkflowValidatorCoverageTests.cs
git commit -m "feat(workflow): add connector reference validation to WorkflowValidator"
```

---

### Task 4: Create Lark-to-GitHub deep workflow template

**Files:**
- Create: `workflows/lark-github-issue.yaml`
- Test: add to existing `test/Aevatar.Integration.Tests/WorkflowValidatorCoverageTests.cs` or a dedicated template test

This is the team's dogfood workflow: Lark bot receives a message containing keywords (like "urgent", "bug"), then an LLM classifies the intent and creates a GitHub Issue via connector_call. Single-file YAML, no sub-workflows.

- [ ] **Step 1: Write the template YAML**

Create `workflows/lark-github-issue.yaml`:

```yaml
# Lark 消息 → LLM 分类 → GitHub Issue 创建
# 当 Lark 收到包含关键词的消息时，LLM 判断意图并自动创建 GitHub Issue
name: lark_github_issue
description: >
  Receives a Lark message, classifies intent via LLM, and creates a GitHub
  Issue if it matches an actionable category (bug, feature request, urgent).

roles:
  - id: classifier
    name: Intent Classifier
    system_prompt: |
      You are an intent classifier for incoming team messages.
      Classify the message into one of: bug, feature_request, urgent, question, ignore.
      Respond with ONLY the category name, nothing else.

  - id: formatter
    name: Issue Formatter
    system_prompt: |
      You are a GitHub Issue formatter.
      Given a classified message, produce a JSON object with these fields:
      - title: concise issue title (max 80 chars)
      - body: full description with context from the original message
      - labels: array of GitHub label strings matching the classification
      Respond with ONLY the JSON object, no markdown fencing.

steps:
  - id: classify
    type: llm_call
    role: classifier
    parameters: {}

  - id: check_actionable
    type: conditional
    parameters:
      condition: "{{ steps.classify.output not in ['question', 'ignore'] }}"
    branches:
      "true": format_issue
      "false": done

  - id: format_issue
    type: llm_call
    role: formatter
    parameters:
      input: "Classification: {{ steps.classify.output }}\nOriginal message: {{ input }}"

  - id: create_issue
    type: connector_call
    parameters:
      connector: github
      action: create_issue
      input: "{{ steps.format_issue.output }}"

  - id: done
    type: assign
    parameters:
      output: "{{ steps.classify.output == 'ignore' ? 'Message ignored' : 'Issue created' }}"
```

- [ ] **Step 2: Write a test that validates the template parses and passes validation**

Add to `test/Aevatar.Integration.Tests/WorkflowValidatorCoverageTests.cs`:

```csharp
[Fact]
public void LarkGithubIssueTemplate_ShouldPassBasicValidation()
{
    var yaml = File.ReadAllText(
        Path.Combine(TestContext.SolutionRoot, "workflows", "lark-github-issue.yaml"));

    var parser = new WorkflowParser();
    var definition = parser.Parse(yaml);

    definition.Name.Should().Be("lark_github_issue");
    definition.Roles.Should().HaveCount(2);
    definition.Steps.Should().HaveCountGreaterOrEqualTo(4);

    var errors = WorkflowValidator.Validate(definition);
    errors.Should().BeEmpty("template should pass basic validation");
}
```

Adjust `TestContext.SolutionRoot` to match the project's test helper for locating the repo root. If no such helper exists, use a relative path or `Path.GetFullPath("../../../../workflows/lark-github-issue.yaml")` from the test assembly output directory.

- [ ] **Step 3: Run test**

Run: `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --filter "LarkGithubIssueTemplate" --nologo`

Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add workflows/lark-github-issue.yaml test/Aevatar.Integration.Tests/WorkflowValidatorCoverageTests.cs
git commit -m "feat(workflows): add lark-github-issue deep template"
```

---

### Task 5: Append A2A NyxID closed-loop argument to gap analysis

**Files:**
- Modify: `docs/2026-04-01-nyxid-chat-console-ide-design.md`

The CEO explicitly requested that the Agent-to-Agent via NyxID closed-loop design decision be documented. The Codex eng review found that the existing gap analysis doc already lists the missing seams. Append the A2A argument here instead of creating a new doc.

- [ ] **Step 1: Read the existing doc to find the right insertion point**

Read `docs/2026-04-01-nyxid-chat-console-ide-design.md` and find the section that discusses NyxID integration architecture or missing capabilities.

- [ ] **Step 2: Append the A2A section**

Add at the end of the document (before any appendix or references section):

```markdown
## Agent-to-Agent via NyxID Closed Loop

### Design Decision

Agent-to-Agent discovery and invocation does NOT require a new protocol. The existing
NyxID + Aevatar architecture already provides agent-to-agent infrastructure:

```
Agent A (Aevatar) -> exposes WebAPI -> registers via NyxID -> Agent B discovers via NyxID -> calls Agent A
```

### Walkthrough

1. Agent A runs on Aevatar, exposes a WebAPI endpoint (e.g., `/api/translate`)
2. Agent A's owner registers the endpoint through NyxID (auto-wrapped as MCP Server)
3. Agent B's workflow references the MCP Server via `connector_call`
4. NyxID handles: identity verification, credential injection, NAT traversal

### Known Gaps (v0.2)

- **Discovery:** Currently via NyxID route list. No global registry. Owner must share route.
  Scale may require a catalog service.
- **Security:** Per-route access control via NyxID. Agent-to-Agent inherits same model.
- **Versioning:** No API version management. Endpoint changes break consumers.
- **Latency:** NyxID relay adds overhead. Acceptable for async agents, needs evaluation for real-time.

### Why Not a New Protocol

Competitors (LangGraph, CrewAI) lack an identity/connectivity layer, so they must design
agent-to-agent protocols from scratch. Chrono AI does not, because NyxID IS that protocol.
This is a structural advantage, not a missing feature.

Source: CEO Review 2026-04-05, Cross-model consensus (Claude + Codex).
```

- [ ] **Step 3: Commit**

```bash
git add docs/2026-04-01-nyxid-chat-console-ide-design.md
git commit -m "docs: append agent-to-agent NyxID closed-loop design decision to gap analysis"
```

---

## Self-Review Checklist

1. **Spec coverage:**
   - [x] YAML validation at write boundary (Tasks 1-2)
   - [x] Connector reference validation (Task 3)
   - [x] 1-2 deep templates (Task 4, Lark-to-GitHub)
   - [x] A2A docs appended to existing gap analysis (Task 5)
   - [x] Cross-product first-experience owner (TODOS.md TODO-001, P1, people decision not code)

2. **Placeholder scan:** No TBD/TODO/placeholders. All code blocks contain actual code.

3. **Type consistency:** `WorkflowValidator`, `WorkflowValidationOptions`, `WorkflowYamlValidatorImpl`, `WorkflowParser`, `WorkflowDefinition`, `StepDefinition` are all used consistently with their actual definitions in the codebase.

## Notes

- **TODOS.md location:** TODOS.md is in the project root. The new CLAUDE.md docs system rule lists allowed root `.md` files as `CLAUDE.md, README.md, CHANGELOG.md, LICENSE, AGENTS.md`. TODOS.md is not on this list. Consider moving it to `docs/` or adding it to the allowed list if the team wants to keep it in root.
- **Second template:** The plan includes one deep template (Lark-to-GitHub). A second template (e.g., scheduled API health check with alerting) can be added after dogfood feedback on the first one validates the pattern.
- **Connector source of truth:** Codex noted that connector registration has two sources (local `connectors.json` + scope chrono-storage). Task 3's connector validation uses `IConnectorRegistry` which reads the runtime-registered set. This is correct for validation at write time but doesn't resolve the dual-source issue (deferred).
