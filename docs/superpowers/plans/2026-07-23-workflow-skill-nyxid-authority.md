# Workflow Skill NyxID Authority Propagation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make public one-shot workflow skill invocation preserve the authenticated caller's typed NyxID authority and resolved binding through workflow dispatch.

**Architecture:** `WorkflowSkillsEndpoints.InvokeSkill` will reuse `WorkflowCallerCredentialExtractor.ExtractAsync`, which is already authoritative for workflow chat ingress, while scope attribution continues through `AevatarScopeAccessGuard`. `UserSkillRunService` will use the typed credential's normalized bearer only for Ornn lookup and pass the same complete credential instance into `WorkflowChatRunRequest`.

**Tech Stack:** .NET 10, ASP.NET Core minimal endpoints, xUnit, FluentAssertions, NSubstitute.

## Global Constraints

- Never infer the NyxID external user ID from the workflow observatory `scopeId`; tests use `workflow.scope_id = "scope-alpha"` and `uid = "nyx-user-alpha"`.
- Do not change `ScheduleSkill`, `ScheduleAsync`, scheduled credential provisioning, Vault storage, or chrono-sandbox behavior.
- Do not log or serialize bearer tokens, agent keys, or delegation tokens.
- Keep the credential strongly typed as `WorkflowCallerCredential`; do not use metadata or header bags for identity.
- Use TDD: observe the regression tests fail before changing production code.

---

### Task 1: Endpoint Trusted Credential Extraction

**Files:**
- Create: `test/Aevatar.Capabilities.Tests/WorkflowSkillsEndpointsTests.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Skills/WorkflowSkillsEndpoints.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Skills/UserSkillRunModels.cs`

**Interfaces:**
- Consumes: `WorkflowCallerCredentialExtractor.ExtractAsync(HttpContext?, IExternalIdentityBindingQueryPort?, ILogger?, CancellationToken)`.
- Produces: `IUserSkillRunService.InvokeOnceAsync(string skillGuid, WorkflowCallerCredential callerCredential, string scopeId, string prompt, CancellationToken ct = default)`.

- [ ] **Step 1: Write the successful extraction regression test**

Create a test that invokes `WorkflowSkillsEndpoints.InvokeSkill` with `Authorization: Bearer caller-token`, authenticated claims `workflow.scope_id=scope-alpha` and `uid=nyx-user-alpha`, and a binding query returning `binding-alpha`. Capture the `WorkflowCallerCredential` passed to the run service and assert:

```csharp
capturedCredential.Should().BeEquivalentTo(new WorkflowCallerCredential(
    "caller-token",
    new WorkflowCallerNyxIdAuthority(
        "nyxid",
        string.Empty,
        "nyx-user-alpha",
        "proxy",
        "binding-alpha")));
capturedScopeId.Should().Be("scope-alpha");
bindingSubject.Should().BeEquivalentTo(new ExternalSubjectRef
{
    Platform = "nyxid",
    Tenant = string.Empty,
    ExternalUserId = "nyx-user-alpha",
});
```

- [ ] **Step 2: Write missing and malformed credential regression tests**

Use a theory with no authorization header and with `Authorization: Bearer token with spaces`. For each case, assert HTTP 401 through `IStatusCodeHttpResult` and assert `IUserSkillRunService.InvokeOnceAsync` was not called.

- [ ] **Step 3: Run the endpoint tests to verify RED**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~WorkflowSkillsEndpointsTests
```

Expected: FAIL because `IUserSkillRunService.InvokeOnceAsync` still accepts `string accessToken` and `InvokeSkill` does not resolve a binding or typed authority.

- [ ] **Step 4: Change the one-shot service contract**

Replace only the one-shot signature with:

```csharp
Task<SkillRunOutcome> InvokeOnceAsync(
    string skillGuid,
    WorkflowCallerCredential callerCredential,
    string scopeId,
    string prompt,
    CancellationToken ct = default);
