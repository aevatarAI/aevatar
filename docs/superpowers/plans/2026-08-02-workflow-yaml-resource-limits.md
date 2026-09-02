# Workflow YAML Resource Limits Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reject oversized, over-populated, and over-deep workflow YAML before YamlDotNet builds recursive object graphs, with one boundary shared by every workflow ingress.

**Architecture:** `WorkflowYamlResourceGuard` performs an allocation-free UTF-8 byte check followed by iterative YamlDotNet event scanning and bounded alias-graph expansion. `WorkflowParser` and Studio's `YamlWorkflowDocumentService` invoke it before any `YamlStream.Load` or typed deserialization, while Application parse results preserve a strong resource-limit failure code.

**Tech Stack:** .NET 10, C#, YamlDotNet 16.3, xUnit, FluentAssertions

## Global Constraints

- Maximum UTF-8 encoded YAML size is exactly 1,048,576 bytes, inclusive.
- Maximum YAML node count is exactly 10,000 nodes, inclusive.
- Maximum open mapping/sequence nesting depth is exactly 64, inclusive.
- Node and depth limits apply after collection aliases are expanded; alias cycles fail closed.
- Limits are fixed safety invariants and are not host configuration.
- The guard must run before `YamlStream.Load` and `IDeserializer.Deserialize`.
- Never catch `StackOverflowException`.
- Runtime, Chat, Studio, service revision, fork, and dynamic workflow paths must use the same guard.
- Resource failures must map to existing 4xx validation paths and never echo submitted YAML.
- Product code changes require a failing regression test first.

---

### Task 1: Iterative Workflow YAML Resource Guard

**Files:**
- Create: `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowYamlResourceGuard.cs`
- Create: `test/Aevatar.Workflow.Core.Tests/Primitives/WorkflowYamlResourceGuardTests.cs`
- Modify: `src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowParser.cs:87-90`
- Modify: `test/Aevatar.Workflow.Core.Tests/Modules/WorkflowRuntimeModuleBranchTests.cs`

**Interfaces:**
- Produces: `WorkflowYamlResourceLimitKind`, `WorkflowYamlResourceLimitException`, and `WorkflowYamlResourceGuard.Validate(string yaml)`.
- Consumed by: runtime `WorkflowParser`, Infrastructure parse classification, Studio document parsing, and dynamic workflow validation.

- [x] **Step 1: Write failing core parser tests**

Create tests that generate `steps[].children` rather than a generic YAML tree so the regression exactly matches issue #3041:

```csharp
[Fact]
public void Parse_WhenChildrenDepthIsBelowLimit_ShouldSucceed()
{
    var yaml = BuildNestedWorkflow(childLinks: 30); // maximum open collection depth: 63

    var workflow = new WorkflowParser().Parse(yaml);

    workflow.Name.Should().Be("nested");
}

[Fact]
public void Parse_WhenChildrenDepthExceedsLimit_ShouldRejectBeforeDeserialization()
{
    var yaml = BuildNestedWorkflow(childLinks: 31); // maximum open collection depth: 65

    var act = () => new WorkflowParser().Parse(yaml);

    act.Should().Throw<WorkflowYamlResourceLimitException>()
        .Which.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.NestingDepth);
}

[Fact]
public void Parse_WhenUtf8BytesExceedLimit_ShouldRejectWithTypedLimit()
{
    var yaml = $"name: oversized\\ndescription: {new string('a', WorkflowYamlResourceGuard.MaxUtf8Bytes)}\\nroles: []\\nsteps: []\\n";

    var act = () => new WorkflowParser().Parse(yaml);

    act.Should().Throw<WorkflowYamlResourceLimitException>()
        .Which.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.Utf8Bytes);
}

[Fact]
public void Parse_WhenNodeCountExceedsLimit_ShouldRejectWithTypedLimit()
{
    var parameters = string.Join('\n', Enumerable.Range(0, 5_100).Select(index => $"      key_{index}: value_{index}"));
    var yaml = $"name: nodes\\nroles: []\\nsteps:\\n  - id: assign\\n    type: assign\\n    parameters:\\n{parameters}\\n";

    var act = () => new WorkflowParser().Parse(yaml);

    act.Should().Throw<WorkflowYamlResourceLimitException>()
        .Which.LimitKind.Should().Be(WorkflowYamlResourceLimitKind.Nodes);
}
```

