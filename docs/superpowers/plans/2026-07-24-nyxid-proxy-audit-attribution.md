# NyxID Proxy Audit Attribution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Aevatar audit every exact NyxID proxy invocation against its stable UserService identity and preserve only safe, repository-owned NyxID proxy failure codes.

**Architecture:** `NyxIdProxyTool` remains the only live proxy tool surface and delegates provider-owned result classification to `NyxIdProxyReceiptFactory`. The factory emits the same typed `nyxid.user-service/<user_service_id>` subject for success, authorization-required, and proxy failures; `ToolAuditRecordFactory` retains only an exact allowlist of NyxID proxy codes while continuing to redact arbitrary receipt text.

**Tech Stack:** .NET 10, C#, Protobuf-generated `AgentToolReceipt`, xUnit, FluentAssertions.

## Global Constraints

- Modify only Aevatar; do not change FKST, alert thresholds, NyxID services, or production configuration.
- Use `SubjectKind = "nyxid.user-service"` and the exact admitted `user_service_id` as `SubjectId`.
- Never derive resource identity from slug, path, call id, response content, or string patterns.
- Emit the same stable subject for successful and failed proxy results.
- Keep `CallId` as invocation correlation only.
- Preserve only exact `NYXID_PROXY_UNAUTHORIZED`, `NYXID_PROXY_FORBIDDEN`, and `NYXID_PROXY_HTTP_[1-5][0-9][0-9]` audit failure codes.
- Map arbitrary, provider-controlled, malformed, or token-shaped failure strings to `tool_error`.
- Do not expose arguments, paths, query strings, headers, credentials, downstream bodies, `ErrorMessage`, or raw `ResultJson` in audit artifacts.
- Do not change Protobuf fields, public signatures, proxy execution, workflow retry/fallback, workflow outcome mapping, or user-visible tool results.
- Do not restore the deleted `ConnectedServiceProxyTool`; current `origin/feature/integrate` routes exact invocations through `NyxIdProxyTool`.
- Run `bash tools/ci/test_stability_guards.sh` because tests change.

---

## File Map

| Responsibility | Files |
|---|---|
| Stable UserService receipt identity | `src/Aevatar.AI.ToolProviders.NyxId/NyxIdProxyReceiptFactory.cs` |
| Receipt regression coverage | `test/Aevatar.AI.Tests/NyxIdProxyToolExactIdentityTests.cs` |
| Safe audit failure-code allowlist | `src/Aevatar.AI.Core/Auditing/ToolAuditRecordFactory.cs` |
| Audit and redaction coverage | `test/Aevatar.AI.Core.Tests/Middleware/ToolExecutionAuditMiddlewareTests.cs` |
| Canonical audit semantics | `docs/canon/audit-trail.md` |
| Approved design alignment | `docs/superpowers/specs/2026-07-24-nyxid-proxy-audit-attribution-design.md` |

### Task 1: Integrate Current Remote History

**Files:**

- Preserve all current tracked files and the unrelated untracked `.superpowers/` directory.

**Interfaces:**

- Produces a local `feature/integrate` containing the latest remote history plus the user's three existing local documentation commits.
- Establishes the exact source tree against which the red-green tests run.

- [ ] **Step 1: Fetch and merge the remote branch**

```bash
git fetch origin feature/integrate
git merge --no-edit origin/feature/integrate
```

Expected: the merge preserves the local agent-profile documentation and NyxID audit design commits. It must not restore `ConnectedServiceProxyTool`.

- [ ] **Step 2: Inspect the merged ownership paths**

```bash
git status --short --branch
rg -n "NyxIdProxyReceiptFactory|ResolveFailureCode" \
  src/Aevatar.AI.ToolProviders.NyxId src/Aevatar.AI.Core test/Aevatar.AI.Tests test/Aevatar.AI.Core.Tests
```

Expected: the branch is no longer behind; `.superpowers/` remains untracked; the live provider path is `NyxIdProxyTool -> NyxIdProxyReceiptFactory`.

### Task 2: Stable NyxID UserService Receipt Identity

**Files:**

- Modify: `test/Aevatar.AI.Tests/NyxIdProxyToolExactIdentityTests.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdProxyReceiptFactory.cs`

