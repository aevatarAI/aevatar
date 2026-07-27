# Direct Responses NyxID Authority Propagation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve the authenticated caller's typed NyxID authority through Aevatar's Responses, Messages, and Chat Completions ingress so account-scoped tools such as managed `codex_exec` work transparently for every eligible user.

**Architecture:** The Mainnet Host remains the only identity-verification boundary. It adds a complete `AgentToolNyxIdAuthorityContext` to `ResponsesCallerScope` from the already-validated NyxID subject, and each Application facade copies that typed value into `AgentToolExecutionContext` without reconstructing identity from scope, owner, or credentials.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, Protobuf tool-context mapping, Aevatar Mainnet Host and GAgent Service Application.

## Global Constraints

- Use `origin/feature/integrate` commit `900ced56fb3ae5bee76d092d9b140eb45ab905e6` plus the approved documentation commits as the implementation base.
- Work on branch `fix/2026-07-27_responses-nyxid-authority` in an isolated worktree.
- `NyxIdResponsesCallerScopeResolver` is the only new producer of direct-ingress NyxID authority.
- Set `Platform=OwnerScope.NyxIdPlatform`, `Tenant=string.Empty`, and `ExternalUserId` to the normalized authenticated NyxID subject.
- Never infer `ExternalUserId` from `ScopeId`, `OwnerSubject`, route identity, workflow identity, service identity, or a delegation token.
- Keep `ResponsesCallerScope.NyxIdAuthority` strongly typed; do not use metadata, headers, annotations, items, or string-key bags.
- Responses, Messages, and Chat Completions must copy the exact `ResponsesCallerScope.NyxIdAuthority` object into `AgentToolExecutionContext.NyxIdAuthority`.
- Preserve existing bearer-token, route, session, streaming, dispatch, observation, feature-flag, allowlist, and fail-closed behavior.
- Do not modify NyxID, chrono-sandbox, OpenSandbox, runner images, Kubernetes configuration, or credential storage.
- Test fixtures must keep `ScopeId`, `OwnerSubject`, and `ExternalUserId` visibly different.
- Follow RED-GREEN-REFACTOR for every production change and record the failing and passing commands.

## File Map

- Modify: `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCommandContracts.cs`
  - Adds the typed authority field to the direct caller-scope contract.
- Modify: `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesCallerScope.cs`
  - Produces typed authority from the Host-validated NyxID subject.
- Modify: `test/Aevatar.Capabilities.Tests/ResponsesCallerScopeResolverTests.cs`
  - Covers identity-assertion, bearer fallback, normalization, and delegation non-substitution.
- Modify: `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCommandFacade.cs`
  - Copies typed authority into Responses tool context.
- Modify: `test/Aevatar.GAgentService.Tests/Application/ResponsesCommandFacadeTests.cs`
  - Proves serialized `LlmRunRequested.ToolContext` preserves the exact authority.
- Modify: `src/platform/Aevatar.GAgentService.Application/Responses/MessagesCommandFacade.cs`
  - Copies typed authority into Messages tool context.
- Modify: `test/Aevatar.GAgentService.Tests/Application/MessagesCommandFacadeTests.cs`
  - Proves serialized Messages run context preserves the exact authority.
- Modify: `src/platform/Aevatar.GAgentService.Application/Responses/ChatCompletionsCommandFacade.cs`
  - Copies typed authority into Chat Completions tool context.
- Modify: `test/Aevatar.GAgentService.Tests/Application/ChatCompletionsCommandFacadeTests.cs`
  - Proves serialized Chat Completions run context preserves the exact authority.

---

### Task 1: Extend the caller-scope contract and populate it at the Host boundary

**Files:**

- Modify: `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCommandContracts.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesCallerScope.cs`
- Test: `test/Aevatar.Capabilities.Tests/ResponsesCallerScopeResolverTests.cs`

**Interfaces:**