The helper writes one root step and then alternates a `children` sequence and child mapping with two spaces of additional indentation for each link.

Also pass the 31-link fixture to `DynamicWorkflowModule.ValidateWorkflowYaml` and assert a single error containing `YAML parse failed` and `nesting depth`. This test must be written before the guard because the dynamic module is a direct `WorkflowParser` ingress.

- [x] **Step 2: Run tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter FullyQualifiedName~WorkflowYamlResourceGuardTests
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter FullyQualifiedName~DynamicWorkflowModule
```

Expected: compilation fails because `WorkflowYamlResourceGuard`, `WorkflowYamlResourceLimitException`, and `WorkflowYamlResourceLimitKind` do not exist.

- [x] **Step 3: Implement the iterative guard**

Create the new guard with fixed public constants and no configuration surface:

```csharp
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Aevatar.Workflow.Core.Primitives;

public enum WorkflowYamlResourceLimitKind
{
    Utf8Bytes = 1,
    Nodes = 2,
    NestingDepth = 3,
}

public sealed class WorkflowYamlResourceLimitException : InvalidOperationException
{
    public WorkflowYamlResourceLimitException(
        WorkflowYamlResourceLimitKind limitKind,
        int actual,
        int maximum)
        : base($"Workflow YAML {Format(limitKind)} limit exceeded: {actual} > {maximum}.")
    {
        LimitKind = limitKind;
        Actual = actual;
        Maximum = maximum;
    }

    public WorkflowYamlResourceLimitKind LimitKind { get; }
    public int Actual { get; }
    public int Maximum { get; }

    private static string Format(WorkflowYamlResourceLimitKind kind) => kind switch
    {
        WorkflowYamlResourceLimitKind.Utf8Bytes => "UTF-8 byte",
        WorkflowYamlResourceLimitKind.Nodes => "node count",
        WorkflowYamlResourceLimitKind.NestingDepth => "nesting depth",
        _ => "resource",
    };
}

public static class WorkflowYamlResourceGuard
{
    public const int MaxUtf8Bytes = 1024 * 1024;
    public const int MaxNodes = 10_000;
    public const int MaxNestingDepth = 64;

    public static void Validate(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var utf8Bytes = Encoding.UTF8.GetByteCount(yaml);
        ThrowIfExceeded(WorkflowYamlResourceLimitKind.Utf8Bytes, utf8Bytes, MaxUtf8Bytes);

        var parser = new Parser(new StringReader(yaml));
        var nodes = 0;
        var depth = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case MappingStart:
                case SequenceStart:
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.Nodes, ++nodes, MaxNodes);
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.NestingDepth, ++depth, MaxNestingDepth);
                    break;
                case Scalar:
                case AnchorAlias:
                    ThrowIfExceeded(WorkflowYamlResourceLimitKind.Nodes, ++nodes, MaxNodes);
                    break;
                case MappingEnd:
                case SequenceEnd:
                    depth--;
                    break;
            }
        }
    }

    private static void ThrowIfExceeded(WorkflowYamlResourceLimitKind kind, int actual, int maximum)
    {
        if (actual > maximum)
            throw new WorkflowYamlResourceLimitException(kind, actual, maximum);
    }
}
```

Call `WorkflowYamlResourceGuard.Validate(yaml);` as the first statement in `WorkflowParser.Parse`, before `ValidateRootSchema(yaml)`.

- [x] **Step 4: Run focused tests and verify GREEN**

Run both filtered Core test commands. Expected: all resource-guard and dynamic-workflow tests pass without process termination.

- [x] **Step 5: Run the existing parser suite**

Run:

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --filter FullyQualifiedName~WorkflowParser
```

Expected: existing parser behavior remains green.

- [x] **Step 6: Commit the core guard**

