# Managed Codex Production Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make normal managed `codex_exec` consume only an execution-ready committed credential, expose honest readiness diagnostics, and keep every HTTP deadline outside chrono-sandbox's complete 180-second lifecycle.

**Architecture:** Add one pure Application readiness assessor over `ManagedCodexOptions`, owner identity, committed credential snapshot, and current time. The status endpoint and Workflow execution coordinator consume that same assessor; only explicit credential lifecycle endpoints retain mutation, distributed lease, NyxID/Vault reconciliation, Actor dispatch, and Projection observation. The chrono transport owns a 300-second maximum per-call deadline, while the shared NyxID `HttpClient` remains a 330-second transport backstop.

**Tech Stack:** .NET 10, C# 14, ASP.NET Core minimal APIs, protobuf-generated credential contracts, xUnit, FluentAssertions, NSubstitute, `FakeTimeProvider`, Rust/cargo verification for chrono-sandbox.

## Global Constraints

- Preserve `Domain / Application / Infrastructure / Host` layering and depend on abstractions across layer boundaries.
- Normal execution must not bind a readiness observation, acquire a mutation lease, contact NyxID for repair, mutate Vault, dispatch a credential Actor command, prime a projection, replay events, or poll.
- Credential status and execution readiness retain separate field meanings.
- The committed credential Actor state remains authoritative; readiness is a pure interpretation of its current-state read model.
- Do not expose raw keys, bearer tokens, Vault locators, fingerprints, prompts, or raw chrono output.
- No process-local owner/run/session registry, `Task.Delay`, `WaitUntilAsync`, synchronous actor request/reply, or query-time projection refresh.
- The production timeout chain is `180s execution -> 300s Aevatar per-call -> >=315s NyxID/ingress -> 330s HttpClient -> >=360s Workflow canary`.
- chrono-sandbox source is unchanged; verify and deploy a build containing `feat/managed-codex-execution@1e8134d`.
- Preserve unrelated dirty-worktree changes and stage only files owned by each task.

---

## File Structure

- Create `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialReadiness.cs`: pure readiness assessment, stable reason codes, and sanitized messages.
- Modify `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialLifecycle.cs`: delegate structural `IsReady` checks to the pure assessor while preserving explicit lifecycle behavior.
- Create `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialReadinessTests.cs`: exhaustive descriptor-category tests for the pure contract.
- Modify `src/Aevatar.Mainnet.Host.Api/ManagedCodex/ManagedCodexCredentialEndpoints.cs`: add `execution_ready` and `execution_readiness_reason` from the shared contract.
- Modify `test/Aevatar.Capabilities.Tests/MainnetManagedCodexCredentialEndpointsTests.cs`: status response and secret-redaction coverage.
- Modify `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexExecutionCoordinator.cs`: query committed readiness directly and remove same-turn automatic repair.
- Modify `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexExecutionCoordinatorTests.cs`: prove fast failure and one-query/one-transport behavior.
- Modify `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexOptions.cs`: rename and strengthen the complete lifecycle allowance.
- Modify `src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdManagedCodexChronoTransport.cs`: apply the linked 300-second maximum deadline.
- Modify `src/Aevatar.AI.ToolProviders.NyxId/NyxIdToolOptions.cs`, `NyxIdApiClient.cs`, and `ServiceCollectionExtensions.cs`: retain the 330-second transport ceiling for owned and typed clients.
- Modify focused timeout tests in `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests` and `test/Aevatar.AI.Tests`.
- Modify `docs/canon/managed-codex-execution.md` and `docs/operations/2026-07-16-managed-codex-exec-rollout.md`: replace transparent first-use repair with explicit readiness and record the deployment checklist.

---

### Task 1: Define One Pure Execution-Readiness Contract

