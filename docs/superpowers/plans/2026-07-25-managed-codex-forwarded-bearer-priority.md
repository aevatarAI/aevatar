# Managed Codex Forwarded Bearer Priority Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an eligible user invoke `codex_exec` transparently when the Aevatar ingress receives both the forwarded current-user bearer and a NyxID delegation token.

**Architecture:** Keep the existing typed `WorkflowCallerCredential` contract for the internal P0. At the workflow HTTP boundary, select a valid `Authorization: Bearer` credential before considering `X-NyxID-Delegation-Token`; preserve delegation-only fallback and fail closed when the selected credential is malformed. Managed Codex Application orchestration, Vault Agent Keys, chrono transport, and runner delegation remain unchanged.

**Tech Stack:** .NET 9, ASP.NET Core, xUnit, FluentAssertions, protobuf-backed workflow runtime, NyxID HTTP proxy.

## Global Constraints

- The internal P0 may use the forwarded user bearer; public rollout remains blocked.
- Caller identity remains derived from the authenticated principal, never from a raw credential or `scopeId`.
- Do not add a second workflow credential field, protobuf field, Actor-state field, or Infrastructure fallback.
- Do not change chrono-sandbox, OpenSandbox, runner, provider, or Vault credential contracts.
- A malformed present Authorization credential fails closed and does not fall back to delegation.
- A malformed unselected delegation header does not invalidate a valid Authorization bearer.
- Tests must be written and observed failing before production code changes.
- Run `bash tools/ci/test_stability_guards.sh` because tests are modified.

---

### Task 1: Correct Workflow Ingress Credential Selection

**Files:**
- Modify: `test/Aevatar.Workflow.Host.Api.Tests/ChatEndpointsInternalTests.cs`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowCallerCredentialExtractor.cs`

**Interfaces:**
- Consumes: `WorkflowCallerCredentialExtractor.Extract(HttpContext?)`
- Produces: unchanged `WorkflowCallerCredentialExtractionResult`

- [ ] **Step 1: Change the focused test to express the corrected precedence**

Replace the credential cases in
`WorkflowCallerCredentialExtractor_ShouldExposeMissingValidAndInvalidStatus`
with explicit Authorization-first and delegation-fallback assertions:

```csharp
var bothValidHttp = CreateHttpContext();
bothValidHttp.Request.Headers.Authorization = "Bearer forwarded-token";
bothValidHttp.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";

var malformedAuthorizationWithDelegationHttp = CreateHttpContext();
malformedAuthorizationWithDelegationHttp.Request.Headers.Authorization = "Bearer token with spaces";
malformedAuthorizationWithDelegationHttp.Request.Headers["X-NyxID-Delegation-Token"] = "delegation-token";

var validAuthorizationWithMalformedDelegationHttp = CreateHttpContext();
validAuthorizationWithMalformedDelegationHttp.Request.Headers.Authorization = "Bearer forwarded-token";
validAuthorizationWithMalformedDelegationHttp.Request.Headers["X-NyxID-Delegation-Token"] = "token with spaces";
```

Assert:

```csharp
var bothValid = WorkflowCallerCredentialExtractor.Extract(bothValidHttp);
var malformedAuthorizationWithDelegation =
    WorkflowCallerCredentialExtractor.Extract(malformedAuthorizationWithDelegationHttp);
var validAuthorizationWithMalformedDelegation =
    WorkflowCallerCredentialExtractor.Extract(validAuthorizationWithMalformedDelegationHttp);

bothValid.Succeeded.Should().BeTrue();
bothValid.Credential!.BearerToken.Should().Be("forwarded-token");
malformedAuthorizationWithDelegation.Succeeded.Should().BeFalse();
malformedAuthorizationWithDelegation.Error.Should().Be(
    WorkflowChatRunStartError.InvalidCallerCredential);
validAuthorizationWithMalformedDelegation.Succeeded.Should().BeTrue();
validAuthorizationWithMalformedDelegation.Credential!.BearerToken.Should().Be(
    "forwarded-token");
```

Keep assertions for missing credentials, valid Authorization-only credentials,
delegation-only credentials, bare Bearer, and malformed delegation-only
credentials.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj \
  --nologo \
  --filter 'FullyQualifiedName~WorkflowCallerCredentialExtractor_ShouldExposeMissingValidAndInvalidStatus'
```

Expected: FAIL because the existing extractor returns `delegation-token` when
both headers are valid and rejects a valid Authorization bearer when the
delegation header is malformed.

- [ ] **Step 3: Implement Authorization-first selection**

Replace `ExtractCredentialToken` with logic equivalent to:

```csharp
private static CallerCredentialTokenExtractionResult ExtractCredentialToken(
    HttpContext? http)
{
    if (http?.Request.Headers.TryGetValue("Authorization", out var authorizationValues) == true)
    {
        if (authorizationValues.Count != 1)
            return CallerCredentialTokenExtractionResult.Invalid;

        var authorization = authorizationValues[0];
        if (string.Equals(
                authorization?.Trim(),
                "Bearer",
                StringComparison.OrdinalIgnoreCase))
        {
            return CallerCredentialTokenExtractionResult.Invalid;
        }

        return authorization?.StartsWith(
                   BearerPrefix,
                   StringComparison.OrdinalIgnoreCase) == true
            ? CallerCredentialTokenExtractionResult.Success(
                authorization[BearerPrefix.Length..])
            : CallerCredentialTokenExtractionResult.Invalid;
    }

    if (http?.Request.Headers.TryGetValue(
            NyxIdDelegationTokenHeader,
            out var delegationValues) == true)
    {
        return delegationValues.Count != 1
            ? CallerCredentialTokenExtractionResult.Invalid
            : CallerCredentialTokenExtractionResult.Success(delegationValues[0]);
    }

    return CallerCredentialTokenExtractionResult.Missing;
}
```