```bash
git add src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowYamlResourceGuard.cs \
  src/workflow/Aevatar.Workflow.Core/Primitives/WorkflowParser.cs \
  test/Aevatar.Workflow.Core.Tests/Primitives/WorkflowYamlResourceGuardTests.cs \
  test/Aevatar.Workflow.Core.Tests/Modules/WorkflowRuntimeModuleBranchTests.cs
git commit -m "Guard workflow YAML resource usage"
```

---

### Task 2: Typed Application Parse Classification

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowRunPorts.cs:5-56`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowDefinitionParser.cs:37-87,108-110`
- Modify: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowInfrastructureCoverageTests.cs`
- Modify: `test/Aevatar.Workflow.Application.Tests/WorkflowRunControlAndAbstractionsCoverageTests.cs:153-170`

**Interfaces:**
- Consumes: `WorkflowYamlResourceLimitException` from Task 1.
- Produces: `WorkflowYamlParseErrorCode` and `ErrorCode` on single-document and inline-bundle parse results.

- [x] **Step 1: Write failing result and Infrastructure tests**

Add assertions that ordinary invalid results default to `InvalidYaml`, successful results use `None`, a deeply nested real parser result uses `ResourceLimit`, and an inline bundle propagates `ResourceLimit`:

```csharp
result.Succeeded.Should().BeFalse();
result.ErrorCode.Should().Be(WorkflowYamlParseErrorCode.ResourceLimit);
result.Error.Should().Contain("nesting depth");
```

- [x] **Step 2: Run tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter FullyQualifiedName~WorkflowRunControlAndAbstractionsCoverageTests
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter FullyQualifiedName~WorkflowInfrastructureCoverageTests
```

Expected: compilation fails because `WorkflowYamlParseErrorCode` and `ErrorCode` do not exist.

- [x] **Step 3: Add the typed result contract**

Append an optional `ErrorCode` parameter to both parse records so existing callers remain source-compatible:

```csharp
public enum WorkflowYamlParseErrorCode
{
    None = 0,
    InvalidYaml = 1,
    ResourceLimit = 2,
}
```

`Success` sets `None`; `Invalid` defaults to `InvalidYaml` and accepts an explicit final `errorCode` parameter. The inline parser passes `parseResult.ErrorCode` when a child document fails.

- [x] **Step 4: Classify the strong exception**

Add this catch before the existing external-capability and generic catches:

```csharp
catch (WorkflowYamlResourceLimitException ex)
{
    return Task.FromResult(WorkflowYamlParseResult.Invalid(
        ex.Message,
        errorCode: WorkflowYamlParseErrorCode.ResourceLimit));
}
```

- [x] **Step 5: Run focused tests and verify GREEN**

Run both commands from Step 2. Expected: both projects pass their focused tests.

- [x] **Step 6: Commit typed classification**

```bash
git add src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/WorkflowRunPorts.cs \
  src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowDefinitionParser.cs \
  test/Aevatar.Workflow.Application.Tests/WorkflowRunControlAndAbstractionsCoverageTests.cs \
  test/Aevatar.Workflow.Host.Api.Tests/WorkflowInfrastructureCoverageTests.cs
git commit -m "Classify workflow YAML resource failures"
```

---

### Task 3: Studio Document Ingress

**Files:**
- Modify: `src/Aevatar.Studio.Infrastructure/Aevatar.Studio.Infrastructure.csproj`
- Modify: `src/Aevatar.Studio.Infrastructure/Serialization/YamlWorkflowDocumentService.cs:1-75`
- Modify: `test/Aevatar.Studio.Tests/WorkflowCompatibilityProfileTests.cs`

**Interfaces:**
- Consumes: `WorkflowYamlResourceGuard` and `WorkflowYamlResourceLimitException` from Task 1.
- Produces: Studio validation finding code `yaml_resource_limit` before representation-model parsing.

- [x] **Step 1: Write failing Studio tests**

Test a 31-link children chain and assert:

```csharp
result.Document.Should().BeNull();
result.Findings.Should().ContainSingle(finding =>
    finding.Path == "/" && finding.Code == "yaml_resource_limit");
```