**Interfaces:**

- Consumes `NyxIdProxyReceiptFactory.TryCreate(string, string, string, string?, string?, string?, string)`.
- Produces receipts with `SubjectKind = "nyxid.user-service"` and `SubjectId` equal to the normalized exact UserService id.
- Relies on the existing runtime normalizer to retain the original successful result; audit records continue to omit receipt result JSON.

- [ ] **Step 1: Add failing receipt identity tests**

Add these tests and extend the existing authorization test with the same subject assertions:

```csharp
[Fact]
public void CreateResultReceipt_WithSuccess_ShouldTargetExactUserService()
{
    var tool = CreateTool(new CountingHandler());

    var receipt = tool.CreateResultReceipt(
        "call-success",
        tool.Name,
        """{"service_id":"us-home-alpha","slug":"home-assistant","path":"/api/items"}""",
        """{"items":[]}""");

    receipt.Should().NotBeNull();
    receipt!.Status.Should().Be(AgentToolReceiptStatus.Success);
    receipt.SubjectKind.Should().Be("nyxid.user-service");
    receipt.SubjectId.Should().Be("us-home-alpha");
}

[Fact]
public void CreateResultReceipt_WithHttpFailure_ShouldTargetExactUserService()
{
    var tool = CreateTool(new CountingHandler());
    const string result =
        """{"error":true,"status":502,"body":"upstream bearer-secret"}""";

    var receipt = tool.CreateResultReceipt(
        "call-error",
        tool.Name,
        """{"service_id":"us-home-alpha","slug":"home-assistant","path":"/api/items?token=query-secret"}""",
        result);

    receipt.Should().NotBeNull();
    receipt!.Status.Should().Be(AgentToolReceiptStatus.Error);
    receipt.ErrorCode.Should().Be("NYXID_PROXY_HTTP_502");
    receipt.SubjectKind.Should().Be("nyxid.user-service");
    receipt.SubjectId.Should().Be("us-home-alpha");
    receipt.ToString().Should().NotContain("bearer-secret").And.NotContain("query-secret");
}
```

Add to `CreateResultReceipt_WithAuthorizationFailure_ShouldPreserveExactServiceIdentity`:

```csharp
receipt.SubjectKind.Should().Be("nyxid.user-service");
receipt.SubjectId.Should().Be("us-home-alpha");
```

- [ ] **Step 2: Run the receipt tests and verify RED**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore \
  --filter "FullyQualifiedName~NyxIdProxyToolExactIdentityTests.CreateResultReceipt"
```

Expected: success returns `null`; failure and authorization receipts have empty subject fields.

- [ ] **Step 3: Emit the stable subject from the shared factory**

Normalize `userServiceId` before result classification and add:

```csharp
private const string UserServiceSubjectKind = "nyxid.user-service";

private static void AttachUserServiceSubject(
    AgentToolReceipt receipt,
    string? normalizedUserServiceId)
{
    if (normalizedUserServiceId == null)
        return;

    receipt.SubjectKind = UserServiceSubjectKind;
    receipt.SubjectId = normalizedUserServiceId;
}
```

For a non-error result with a valid id, return:

```csharp
new AgentToolReceipt
{
    CallId = callId ?? string.Empty,
    ToolName = toolName ?? string.Empty,
    Status = AgentToolReceiptStatus.Success,
    SubjectKind = UserServiceSubjectKind,
    SubjectId = normalizedUserServiceId,
};
```

For error and authorization receipts, call `AttachUserServiceSubject` before returning. Continue setting `AuthorizationRequired.UserServiceId` from the same normalized id. If a success has no valid exact id, return `null` rather than fabricating identity.

- [ ] **Step 4: Run the receipt tests and verify GREEN**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore \
  --filter "FullyQualifiedName~NyxIdProxyToolExactIdentityTests.CreateResultReceipt"
```

Expected: all filtered tests pass.

- [ ] **Step 5: Commit the provider change**

```bash
git add src/Aevatar.AI.ToolProviders.NyxId/NyxIdProxyReceiptFactory.cs \
  test/Aevatar.AI.Tests/NyxIdProxyToolExactIdentityTests.cs
git commit -m "Fix NyxID proxy audit resource identity"
```