- Consumes: the existing validated identity-assertion subject or current-user ID returned by `INyxIdCurrentUserResolver`.
- Produces: `ResponsesCallerScope.NyxIdAuthority` as a non-null `AgentToolNyxIdAuthorityContext`, defaulting to `Empty` for non-NyxID resolver implementations.

- [ ] **Step 1: Add failing Host resolver assertions**

Add these imports to `ResponsesCallerScopeResolverTests.cs`:

```csharp
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
```

Extend `ResolveAsync_ShouldReturnTrimmedScope_WithApiKeyOrigin`:

```csharp
scope.NyxIdAuthority.Should().BeEquivalentTo(
    new AgentToolNyxIdAuthorityContext(
        OwnerScope.NyxIdPlatform,
        string.Empty,
        "alice-1"));
```

Extend `ResolveAsync_WithValidIdentityAssertion_ShouldResolveScopeWithoutCurrentUserLookup`:

```csharp
scope.NyxIdAuthority.Should().BeEquivalentTo(
    new AgentToolNyxIdAuthorityContext(
        OwnerScope.NyxIdPlatform,
        string.Empty,
        "identity-user"));
```

Extend `ResolveAsync_WithDelegationTokenOnly_ShouldNotResolveCallerIdentityFromDelegationToken`:

```csharp
scope.NyxIdAuthority.Should().BeEquivalentTo(
    new AgentToolNyxIdAuthorityContext(
        OwnerScope.NyxIdPlatform,
        string.Empty,
        "fallback-user"));
```

- [ ] **Step 2: Run the focused test class and verify RED**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj \
  --filter 'FullyQualifiedName~ResponsesCallerScopeResolverTests' \
  --nologo
```

Expected: FAIL at compile time because `ResponsesCallerScope` does not yet expose `NyxIdAuthority`.

- [ ] **Step 3: Add the typed scope property**

Change `ResponsesCallerScope` to:

```csharp
public sealed record ResponsesCallerScope(
    string ScopeId,
    string OwnerSubject,
    LlmSessionOriginKind OriginKind)
{
    public AgentToolNyxIdAuthorityContext NyxIdAuthority { get; init; } =
        AgentToolNyxIdAuthorityContext.Empty;
}
```

- [ ] **Step 4: Populate authority from the normalized authenticated subject**

Add these imports to `ResponsesCallerScope.cs`:

```csharp
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
```

Replace both the `normalizedUserId` local and the bearer fallback's direct
record construction with:

```csharp
return ScopeForSubject(userId);
```

Replace `ScopeForSubject` with:

```csharp
private static ResponsesCallerScope ScopeForSubject(string subject)
{
    var normalizedSubject = subject.Trim();
    return new ResponsesCallerScope(
        ScopeId: normalizedSubject,
        OwnerSubject: normalizedSubject,
        OriginKind: LlmSessionOriginKind.ApiKey)
    {
        NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
            OwnerScope.NyxIdPlatform,
            string.Empty,
            normalizedSubject),
    };
}
```

Do not read `context.NyxIdDelegationToken` while building authority.

- [ ] **Step 5: Run the focused test class and verify GREEN**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj \
  --filter 'FullyQualifiedName~ResponsesCallerScopeResolverTests' \
  --nologo
```

Expected: all `ResponsesCallerScopeResolverTests` pass with no warnings.

- [ ] **Step 6: Commit**

```bash
git add \
  src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCommandContracts.cs \
  src/Aevatar.Mainnet.Host.Api/Responses/ResponsesCallerScope.cs \
  test/Aevatar.Capabilities.Tests/ResponsesCallerScopeResolverTests.cs
git commit -m 'Preserve NyxID authority in responses scope'
```

### Task 2: Propagate typed authority through Responses

**Files:**

- Modify: `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCommandFacade.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/ResponsesCommandFacadeTests.cs`

**Interfaces:**

