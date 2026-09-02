# Managed Codex Lifecycle Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the layering, concurrency, cancellation, dispatch-ambiguity, expiry, and mutable NyxID-policy gaps found while reviewing the internal managed `codex_exec` implementation.

**Architecture:** Move lifecycle policy and orchestration into a dedicated Application project behind typed NyxID and mutation-lease ports. Serialize each user's mutations with a production Garnet lease, use one deterministic Vault reference per issued key, and reconcile remote active-key/Vault facts after uncertain actor dispatch without treating an accepted-only transport failure as non-delivery.

**Tech Stack:** .NET 10, protobuf, Aevatar actor command/current-state ports, `ISecretVault`, StackExchange.Redis-compatible Garnet, xUnit, FluentAssertions, NSubstitute.

## Global Constraints

- Raw bearer and agent-key values remain method-local and never enter protobuf, Actor state, projection, logs, exceptions, diagnostics, or HTTP results.
- Production lifecycle serialization is cluster-shared; an in-memory implementation is allowed only in Development and Testing.
- Request cancellation is honored before mutation, while post-mutation completion uses a separate bounded token.
- Actor dispatch is accepted-only; failure or cancellation never proves non-delivery.
- Rotation creates a new Vault reference and never overwrites the committed descriptor's referenced secret.
- Mutable NyxID UserService forwarding policy remains an explicit internal-only trust assumption tracked by #2899.

---

### Task 1: Application Boundary And Typed Ports

**Files:**
- Create: `src/Aevatar.AI.Application.CodexExecution/Aevatar.AI.Application.CodexExecution.csproj`
- Create: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialContracts.cs`
- Create: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/IManagedCodexNyxIdCredentialPort.cs`
- Create: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/IManagedCodexCredentialMutationLease.cs`
- Move: lifecycle policy/orchestration out of `src/Aevatar.AI.Infrastructure.ChronoSandbox/ManagedCodexCredentialLifecycle.cs`
- Modify: `aevatar.slnx`
- Test: `test/Aevatar.Architecture.Tests/Rules/ManagedCodexDependencyBoundaryTests.cs`

**Interfaces:**
- Produces: `IManagedCodexCredentialLifecycle` for Host.
- Produces: `IManagedCodexNyxIdCredentialPort` returning typed owner, service, and API-key facts plus an opaque one-time secret value.
- Produces: `IManagedCodexCredentialMutationLease.TryAcquireAsync(string ownerKey, CancellationToken)` returning an `IAsyncDisposable` handle or `null` on conflict.
- Consumes: `ISecretVault`, `IManagedCodexCredentialQueryPort`, and `IManagedCodexCredentialCommandPort`.

- [ ] **Step 1: Add a failing architecture test**

Assert the Application project has no reference to `Aevatar.AI.ToolProviders.NyxId`, StackExchange.Redis, ASP.NET Host, or ChronoSandbox Infrastructure, and that `ManagedCodexCredentialLifecycle` is absent from Infrastructure source.

- [ ] **Step 2: Run the architecture slice and verify RED**

Run: `dotnet test test/Aevatar.Architecture.Tests/Aevatar.Architecture.Tests.csproj --filter FullyQualifiedName~ManagedCodexDependencyBoundaryTests --nologo`

Expected: FAIL because lifecycle orchestration currently lives in Infrastructure.

- [ ] **Step 3: Create the Application project and typed ports**

Keep external JSON and `NyxIdApiClient` out of Application. Represent remote API-key facts as typed fields: ID, active flag, exact scopes/service/node grants, platform, and expiry. Expose the raw issued key only through an opaque `UseAsync` callback type whose `ToString()` is redacted.

- [ ] **Step 4: Move orchestration and make Infrastructure implement only adapters**

Update DI so Host composes Application lifecycle plus Infrastructure adapters. Remove direct Host dependency on an Infrastructure-owned lifecycle contract.

- [ ] **Step 5: Run the architecture slice and verify GREEN**

Run the command from Step 2 and require zero failures.

### Task 2: Per-User Mutation Lease

**Files:**
- Create: `src/Aevatar.AI.Infrastructure.ChronoSandbox/Credentials/GarnetManagedCodexCredentialMutationLease.cs`
- Create: `src/Aevatar.AI.Infrastructure.ChronoSandbox/Credentials/InMemoryManagedCodexCredentialMutationLease.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`
- Test: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialMutationLeaseTests.cs`
- Test: `test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs`

**Interfaces:**
- Consumes: `IGarnetSecretKeyValueStore.SetIfAbsentAsync` and `CompareDeleteAsync`.
- Produces: one owner-token lease key derived from a SHA-256 digest of the managed credential actor ID.

- [ ] **Step 1: Write failing lease tests**