**Files:**
- Create: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialReadiness.cs`
- Modify: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialLifecycle.cs`
- Create: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialReadinessTests.cs`

**Interfaces:**
- Consumes: `ManagedCodexOptions`, `ExternalSubjectRef`, `ManagedCodexCredentialSnapshot`, `ManagedCodexCredentialActorIdentity`, and `DateTimeOffset`.
- Produces: `ManagedCodexCredentialReadinessAssessment(bool ExecutionReady, string Reason, string Message)` and `ManagedCodexCredentialReadiness.Assess(...)`.

- [ ] **Step 1: Write failing pure-contract tests**

Create tests that use different concrete fixtures and assert exact reason codes:

```csharp
[Theory]
[InlineData(DescriptorFault.None, true, "ready")]
[InlineData(DescriptorFault.MissingCredential, false, "managed_credential_not_provisioned")]
[InlineData(DescriptorFault.Inactive, false, "managed_credential_inactive")]
[InlineData(DescriptorFault.Expired, false, "managed_credential_expired")]
[InlineData(DescriptorFault.WrongOwner, false, "managed_credential_owner_invalid")]
[InlineData(DescriptorFault.InvalidReference, false, "managed_credential_reference_invalid")]
[InlineData(DescriptorFault.InvalidServiceBinding, false, "managed_credential_service_binding_invalid")]
public void Assess_ReturnsOneStableStructuralReason(
    DescriptorFault fault,
    bool expectedReady,
    string expectedReason)
{
    var result = ManagedCodexCredentialReadiness.Assess(
        EnabledOptions(),
        Owner("user-a"),
        Snapshot(fault),
        Now);

    result.ExecutionReady.Should().Be(expectedReady);
    result.Reason.Should().Be(expectedReason);
}

[Fact]
public void Assess_WhenDisabled_WinsOverCredentialShape()
{
    var options = EnabledOptions();
    options.Enabled = false;

    ManagedCodexCredentialReadiness.Assess(options, Owner("user-a"), Snapshot(), Now)
        .Reason.Should().Be("managed_target_disabled");
}

[Fact]
public void Assess_WhenIneligible_WinsOverCredentialShape()
{
    ManagedCodexCredentialReadiness.Assess(EnabledOptions(), Owner("user-b"), Snapshot(), Now)
        .Reason.Should().Be("managed_feature_not_enabled");
}
```

The fixture must separately cover blank API-key ID, blank/equal UserService IDs, wrong slug, missing expiry, wrong purpose, wrong owner scope, non-positive Vault version, blank fingerprint, and mismatched Vault expiry. Each case maps to the category listed in the approved spec.

- [ ] **Step 2: Run the pure tests and confirm the red state**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --nologo --filter FullyQualifiedName~ManagedCodexCredentialReadinessTests
```

Expected: compilation fails because `ManagedCodexCredentialReadiness` and `ManagedCodexCredentialReadinessAssessment` do not exist.

- [ ] **Step 3: Implement the pure assessor**

Add the public Application contract:

```csharp
public sealed record ManagedCodexCredentialReadinessAssessment(
    bool ExecutionReady,
    string Reason,
    string Message);

public static class ManagedCodexCredentialReadiness
{
    public static ManagedCodexCredentialReadinessAssessment Assess(
        ManagedCodexOptions options,
        ExternalSubjectRef owner,
        ManagedCodexCredentialSnapshot? snapshot,
        DateTimeOffset now);

    internal static ManagedCodexCredentialReadinessAssessment AssessCredential(
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor? credential,
        DateTimeOffset now);
}
```

Evaluation order is fixed: disabled, ineligible, missing, inactive, expired/missing expiry, owner, Vault reference, API-key/service binding, ready. Compare owner identities only through `ManagedCodexCredentialActorIdentity.From`, and convert malformed protobuf timestamps or identities to their typed reason rather than throwing. Messages must describe the explicit credential POST/rotate action without including descriptor values.

Replace lifecycle's private structural body with:

```csharp
private static bool IsReady(
    ManagedCodexCredentialDescriptor? credential,
    ExternalSubjectRef owner,
    DateTimeOffset now) =>
    ManagedCodexCredentialReadiness
        .AssessCredential(owner, credential, now)
        .ExecutionReady;
```

- [ ] **Step 4: Run readiness and lifecycle tests**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --nologo --filter "FullyQualifiedName~ManagedCodexCredentialReadinessTests|FullyQualifiedName~ManagedCodexCredentialLifecycleTests"
```

Expected: PASS, with explicit lifecycle behavior unchanged.

- [ ] **Step 5: Commit the readiness contract**

```bash
git add -- \
  src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialReadiness.cs \
  src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialLifecycle.cs \
  test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialReadinessTests.cs
git commit -m "Define managed Codex execution readiness"
```

### Task 2: Make Credential Status Honest

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/ManagedCodex/ManagedCodexCredentialEndpoints.cs`
- Modify: `test/Aevatar.Capabilities.Tests/MainnetManagedCodexCredentialEndpointsTests.cs`