- Consumes: `ResponsesCallerScope.NyxIdAuthority` from Task 1.
- Produces: the same value in `LlmRunRequested.ToolContext.NyxIdAuthority`.

- [ ] **Step 1: Add the failing serialized-context assertion**

Add this field inside `ResponsesCommandFacadeTests`:

```csharp
private static readonly AgentToolNyxIdAuthorityContext TestNyxIdAuthority =
    new(OwnerScope.NyxIdPlatform, string.Empty, "nyx-user-alpha");
```

Change `StaticCallerScopeResolver.ResolveAsync` to:

```csharp
Task.FromResult(new ResponsesCallerScope(scopeId, ownerSubject, originKind)
{
    NyxIdAuthority = TestNyxIdAuthority,
});
```

Extend `CreateAsync_ShouldRegisterSession_AndExecuteRoutedNonStreamingRequest`:

```csharp
toolContext.NyxIdAuthority.Should().BeEquivalentTo(TestNyxIdAuthority);
toolContext.NyxIdAuthority.ExternalUserId.Should().NotBe(toolContext.Caller.ScopeId);
toolContext.NyxIdAuthority.ExternalUserId.Should().NotBe(toolContext.Caller.OwnerSubject);
```

- [ ] **Step 2: Run the exact test and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj \
  --filter 'FullyQualifiedName=Aevatar.GAgentService.Tests.Application.ResponsesCommandFacadeTests.CreateAsync_ShouldRegisterSession_AndExecuteRoutedNonStreamingRequest' \
  --nologo
```

Expected: FAIL because the decoded tool context contains `AgentToolNyxIdAuthorityContext.Empty`.

- [ ] **Step 3: Copy the typed authority into the Responses tool context**

Change `ResponsesCommandFacade.BuildToolContext` so the constructed context ends with:

```csharp
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal))
        {
            NyxIdAuthority = callerScope.NyxIdAuthority,
        };
```

Do not derive a replacement value from `callerScope.ScopeId` or
`callerScope.OwnerSubject`.

- [ ] **Step 4: Run the exact test and verify GREEN**

Run the same command as Step 2.

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add \
  src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCommandFacade.cs \
  test/Aevatar.GAgentService.Tests/Application/ResponsesCommandFacadeTests.cs
git commit -m 'Propagate NyxID authority through Responses'
```

### Task 3: Propagate typed authority through Messages

**Files:**

- Modify: `src/platform/Aevatar.GAgentService.Application/Responses/MessagesCommandFacade.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/MessagesCommandFacadeTests.cs`

**Interfaces:**

- Consumes: `ResponsesCallerScope.NyxIdAuthority` from Task 1.
- Produces: the same value in the serialized Messages `LlmRunRequested.ToolContext`.

- [ ] **Step 1: Add the failing serialized-context assertion**

Add this field inside `MessagesCommandFacadeTests`:

```csharp
private static readonly AgentToolNyxIdAuthorityContext TestNyxIdAuthority =
    new(OwnerScope.NyxIdPlatform, string.Empty, "nyx-user-alpha");
```

Change `StaticCallerScopeResolver.ResolveAsync` to preserve its existing scope
and owner fixtures while adding a deliberately different NyxID user:

```csharp
Task.FromResult(new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey)
{
    NyxIdAuthority = TestNyxIdAuthority,
});
```

Extend `CreateAsync_ShouldRegisterSession_AndReturnAcceptedDispatchReceipt`:

```csharp
toolContext.NyxIdAuthority.Should().BeEquivalentTo(TestNyxIdAuthority);
toolContext.NyxIdAuthority.ExternalUserId.Should().NotBe(toolContext.Caller.ScopeId);
toolContext.NyxIdAuthority.ExternalUserId.Should().NotBe(toolContext.Caller.OwnerSubject);
```