Do not change caller-authority resolution or the typed workflow credential
mapping.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the command from Step 2.

Expected: PASS.

- [ ] **Step 5: Run the complete workflow-host test project**

Run:

```bash
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj \
  --nologo
```

Expected: all tests pass.

---

### Task 2: Align Canonical Documentation With the Internal P0 Boundary

**Files:**
- Modify: `docs/canon/nyxid-llm-integration.md`
- Modify: `docs/operations/2026-07-16-managed-codex-exec-rollout.md`

**Interfaces:**
- Consumes: credential-selection behavior from Task 1
- Produces: authoritative rollout guidance for operations and future code changes

- [ ] **Step 1: Update canonical workflow caller credential semantics**

Replace the statement that workflow ingress always prefers
`X-NyxID-Delegation-Token` with:

```text
The internal P0 prefers a valid forwarded Authorization bearer when both
credentials are present, because transparent managed Codex readiness must call
NyxID current-user and API-key management endpoints. Delegation remains the
fallback when Authorization is absent. Caller identity still comes only from
the validated principal. This temporary ordering must not be generalized into
a public-rollout guarantee.
```

- [ ] **Step 2: Update the managed Codex rollout prerequisites**

Add an explicit internal P0 prerequisite:

```text
The NyxID UserService that fronts Aevatar must temporarily forward the current
user access token. It may also inject a delegation token; Aevatar selects the
forwarded bearer for transparent readiness. The chrono-sandbox UserService
contract remains forward_access_token=false and inject_delegation_token=true.
```

State that disabling Aevatar access-token forwarding requires the later
dual-credential typed contract or a NyxID delegated self-service capability.

- [ ] **Step 3: Run documentation and stability guards**

Run:

```bash
bash tools/docs/lint.sh
bash tools/ci/test_stability_guards.sh
git diff --check
```

Expected: every command exits 0.

---

### Task 3: Verify, Commit, Push, and Re-run the Canary

**Files:**
- Verify all files changed by Tasks 1 and 2

**Interfaces:**
- Consumes: corrected extractor and canonical documentation
- Produces: a pushed Aevatar revision and production canary evidence

- [ ] **Step 1: Run managed Codex and architecture regression suites**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter 'FullyQualifiedName~NyxIdCodexExecToolTests'
bash tools/ci/architecture_guards.sh
dotnet build aevatar.slnx --nologo
```

Expected: tests and guards pass; build has zero errors.

- [ ] **Step 2: Commit the implementation**

Run:

```bash
git add \
  src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowCallerCredentialExtractor.cs \
  test/Aevatar.Workflow.Host.Api.Tests/ChatEndpointsInternalTests.cs \
  docs/canon/nyxid-llm-integration.md \
  docs/operations/2026-07-16-managed-codex-exec-rollout.md
git commit -m "Fix managed Codex caller bearer selection"
```

- [ ] **Step 3: Push without rewriting history**

Run:

```bash
git -c url."git@github.com:".insteadOf=https://github.com/ \
  push origin HEAD:feature/integrate
```

Expected: fast-forward update of `origin/feature/integrate`.

- [ ] **Step 4: Confirm the deployed immutable revision**

Run:

```bash
export KUBECONFIG=/Users/eanzhao/Code/aelf-shared-k8s-prod.yaml
kubectl -n aismart-app-mainnet get deploy aevatar-console-backend \
  -o jsonpath='{.spec.template.spec.containers[*].image}{"\n"}'
kubectl -n aismart-app-mainnet get pods -l app=aevatar-console-backend
```

Expected: the image tag matches the new pushed revision and the Pod is Ready
with no restart during the canary.

- [ ] **Step 5: Re-run the canonical workflow through the local NyxID CLI**

Submit an inline workflow containing:

```yaml
name: managed_codex_canary
roles: []
steps:
  - id: verify_managed_codex
    type: tool_call
    timeout_ms: 200000
    parameters:
      tool: codex_exec
      arguments: >-
        {"target":{"kind":"managed_sandbox"},"workspace":{"kind":"empty_git"},"prompt":"Reply with exactly CODEX_EXEC_READY","timeout_secs":180}
```

through:

```bash
nyxid proxy request aevatar /api/chat \
  -m POST \
  -H 'Content-Type:application/json' \
  -H 'Accept:text/event-stream' \
  -d - \
  --stream
```

Expected: the workflow succeeds and output trims exactly to
`CODEX_EXEC_READY`.

- [ ] **Step 6: Record redacted production evidence**

Using the scoped production kubeconfig, inspect the Aevatar run by run ID and
confirm:

- readiness no longer fails at `/api/v1/users/me`;
- chrono-sandbox is called;
- the terminal result is successful;
- no raw access token, Agent Key, or delegation token appears in logs;
- the sandbox cleanup is confirmed by the chrono diagnostic evidence available
  to operations.