**Interfaces:**
- Consumes: `ManagedCodexCredentialReadiness.Assess(ManagedCodexOptions, ExternalSubjectRef, ManagedCodexCredentialSnapshot?, DateTimeOffset)`.
- Produces: status JSON fields `execution_ready` and `execution_readiness_reason`, while preserving `status`, `state_version`, and `cleanup_pending`.

- [ ] **Step 1: Write failing endpoint tests**

Extend the missing, valid, disabled, expired, owner-invalid, reference-invalid, and service-binding-invalid cases. The core assertions are:

```csharp
payload.RootElement.GetProperty("status").GetString().Should().Be("active");
payload.RootElement.GetProperty("execution_ready").GetBoolean().Should().BeFalse();
payload.RootElement.GetProperty("execution_readiness_reason").GetString()
    .Should().Be("managed_credential_reference_invalid");
json.Should().NotContain("sec-1")
    .And.NotContain("fingerprint")
    .And.NotContain("key-1");
```

For a valid descriptor assert `status=active`, `execution_ready=true`, and reason `ready`. For disabled and ineligible users assert the corresponding policy reason even when the stored descriptor is valid.

- [ ] **Step 2: Run endpoint tests and confirm the red state**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter FullyQualifiedName~MainnetManagedCodexCredentialEndpointsTests
```

Expected: FAIL because the response does not contain the two readiness fields.

- [ ] **Step 3: Use the shared assessment in `StatusAsync`**

Resolve the owner once, evaluate readiness once, and include the result in both response shapes:

```csharp
var owner = Owner(userId);
var snapshot = await queryPort.ResolveAsync(owner, ct).ConfigureAwait(false);
var readiness = ManagedCodexCredentialReadiness.Assess(
    options.Value,
    owner,
    snapshot,
    timeProvider.GetUtcNow());
```

The missing shape retains `status=not_provisioned`, `state_version=0`, and `cleanup_pending=0`. The stored shape retains the current lifecycle status and authoritative snapshot values. Neither shape serializes credential identity or Vault fields.

- [ ] **Step 4: Run endpoint and Host composition tests**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter "FullyQualifiedName~MainnetManagedCodexCredentialEndpointsTests|FullyQualifiedName~MainnetHostCompositionTests"
```

Expected: PASS.

- [ ] **Step 5: Commit the honest status contract**

```bash
git add -- \
  src/Aevatar.Mainnet.Host.Api/ManagedCodex/ManagedCodexCredentialEndpoints.cs \
  test/Aevatar.Capabilities.Tests/MainnetManagedCodexCredentialEndpointsTests.cs
git commit -m "Report managed Codex execution readiness"
```

### Task 3: Remove Hidden Credential Mutation from Workflow Execution

**Files:**
- Modify: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexExecutionCoordinator.cs`
- Modify: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexExecutionCoordinatorTests.cs`

**Interfaces:**
- Consumes: `IOptions<ManagedCodexOptions>`, `IManagedCodexCredentialQueryPort.ResolveAsync`, `ManagedCodexCredentialReadiness.Assess`, `TimeProvider`, and `IManagedCodexChronoTransport`.
- Produces: one read-only readiness check followed by zero or one chrono call; no lifecycle mutation call or same-turn repair retry.

- [ ] **Step 1: Rewrite coordinator tests for the approved boundary**

Construct the coordinator with options, query port, transport, clock, and logger. Delete same-call provisioning and retry expectations. Add these tests:

```csharp
[Fact]
public async Task ExecuteAsync_WhenCredentialIsNotExecutionReady_FailsWithoutMutationOrChrono()
{
    _query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
        .Returns(SnapshotWithInvalidReference());

    var terminal = (await CollectAsync(_coordinator.ExecuteAsync(Request())))[^1];

    terminal.Failure!.Code.Should().Be("managed_credential_reference_invalid");
    await _query.Received(1).ResolveAsync(Owner("user-a"), Arg.Any<CancellationToken>());
    await _transport.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default);
}

[Fact]
public async Task ExecuteAsync_WhenAuthorizationIsDenied_DoesNotRepairOrRetryInActorTurn()
{
    _transport.ExecuteAsync(Arg.Any<CodexExecutionRequest>(), Arg.Any<ManagedCodexCredentialDescriptor>(), Arg.Any<CancellationToken>())
        .Returns(_ => Task.FromException<CodexExecutionResult>(
            TransportFailure("managed_proxy_authorization_denied")));

    var terminal = (await CollectAsync(_coordinator.ExecuteAsync(Request())))[^1];

    terminal.Failure!.Code.Should().Be("managed_proxy_authorization_denied");
    await _transport.Received(1).ExecuteAsync(Arg.Any<CodexExecutionRequest>(), Arg.Any<ManagedCodexCredentialDescriptor>(), Arg.Any<CancellationToken>());
}
```