Also parse the 30-link fixture and assert a non-null document so the accepted boundary remains covered.

- [x] **Step 2: Run the Studio test group and verify RED**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter FullyQualifiedName~WorkflowCompatibilityProfileTests
```

Expected: Studio over-depth parsing reaches its own `YamlStream.Load` instead of producing `yaml_resource_limit`.

- [x] **Step 3: Guard Studio before representation-model parsing**

Add a direct `Aevatar.Workflow.Core` project reference to Studio Infrastructure. At the start of `Parse`, after the existing empty check and before creating/loading `YamlStream`, run the guard and translate only its strong exception:

```csharp
try
{
    WorkflowYamlResourceGuard.Validate(yaml);
}
catch (WorkflowYamlResourceLimitException exception)
{
    return new WorkflowParseResult(
        null,
        [ValidationFinding.Error("/", exception.Message, code: "yaml_resource_limit")]);
}
```

- [x] **Step 4: Run Studio tests and verify GREEN**

Run the command from Step 2. Expected: all selected tests pass.

- [x] **Step 5: Commit ingress coverage**

```bash
git add src/Aevatar.Studio.Infrastructure/Aevatar.Studio.Infrastructure.csproj \
  src/Aevatar.Studio.Infrastructure/Serialization/YamlWorkflowDocumentService.cs \
  test/Aevatar.Studio.Tests/WorkflowCompatibilityProfileTests.cs
git commit -m "Enforce YAML limits across workflow ingresses"
```

---

### Task 4: Verification and Delivery

**Files:**
- Modify: `docs/superpowers/specs/2026-08-02-workflow-yaml-resource-limits-design.md`
- Modify: `docs/superpowers/plans/2026-08-02-workflow-yaml-resource-limits.md`

**Interfaces:**
- Consumes: all completed behavior from Tasks 1-3.
- Produces: reviewable #3041 branch and PR targeting `feature/integrate`.

- [x] **Step 1: Mark plan checkboxes and design status accurately**

Change the design frontmatter status to `Implemented and verified` only after all required commands succeed. Mark each completed plan checkbox `[x]`.

- [x] **Step 2: Run mandatory local verification**

```bash
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
git diff --check
```

Expected: every command exits 0. Record existing warning counts without treating baseline warnings as new failures.

- [x] **Step 3: Perform independent review**

Review only the diff from the #3041 base commit through branch HEAD. Reject changes that bypass the guard, configure weaker limits, miscount YAML collections, lose typed classification, or omit an ingress. Address every Critical or Important finding with a new failing test before changing product code.

The independent review found that collection aliases could create cyclic or
exponentially expanding object graphs after the syntactic event scan. The fix added a
bounded anchor graph, iterative expanded-limit validation, exact inclusive-boundary
tests, cyclic and exponential alias tests, and ingress coverage for runtime,
Infrastructure, Studio, and dynamic workflow parsing.

The second review found that aliases seen before their anchors were left unresolved by
the guard even though Studio's `YamlStream` resolves them at document completion. The
follow-up fix retains unresolved aliases per document, resolves them at `DocumentEnd`,
and adds forward-cycle and forward-expansion regressions across the guard, Runtime parse
classification, and Studio ingress.

The third review reported no Critical or Important findings and marked the change ready
after verification. Its three Minor test suggestions were also accepted to pin backward
binding under anchor redefinition, missing-anchor syntax handling, and cross-document
anchor isolation.

- [x] **Step 4: Re-run verification after review fixes**

Repeat all commands from Step 2 and the focused tests from Tasks 1-3. Expected: every command exits 0 with a clean working tree after the final commit.

- [ ] **Step 5: Push and create the PR**

Push `fix/2026-08-02_issue-3041-workflow-yaml-limits` and create a ready PR targeting `feature/integrate`, with `Closes #3041`, design summary, affected paths, and exact verification results.

- [ ] **Step 6: Wait for GitHub CI and merge only when green**

Require every applicable GitHub check to succeed. Merge PR #3041 alone into `feature/integrate`, verify the remote target contains the head commit, and close #3041 manually if GitHub does not auto-close it on the non-default base.