- [ ] **Step 2: Run the exact test and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj \
  --filter 'FullyQualifiedName=Aevatar.GAgentService.Tests.Application.MessagesCommandFacadeTests.CreateAsync_ShouldRegisterSession_AndReturnAcceptedDispatchReceipt' \
  --nologo
```

Expected: FAIL because the decoded tool context contains empty NyxID authority.

- [ ] **Step 3: Copy the typed authority into the Messages tool context**

Change `MessagesCommandFacade.BuildToolContext` so the constructed context ends with:

```csharp
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal))
        {
            NyxIdAuthority = callerScope.NyxIdAuthority,
        };
```

- [ ] **Step 4: Run the exact test and verify GREEN**

Run the same command as Step 2.

Expected: PASS.

- [ ] **Step 5: Run the whole Messages test class**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj \
  --filter 'FullyQualifiedName~MessagesCommandFacadeTests' \
  --nologo
```

Expected: all tests pass without changing existing scope expectations.

- [ ] **Step 6: Commit**

```bash
git add \
  src/platform/Aevatar.GAgentService.Application/Responses/MessagesCommandFacade.cs \
  test/Aevatar.GAgentService.Tests/Application/MessagesCommandFacadeTests.cs
git commit -m 'Propagate NyxID authority through Messages'
```

### Task 4: Propagate typed authority through Chat Completions

**Files:**

- Modify: `src/platform/Aevatar.GAgentService.Application/Responses/ChatCompletionsCommandFacade.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/ChatCompletionsCommandFacadeTests.cs`

**Interfaces:**

- Consumes: `ResponsesCallerScope.NyxIdAuthority` from Task 1.
- Produces: the same value in the serialized Chat Completions `LlmRunRequested.ToolContext`.

- [ ] **Step 1: Add the failing serialized-context assertion**

Add this field inside `ChatCompletionsCommandFacadeTests`:

```csharp
private static readonly AgentToolNyxIdAuthorityContext TestNyxIdAuthority =
    new(OwnerScope.NyxIdPlatform, string.Empty, "nyx-user-alpha");
```

Change `StaticCallerScopeResolver.ResolveAsync` to preserve its existing scope
and owner fixtures while adding a deliberately different NyxID user:

```csharp
Task.FromResult(new ResponsesCallerScope("scope-1", "owner-1", LlmSessionOriginKind.ApiKey)
{
    NyxIdAuthority = TestNyxIdAuthority,
});
```

Extend `CreateAsync_ShouldRegisterSession_AndDispatchLlmRun`:

```csharp
toolContext.NyxIdAuthority.Should().BeEquivalentTo(TestNyxIdAuthority);
toolContext.NyxIdAuthority.ExternalUserId.Should().NotBe(toolContext.Caller.ScopeId);
toolContext.NyxIdAuthority.ExternalUserId.Should().NotBe(toolContext.Caller.OwnerSubject);
```

- [ ] **Step 2: Run the exact test and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj \
  --filter 'FullyQualifiedName=Aevatar.GAgentService.Tests.Application.ChatCompletionsCommandFacadeTests.CreateAsync_ShouldRegisterSession_AndDispatchLlmRun' \
  --nologo
```

Expected: FAIL because the decoded tool context contains empty NyxID authority.

- [ ] **Step 3: Copy the typed authority into the Chat Completions tool context**

Change `ChatCompletionsCommandFacade.BuildToolContext` so the constructed context ends with:

```csharp
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal))
        {
            NyxIdAuthority = callerScope.NyxIdAuthority,
        };
```

- [ ] **Step 4: Run the exact test and verify GREEN**

Run the same command as Step 2.

Expected: PASS.

- [ ] **Step 5: Run the whole Chat Completions test class**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj \
  --filter 'FullyQualifiedName~ChatCompletionsCommandFacadeTests' \
  --nologo
```

Expected: all tests pass without changing existing scope expectations.

- [ ] **Step 6: Commit**