Retain request validation, cancellation, non-repairable failure, and successful execution coverage. Assert a ready execution queries once and invokes chrono once with the exact committed descriptor.

- [ ] **Step 2: Run coordinator tests and confirm the red state**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --nologo --filter FullyQualifiedName~ManagedCodexExecutionCoordinatorTests
```

Expected: compilation or behavioral failures because the coordinator still depends on `IManagedCodexCredentialLifecycle` and retries repair.

- [ ] **Step 3: Implement the read-only coordinator**

Change the constructor to:

```csharp
public sealed class ManagedCodexExecutionCoordinator(
    IOptions<ManagedCodexOptions> options,
    IManagedCodexCredentialQueryPort queryPort,
    IManagedCodexChronoTransport transport,
    TimeProvider timeProvider,
    ILogger<ManagedCodexExecutionCoordinator> logger) : ICodexExecutionPort
```

After request validation:

```csharp
var snapshot = await _queryPort.ResolveAsync(owner, ct).ConfigureAwait(false);
var readiness = ManagedCodexCredentialReadiness.Assess(
    _options, owner, snapshot, _timeProvider.GetUtcNow());
if (!readiness.ExecutionReady)
    return CodexExecutionEvent.Failed(MapReadinessFailure(readiness));

var result = await _transport.ExecuteAsync(
    request, snapshot!.Credential.Clone(), ct).ConfigureAwait(false);
return CodexExecutionEvent.Completed(result);
```

Remove `CanRepair`, ForceRemoteValidation retry, and coordinator lifecycle exception mapping. Map disabled to `TargetNotConfigured`, ineligible to `AdmissionDenied`, and other readiness reasons to `ProvisioningFailed`. Do not pass the caller bearer to a readiness mutation path.

- [ ] **Step 4: Run coordinator, DI, and focused tool tests**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --nologo --filter FullyQualifiedName~ManagedCodexExecutionCoordinatorTests
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter FullyQualifiedName~NyxIdCodexExecToolTests
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter FullyQualifiedName~MainnetHostCompositionTests
```

Expected: all PASS.

- [ ] **Step 5: Commit the Workflow fast-failure boundary**

```bash
git add -- \
  src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexExecutionCoordinator.cs \
  test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexExecutionCoordinatorTests.cs
git commit -m "Keep managed Codex workflow execution read only"
```

### Task 4: Close the Complete HTTP Timeout Chain

**Files:**
- Modify: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexOptions.cs`
- Modify: `src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdManagedCodexChronoTransport.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdToolOptions.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ServiceCollectionExtensions.cs`
- Modify: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexOptionsValidatorTests.cs`
- Modify: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/NyxIdManagedCodexChronoTransportTests.cs`
- Modify: `test/Aevatar.AI.Tests/ToolProviderHttpClientRegistrationTests.cs`
- Modify: `test/Aevatar.AI.Tests/ToolProviderHttpClientOwnershipTests.cs`

**Interfaces:**
- Consumes: request `TimeoutSeconds`, caller cancellation, `FakeTimeProvider`, and `IHttpClientFactory`.
- Produces: `ExecutionLifecycleGraceSeconds=120`, maximum managed deadline 300 seconds, and `MaxRequestDurationSeconds=330`.

- [ ] **Step 1: Update timeout tests to the complete lifecycle budget**

Replace the old 30-second execution grace assertions with:

```csharp
[Fact]
public async Task ExecuteAsync_KeepsWaitingBeforeCompleteLifecycleDeadline()
{
    var time = new FakeTimeProvider(Now);
    var options = ValidOptions();
    options.ExecutionLifecycleGraceSeconds = 120;
    var handler = new UnansweredHandler(() => time.Advance(TimeSpan.FromSeconds(299)));
    var (transport, _) = CreateTransport(handler, options, time);

    var pending = transport.ExecuteAsync(Request(timeoutSeconds: 180), Descriptor());

    pending.IsCompleted.Should().BeFalse();
    handler.ObservedToken.IsCancellationRequested.Should().BeFalse();
}