Prove the first owner acquires, a concurrent owner receives `null`, a wrong owner cannot release, disposal releases with compare-delete, and expiry is longer than the bounded mutation window. Prove non-Development composition never selects InMemory.

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter FullyQualifiedName~ManagedCodexCredentialMutationLease --nologo`

- [ ] **Step 3: Implement Garnet and explicitly scoped InMemory adapters**

Use a random 256-bit owner token as the stored lease value. Acquire with `SET NX` plus TTL and release only with compare-delete. Do not expose a generic business-level distributed-lock abstraction.

- [ ] **Step 4: Wrap all lifecycle mutations and map conflict**

Acquire after authenticated owner resolution and before reading lifecycle state. Return `managed_credential_mutation_in_progress`; map it to HTTP `409`.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the tests from Step 2 plus `MainnetHostCompositionTests`.

### Task 3: Non-Destructive Rotation And Ambiguous Dispatch Recovery

**Files:**
- Modify: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialLifecycle.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/ManagedCodex/ManagedCodexCredentialGAgent.cs`
- Test: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialLifecycleTests.cs`
- Test: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ManagedCodexCredentialGAgentTests.cs`

**Interfaces:**
- Consumes: typed active managed-key list and deterministic `SecretRefFor(owner, apiKeyId)`.
- Produces: idempotent provision/rotate commands whose duplicate delivery is a no-op.

- [ ] **Step 1: Write failing race and recovery tests**

Cover concurrent rotate/rotate and rotate/revoke conflict, a dispatch exception after possible admission, a later call seeing an active remote key different from the projection, duplicate rotated commands, and a Vault-write failure after remote rotation.

- [ ] **Step 2: Run the lifecycle and Actor slices and verify RED**

Run: `dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter FullyQualifiedName~ManagedCodexCredentialLifecycle --nologo`

Run: `dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --filter FullyQualifiedName~ManagedCodexCredentialGAgent --nologo`

- [ ] **Step 3: Store each issued key in its own deterministic Vault reference**

Derive the reference from the full owner identity and API-key ID. On rotation, `PutAsync` the new secret; never call `RotateAsync` on the current reference. Validate a follow-up NyxID key read, including exact finite expiry, before persisting the secret.

- [ ] **Step 4: Reconcile before issuing another key**

When NyxID has one exact active managed key different from the projected key, resolve its deterministic Vault reference and redispatch the descriptor. If the reference is absent, revoke that remote key and issue a fresh one. Never rotate an inactive projected key.

- [ ] **Step 5: Make Actor duplicate rotation idempotent**

If incoming key ID, Vault reference, fingerprint, expiry, and service identity already equal current state, return without queuing cleanup. A genuinely stale different credential still queues both independent cleanup tracks.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run both commands from Step 2 and require zero failures.

### Task 4: Cancellation-Stable Revocation And Effective Status

**Files:**
- Modify: `src/Aevatar.AI.Application.CodexExecution/ManagedCodex/ManagedCodexCredentialLifecycle.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/ManagedCodex/ManagedCodexCredentialEndpoints.cs`
- Test: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialLifecycleTests.cs`
- Test: `test/Aevatar.Capabilities.Tests/MainnetManagedCodexCredentialEndpointsTests.cs`

**Interfaces:**
- Produces: an internally bounded post-mutation completion token.
- Produces: status values `not_provisioned`, `active`, `expired`, and `revoked`.

- [ ] **Step 1: Write failing cancellation and expiry tests**

Cancel the HTTP token after Vault revoke and after NyxID revoke; in both cases assert `CommitRevokedAsync` still receives exact pending-track facts. Assert an expired active descriptor is returned as `expired`.

- [ ] **Step 2: Run focused tests and verify RED**

Run: `dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --filter FullyQualifiedName~MainnetManagedCodexCredentialEndpoints --nologo`

- [ ] **Step 3: Separate caller cancellation from critical completion**

After the first external mutation begins, use the bounded internal token for remaining external tracks and actor dispatch. Catch individual track failures into typed cleanup facts; do not rethrow caller cancellation before persistence admission is attempted.

- [ ] **Step 4: Derive effective status without query-time writes**

Compare `ExpiresAt` with `TimeProvider.GetUtcNow()` in Host mapping and report `expired` while preserving the committed enum and state version.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the command from Step 2 plus the full ChronoSandbox test project.

### Task 5: Documentation, Issue Contract, And Verification

**Files:**
- Modify: `docs/canon/managed-codex-execution.md`
- Modify: `docs/operations/2026-07-16-managed-codex-exec-rollout.md`
- Modify: GitHub issue `#2899`

- [ ] **Step 1: Align guarantees with the P0 trust boundary**

State that Aevatar never intentionally forwards the key, but mutable NyxID UserService policy prevents an end-to-end guarantee. Add immutable/request-level caller-credential non-forwarding to #2899's mandatory public-rollout scope.

- [ ] **Step 2: Run focused tests and repository guards**

Run ChronoSandbox, Channel Identity, Capabilities, AI tool, and architecture slices; then `test_stability_guards.sh`, projection guards, architecture guards, docs lint, solution ownership, slow-test, and solution split guards.

- [ ] **Step 3: Build the full solution**

Run: `dotnet build aevatar.slnx --nologo`

Expected: zero errors; record existing warnings separately.

- [ ] **Step 4: Review secrets and ownership boundaries**

Search for raw key values in protobuf, logs, results, and docs; confirm no normal-path direct OpenSandbox dependency; inspect the final diff for unrelated changes.

- [ ] **Step 5: Commit, push, create PR, and update issues**

Keep #2896 and #2897 open until merge, comment with the PR and verification evidence, leave #2898 for operations/canary, and keep #2899 deferred but mandatory before broad rollout.