```

Add the `Aevatar.Workflow.Application.Abstractions.Runs` import. Leave `ScheduleAsync` unchanged.

- [ ] **Step 5: Reuse the workflow caller extractor in the endpoint**

Resolve `IExternalIdentityBindingQueryPort` and `ILoggerFactory` from `http.RequestServices`, call `WorkflowCallerCredentialExtractor.ExtractAsync`, and return 401 unless extraction succeeds with a non-null credential and non-empty bearer. Pass the extracted credential unchanged to `InvokeOnceAsync`; continue deriving `scopeId` independently with `AevatarScopeAccessGuard`.

- [ ] **Step 6: Run the endpoint tests to verify GREEN**

Run the command from Step 3. Expected: both endpoint tests PASS.

### Task 2: Service Credential Preservation

**Files:**
- Create: `test/Aevatar.Capabilities.Tests/UserSkillRunServiceTests.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Skills/UserSkillRunService.cs`

**Interfaces:**
- Consumes: the typed `IUserSkillRunService.InvokeOnceAsync` signature from Task 1.
- Produces: Ornn lookup with the normalized bearer and `WorkflowChatRunRequest.CallerCredential` referencing the complete input credential.

- [ ] **Step 1: Write the service regression test**

Use a remote skill containing a minimal workflow YAML and an accepted dispatch result. Invoke with:

```csharp
var callerCredential = new WorkflowCallerCredential(
    "caller-token",
    new WorkflowCallerNyxIdAuthority(
        "nyxid",
        string.Empty,
        "nyx-user-alpha",
        "proxy",
        "binding-alpha"));
```

Capture the Ornn fetch and dispatched request, then assert:

```csharp
fetchAccessToken.Should().Be("caller-token");
dispatchedRequest!.ScopeId.Should().Be("scope-alpha");
dispatchedRequest.CallerCredential.Should().BeSameAs(callerCredential);
dispatchedRequest.CallerCredential!.NyxIdAuthority!.ExternalUserId
    .Should().Be("nyx-user-alpha");
```

- [ ] **Step 2: Run the service test to verify RED**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~UserSkillRunServiceTests
```

Expected: FAIL until `UserSkillRunService` accepts and forwards the complete credential.

- [ ] **Step 3: Implement minimal service propagation**

Parse `callerCredential.BearerToken` with `WorkflowCallerCredentialTokens.ParseOptional`; return `SkillRunOutcome.Failed("invalid_caller_credential", "Caller credential is invalid.")` when it is not valid. Use `NormalizedBearerToken` for `_remoteSkillFetcher.FetchSkillAsync`, and construct the run request with:

```csharp
CallerCredential: callerCredential
```

Do not create a new credential and do not read or derive identity from `scopeId`.

- [ ] **Step 4: Run service and endpoint tests to verify GREEN**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~WorkflowSkillsEndpointsTests|FullyQualifiedName~UserSkillRunServiceTests"
```

Expected: all new regression tests PASS.

### Task 3: Rollout Contract And Verification

**Files:**
- Modify: `docs/operations/2026-07-16-managed-codex-exec-rollout.md`

**Interfaces:**
- Consumes: the public skill invocation path fixed in Tasks 1 and 2.
- Produces: operator/developer guidance stating that skill invocation and workflow chat share the trusted identity extraction contract.

- [ ] **Step 1: Update the workflow proof section**

Add this statement before the expected workflow result:

```markdown
The public skill invoke endpoint uses the same trusted caller-credential extraction path as workflow chat. It resolves the authenticated NyxID subject and binding into typed `WorkflowCallerNyxIdAuthority` independently of the observatory `scopeId`; a bearer-only workflow credential is a deployment regression.
```

- [ ] **Step 2: Run focused verification**

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
bash tools/ci/test_stability_guards.sh
bash tools/docs/lint.sh
```

Expected: capability tests and both guards exit 0.

- [ ] **Step 3: Run repository verification**

```bash
bash tools/ci/architecture_guards.sh
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
```

Expected: all commands exit 0 with no test failures; pre-existing analyzer or package warnings may remain.

- [ ] **Step 4: Inspect the final patch**

```bash
git status --short
git diff --check
git diff --stat
git diff -- src/Aevatar.Mainnet.Host.Api/Skills/WorkflowSkillsEndpoints.cs src/Aevatar.Mainnet.Host.Api/Skills/UserSkillRunModels.cs src/Aevatar.Mainnet.Host.Api/Skills/UserSkillRunService.cs test/Aevatar.Capabilities.Tests/WorkflowSkillsEndpointsTests.cs test/Aevatar.Capabilities.Tests/UserSkillRunServiceTests.cs docs/operations/2026-07-16-managed-codex-exec-rollout.md
```

Expected: only the planned implementation, tests, and documentation are present, and `git diff --check` exits 0.