[Fact]
public async Task ExecuteAsync_StopsAtCompleteLifecycleDeadline()
{
    var time = new FakeTimeProvider(Now);
    var options = ValidOptions();
    options.ExecutionLifecycleGraceSeconds = 120;
    var handler = new UnansweredHandler(() => time.Advance(TimeSpan.FromSeconds(301)));
    var (transport, _) = CreateTransport(handler, options, time);

    await transport.Invoking(value => value.ExecuteAsync(Request(
        timeoutSeconds: 180), Descriptor())).Should().ThrowAsync<OperationCanceledException>();
}
```

Validate that `ExecutionLifecycleGraceSeconds` below 120 or above 180 is rejected. Retain earlier caller-cancellation coverage. Assert the typed client defaults to 330 seconds, honors an explicit override, and a self-owned `NyxIdApiClient` uses the configured ceiling while a caller-owned started client is not mutated.

- [ ] **Step 2: Run timeout tests and confirm the red state**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --nologo --filter "FullyQualifiedName~ManagedCodexOptionsValidatorTests|FullyQualifiedName~NyxIdManagedCodexChronoTransportTests"
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter "FullyQualifiedName~ToolProviderHttpClientRegistrationTests|FullyQualifiedName~ToolProviderHttpClientOwnershipTests"
```

Expected: FAIL until the option is renamed/defaulted to 120 and every client ceiling is explicit.

- [ ] **Step 3: Implement the budget chain**

Use these exact contracts:

```csharp
public int ExecutionLifecycleGraceSeconds { get; set; } = 120;

if (options.ExecutionLifecycleGraceSeconds is < 120 or > 180)
    failures.Add("ExecutionLifecycleGraceSeconds must be between 120 and 180.");
```

The transport linked deadline is:

```csharp
using var lifecycleTimeout = new CancellationTokenSource(
    TimeSpan.FromSeconds(
        request.TimeoutSeconds + _options.ExecutionLifecycleGraceSeconds),
    _timeProvider);
using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(
    ct, lifecycleTimeout.Token);
```

Keep `NyxIdToolOptions.DefaultMaxRequestDurationSeconds = 330`, its configurable `MaxRequestDurationSeconds`, typed-client registration, and self-owned-client initialization. Do not modify a caller-supplied `HttpClient`.

- [ ] **Step 4: Run timeout and broader NyxID client tests**

Run:

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --nologo --filter "FullyQualifiedName~ManagedCodexOptionsValidatorTests|FullyQualifiedName~NyxIdManagedCodexChronoTransportTests"
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter "FullyQualifiedName~ToolProviderHttpClientRegistrationTests|FullyQualifiedName~ToolProviderHttpClientOwnershipTests|FullyQualifiedName~NyxIdApiClient"
```

Expected: PASS.

- [ ] **Step 5: Commit the timeout chain**

```bash
git add -- \
  src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexOptions.cs \
  src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdManagedCodexChronoTransport.cs \
  src/Aevatar.AI.ToolProviders.NyxId/NyxIdToolOptions.cs \
  src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs \
  src/Aevatar.AI.ToolProviders.NyxId/ServiceCollectionExtensions.cs \
  test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexOptionsValidatorTests.cs \
  test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/NyxIdManagedCodexChronoTransportTests.cs \
  test/Aevatar.AI.Tests/ToolProviderHttpClientRegistrationTests.cs \
  test/Aevatar.AI.Tests/ToolProviderHttpClientOwnershipTests.cs