```bash
git add \
  src/platform/Aevatar.GAgentService.Application/Responses/ChatCompletionsCommandFacade.cs \
  test/Aevatar.GAgentService.Tests/Application/ChatCompletionsCommandFacadeTests.cs
git commit -m 'Propagate NyxID authority through Chat Completions'
```

### Task 5: Run regression gates and resume the private Skill live gate

**Files:**

- Verify all files changed by Tasks 1-4.
- Verify: `docs/superpowers/specs/2026-07-27-direct-responses-nyxid-authority-design.md`
- Verify: `docs/superpowers/plans/2026-07-27-direct-responses-nyxid-authority.md`
- Resume: `docs/superpowers/plans/2026-07-27-codex-exec-public-smoke-skill.md`

**Interfaces:**

- Consumes: all four reviewed implementation commits and a deployment built from their exact head.
- Produces: local regression evidence, deployed direct-ingress proof, and permission to resume public Ornn promotion.

- [ ] **Step 1: Run focused regression suites**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj \
  --filter 'FullyQualifiedName~ResponsesCallerScopeResolverTests' \
  --nologo

dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj \
  --filter 'FullyQualifiedName~ResponsesCommandFacadeTests|FullyQualifiedName~MessagesCommandFacadeTests|FullyQualifiedName~ChatCompletionsCommandFacadeTests' \
  --nologo
```

Expected: all selected tests pass with no warnings.

- [ ] **Step 2: Build the affected Host**

Run:

```bash
dotnet build src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj --nologo
```

Expected: build succeeds with zero warnings and zero errors.

- [ ] **Step 3: Run mandatory repository guards**

Run:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
git diff --check
```

Expected: every command exits `0`.

- [ ] **Step 4: Review branch scope**

Run:

```bash
git status --short --branch
git diff --stat origin/feature/integrate...HEAD
git log --oneline origin/feature/integrate..HEAD
```

Expected: only the approved documentation, caller-scope contract, Host
resolver, three facades, and their focused tests differ from
`origin/feature/integrate`.

- [ ] **Step 5: Deploy the exact reviewed head before the live gate**

Record the full commit SHA:

```bash
git rev-parse HEAD
```

Push the reviewed branch for integration. Do not run the live gate against an
older Aevatar image, and do not promote the Skill while deployment identity is
unknown.

- [ ] **Step 6: Re-run the private Skill through direct Responses**

After the exact commit is deployed, run:

```bash
response="$(
  nyxid proxy request aevatar \
    '/v1/responses' \
    --method POST \
    --header 'Content-Type:application/json' \
    --data '{
      "model": "chrono-llm/gpt-5.5",
      "input": "::verify-codex-exec",
      "max_output_tokens": 1200
    }' \
    --output json
)"

jq -e '
  .status == "completed" and
  ([.output[]? | select(.type == "function_call" and .name == "codex_exec")] | length) == 1 and
  (
    [
      .output[]?
      | select(.type == "function_call" and .name == "codex_exec")
      | (.arguments | fromjson)
      | select(
          .status == "succeeded" and
          .target == "managed_sandbox" and
          .exit_code == 0 and
          ((.output // "") | gsub("^\\s+|\\s+$"; "") == "CODEX_EXEC_READY")
        )
    ]
    | length
  ) == 1 and
  (
    [
      .output[]?
      | select(.type == "message")
      | .content[]?
      | select(.type == "output_text")
      | .text
    ]
    | join("\n")
    | startswith("AVAILABLE")
  )
' <<<"$response"
```

Expected: exit `0`.

- [ ] **Step 7: Resume public Ornn promotion only after GREEN**

Continue Task 4 of
`docs/superpowers/plans/2026-07-27-codex-exec-public-smoke-skill.md`.

If the private run fails, keep `verify-codex-exec@1.0` private, preserve the
redacted response and diagnostic IDs, and return to the failing Aevatar
boundary. Do not change NyxID or chrono-sandbox without new evidence.