### Task 3: Safe NyxID Proxy Audit Failure Codes

**Files:**

- Modify: `test/Aevatar.AI.Core.Tests/Middleware/ToolExecutionAuditMiddlewareTests.cs`
- Modify: `src/Aevatar.AI.Core/Auditing/ToolAuditRecordFactory.cs`

**Interfaces:**

- Consumes trimmed `AgentToolReceipt.ErrorCode`.
- Produces exact stable audit codes for repository-owned NyxID proxy classifications.
- Retains `DefaultErrorCode(status)` for every other value.

- [ ] **Step 1: Add a failing allowlist theory**

```csharp
[Theory]
[InlineData("NYXID_PROXY_HTTP_502")]
[InlineData("NYXID_PROXY_UNAUTHORIZED")]
[InlineData("NYXID_PROXY_FORBIDDEN")]
public async Task InvokeAsync_WhenReceiptUsesOwnedNyxIdProxyFailureCode_ShouldPreserveIt(
    string failureCode)
{
    var appender = new RecordingAuditTrailAppender();
    var middleware = NewMiddleware(appender);
    var context = NewContext(
        new FakeAgentTool("nyxid_proxy"),
        AgentToolExecutionContext.Empty with
        {
            Caller = new AgentToolCallerContext("scope-nyxid", "owner-nyxid", null),
        });

    await middleware.InvokeAsync(context, () =>
    {
        context.Receipt = new AgentToolReceipt
        {
            CallId = "call-nyxid",
            ToolName = "nyxid_proxy",
            Status = AgentToolReceiptStatus.Error,
            SubjectKind = "nyxid.user-service",
            SubjectId = "us-home-alpha",
            ErrorCode = failureCode,
            ErrorMessage = "provider-secret-must-not-appear",
        };
        return Task.CompletedTask;
    });

    var record = appender.Records.Should().ContainSingle().Subject;
    record.ErrorCode.Should().Be(failureCode);
    record.Failure.Code.Should().Be(failureCode);
    record.Target.Kind.Should().Be("nyxid.user-service");
    record.Target.Id.Should().Be("us-home-alpha");
    AuditText(record).Should().NotContain("provider-secret-must-not-appear");
}
```

Change the hostile value in the existing compact-secret fallback test to `NYXID_PROXY_HTTP_502_compactSecretToken123`; keep the expected `tool_error` assertions.

- [ ] **Step 2: Run the allowlist theory and verify RED**

```bash
dotnet test test/Aevatar.AI.Core.Tests/Aevatar.AI.Core.Tests.csproj --nologo --no-restore \
  --filter "FullyQualifiedName~InvokeAsync_WhenReceiptUsesOwnedNyxIdProxyFailureCode_ShouldPreserveIt"
```

Expected: all three rows receive `tool_error` instead of the supplied stable code.

- [ ] **Step 3: Add the exact failure-code predicate**

```csharp
private const string NyxIdProxyHttpFailurePrefix = "NYXID_PROXY_HTTP_";

private static bool IsOwnedNyxIdProxyFailureCode(string? value)
{
    if (value is "NYXID_PROXY_UNAUTHORIZED" or "NYXID_PROXY_FORBIDDEN")
        return true;

    return value != null &&
           value.Length == NyxIdProxyHttpFailurePrefix.Length + 3 &&
           value.StartsWith(NyxIdProxyHttpFailurePrefix, StringComparison.Ordinal) &&
           value[NyxIdProxyHttpFailurePrefix.Length] is >= '1' and <= '5' &&
           value[NyxIdProxyHttpFailurePrefix.Length + 1] is >= '0' and <= '9' &&
           value[NyxIdProxyHttpFailurePrefix.Length + 2] is >= '0' and <= '9';
}
```

Normalize once in `ResolveFailureCode`, return that value only when the predicate succeeds, then apply the existing generic-code switch. Do not permit partial, case-insensitive, or suffix matches.

- [ ] **Step 4: Run the complete middleware test class and verify GREEN**

```bash
dotnet test test/Aevatar.AI.Core.Tests/Aevatar.AI.Core.Tests.csproj --nologo --no-restore \
  --filter "FullyQualifiedName~ToolExecutionAuditMiddlewareTests"
```