git commit -m "Close managed Codex request timeout budgets"
```

### Task 5: Update Canonical and Rollout Documentation

**Files:**
- Modify: `docs/canon/managed-codex-execution.md`
- Modify: `docs/operations/2026-07-16-managed-codex-exec-rollout.md`

**Interfaces:**
- Consumes: readiness reason codes and timeout values from Tasks 1-4.
- Produces: one operator checklist covering Aevatar, chrono-sandbox, NyxID/ingress, credential reconciliation, canaries, and rollback.

- [ ] **Step 1: Update canonical semantics**

Replace claims that the first Workflow call transparently provisions or repairs the credential. State explicitly:

```text
Normal codex_exec reads one committed credential current-state read model.
If execution_ready is false, it fails with execution_readiness_reason and does
not acquire the mutation lease or call credential lifecycle dependencies.
POST /api/managed-codex/credential is the explicit idempotent
provision/reconciliation action; /rotate forces replacement.
```

Document every stable reason code and the `status` versus `execution_ready` distinction.

- [ ] **Step 2: Update the rollout runbook**

Add the exact configuration and ordering:

```text
Aevatar__CodexExecution__ManagedSandbox__ExecutionLifecycleGraceSeconds=120
NyxId__MaxRequestDurationSeconds=330
chrono CODEX_TIMEOUT_MAX_SECS=180
chrono CODEX_CLEANUP_TIMEOUT_SECS=30
chrono SANDBOX_TIMEOUT_SECS=30
NyxID/ingress non-streaming proxy timeout >=315 seconds
Workflow canary timeout >=360 seconds
```

Record deployment of chrono commit `1e8134d` or a descendant, the existing POST/rotate repair sequence, bounded status readback, three canaries, cleanup evidence, and rollback.

- [ ] **Step 3: Run documentation checks**

Run:

```bash
bash tools/docs/lint.sh
git diff --check -- docs/canon/managed-codex-execution.md docs/operations/2026-07-16-managed-codex-exec-rollout.md
```

Expected: docs lint passes with zero errors and diff check is silent.

- [ ] **Step 4: Commit documentation**

```bash
git add -- docs/canon/managed-codex-execution.md docs/operations/2026-07-16-managed-codex-exec-rollout.md
git commit -m "Document managed Codex recovery operations"
```

### Task 6: Verify Aevatar and chrono-sandbox

**Files:**
- No source changes expected.

**Interfaces:**
- Consumes: all commits from Tasks 1-5 and chrono-sandbox `feat/managed-codex-execution@1e8134d`.
- Produces: fresh build/test/guard evidence suitable for the push handoff.

- [ ] **Step 1: Run Aevatar focused suites**

```bash
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter "FullyQualifiedName~ManagedCodex|FullyQualifiedName~MainnetHostCompositionTests"
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --filter "FullyQualifiedName~Codex|FullyQualifiedName~NyxIdApiClient|FullyQualifiedName~ToolProviderHttpClient"
```

Expected: all selected tests PASS.

- [ ] **Step 2: Run mandatory guards**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: every guard exits 0.

- [ ] **Step 3: Build the authoritative solution**

```bash
dotnet build aevatar.slnx --nologo
```

Expected: Build succeeded with zero errors. Record any pre-existing warnings separately.

- [ ] **Step 4: Verify the exact chrono deployment branch without editing it**

From `/Users/eanzhao/Code/.worktrees/chrono-sandbox-managed-codex` run:

```bash
git status --short --branch
git merge-base --is-ancestor 1e8134d HEAD
cargo fmt --check
cargo test
cargo clippy --all-targets --all-features
```

Expected: clean branch, ancestor check exits 0, and all cargo gates pass.

- [ ] **Step 5: Inspect the final scoped diff**

```bash
git status --short --branch
git log --oneline --decorate origin/feature/integrate..HEAD
git diff --check origin/feature/integrate...HEAD
git diff --stat origin/feature/integrate...HEAD
```

Expected: only approved managed Codex commits plus pre-existing local branch commits; unrelated dirty files remain unstaged.

### Task 7: Synchronize and Push `feature/integrate`

**Files:**
- No intended source changes; conflicts, if any, are resolved only in files already owned by this plan.

**Interfaces:**
- Consumes: a verified local `feature/integrate` and current `origin/feature/integrate`.
- Produces: a non-force push whose remote contains all local approved commits.

- [ ] **Step 1: Fetch and inspect divergence**

```bash
git fetch origin feature/integrate
git rev-list --left-right --count HEAD...origin/feature/integrate
git log --left-right --cherry-pick --oneline HEAD...origin/feature/integrate
```

Expected: divergence is understood before mutation.

- [ ] **Step 2: Merge the current remote branch**

Preserve unrelated dirty changes. If Git refuses because a dirty file would be overwritten, stop and identify the exact overlap instead of stashing or discarding user work. Otherwise run:

```bash
git merge --no-edit origin/feature/integrate
```

Expected: clean merge or conflicts only in plan-owned managed Codex files. Resolve conflicts by preserving both current remote behavior and the approved readiness/timeout contract; never use `checkout --ours`, `checkout --theirs`, reset, or force.

- [ ] **Step 3: Re-run verification after integration**

```bash
dotnet build aevatar.slnx --nologo
dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter "FullyQualifiedName~ManagedCodex|FullyQualifiedName~MainnetHostCompositionTests"
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: all commands pass on the exact merge result.

- [ ] **Step 4: Push without force and verify the remote SHA**

```bash
git push origin feature/integrate:feature/integrate
git fetch origin feature/integrate
test "$(git rev-parse HEAD)" = "$(git rev-parse origin/feature/integrate)"
```

Expected: push succeeds and the equality check exits 0.