Expected: all tests pass, including hostile-string fallback and secret-redaction cases.

- [ ] **Step 5: Commit the audit policy**

```bash
git add src/Aevatar.AI.Core/Auditing/ToolAuditRecordFactory.cs \
  test/Aevatar.AI.Core.Tests/Middleware/ToolExecutionAuditMiddlewareTests.cs
git commit -m "Preserve safe NyxID proxy audit failures"
```

### Task 4: Canonical Audit Contract

**Files:**

- Modify: `docs/canon/audit-trail.md`
- Modify: `docs/superpowers/specs/2026-07-24-nyxid-proxy-audit-attribution-design.md`

**Interfaces:**

- Documents existing typed receipt fields; introduces no schema.
- Aligns the approved design with the upstream deletion of `ConnectedServiceProxyTool`.

- [ ] **Step 1: Add canonical semantics**

Append to `3.2 Tool-Execution Middleware`:

```markdown
Provider-owned receipts must identify a stable target resource consistently
across successful and failed invocations whenever the provider has an exact
resource identity. Invocation ids remain correlation only and must not replace
that resource identity. For example, NyxID proxy receipts target the exact
`nyxid.user-service` id on both success and failure.

Failure codes crossing into audit artifacts are allowlisted stable
classifications, never provider-controlled diagnostic text. The NyxID proxy
boundary may retain exact `NYXID_PROXY_UNAUTHORIZED`,
`NYXID_PROXY_FORBIDDEN`, and `NYXID_PROXY_HTTP_[1-5][0-9][0-9]` codes; other
values fall back to the generic tool failure classification. Raw error messages,
arguments, results, paths, headers, and credentials remain excluded.
```

- [ ] **Step 2: Align the design with current upstream structure**

State that current `origin/feature/integrate` has one live proxy surface, `NyxIdProxyTool`, and that this fix does not restore the deleted typed connected-service layer. Remove its obsolete test bullet.

- [ ] **Step 3: Run documentation checks**

```bash
bash tools/docs/lint.sh
git diff --check
```

Expected: docs lint reports zero errors and the diff check exits zero.

- [ ] **Step 4: Commit documentation**

```bash
git add docs/canon/audit-trail.md \
  docs/superpowers/specs/2026-07-24-nyxid-proxy-audit-attribution-design.md
git commit -m "Document stable proxy audit attribution"
```

### Task 5: Verification And Push

**Files:**

- Verify all Task 2-4 files.
- Preserve `.superpowers/` as unrelated untracked content.

**Interfaces:**

- Produces a tested `feature/integrate` and advances `origin/feature/integrate` to exactly the local `HEAD`.

- [ ] **Step 1: Run focused tests and builds**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore \
  --filter "FullyQualifiedName~NyxIdProxyToolExactIdentityTests"
dotnet test test/Aevatar.AI.Core.Tests/Aevatar.AI.Core.Tests.csproj --nologo --no-restore \
  --filter "FullyQualifiedName~ToolExecutionAuditMiddlewareTests"
dotnet build src/Aevatar.AI.ToolProviders.NyxId/Aevatar.AI.ToolProviders.NyxId.csproj --nologo --no-restore
dotnet build src/Aevatar.AI.Core/Aevatar.AI.Core.csproj --nologo --no-restore
```

Expected: all commands exit zero with no test failures or compiler errors.

- [ ] **Step 2: Run required guards**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: every command exits zero. Specialized query, projection, current-state, and workflow-binding guards are not independently required because those surfaces do not change.

- [ ] **Step 3: Verify outgoing history and worktree**

```bash
git fetch origin feature/integrate
git status --short --branch
git log --oneline origin/feature/integrate..HEAD
git diff --check origin/feature/integrate..HEAD
```

Expected: the branch is ahead and not behind; only unrelated `.superpowers/` is untracked; outgoing history includes the pre-existing agent-profile docs and this fix.

- [ ] **Step 4: Push and verify the remote ref**

```bash
git push origin HEAD:feature/integrate
git fetch origin feature/integrate
test "$(git rev-parse HEAD)" = "$(git rev-parse origin/feature/integrate)"
git status --short --branch
```

Expected: push advances the remote, the equality check exits zero, and status reports synchronization apart from `.superpowers/`.
