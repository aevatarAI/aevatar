# Managed Codex Chrono Agent Key Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Aevatar's direct OpenSandbox managed Codex path with a per-user Vault-backed NyxID agent key and a fixed chrono-sandbox proxy adapter.

**Architecture:** A per-NyxID-user `ManagedCodexCredentialGAgent` owns non-secret lifecycle facts and publishes a current-state read model. A lifecycle service issues exact-scope NyxID keys and stores raw values only in `ISecretVault`; execution resolves that reference just in time and calls chrono-sandbox through `NyxIdApiClient` behind `ICodexExecutionPort`.

**Tech Stack:** .NET 10, protobuf, Aevatar GAgents/event sourcing, CQRS projection runtime, `ISecretVault`, `NyxIdApiClient`, xUnit, FluentAssertions, NSubstitute.

## Global Constraints

- Keep `Domain / Application / Infrastructure / Host` responsibilities explicit; Host only maps HTTP and composes services.
- Durable user credential facts are actor-owned and protobuf-serialized; no process-local identity registry.
- Queries read current-state projections and never replay events or prime projections in the query call stack.
- The raw NyxID key exists only in lifecycle/proxy method-local memory and `ISecretVault`.
- The key scope is exactly `proxy`; wildcard service/node grants are forbidden.
- Only the exact user-owned `chrono-sandbox` UserService is granted; `chrono-llm-public` is readiness evidence, not a key grant.
- Aevatar sends the persistent key only as the NyxID proxy Authorization value and never serializes it into chrono-sandbox, codex-runner, workflow state, envelopes, logs, or results. End-to-end non-forwarding relies on the internal P0 NyxID policy trust boundary described in the lifecycle-hardening plan and #2899.
- Every production behavior starts with a failing focused test and a verified RED result.
- Update canonical and operational documentation with the same runtime contract.

---

### Task 1: Actor-Owned Credential Contract And Projection

**Files:**
- Modify: `agents/Aevatar.GAgents.Channel.Identity.Abstractions/protos/identity_contracts.proto`
- Create: `agents/Aevatar.GAgents.Channel.Identity.Abstractions/ManagedCodex/IManagedCodexCredentialPorts.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/protos/managed_codex_credential.proto`
- Create: `agents/Aevatar.GAgents.Channel.Identity/ManagedCodex/ManagedCodexCredentialGAgent.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/ManagedCodex/ManagedCodexCredentialActorIdentity.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/ManagedCodex/ManagedCodexCredentialCommandPort.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialProjector.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialProjectionQueryPort.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialMaterializationContext.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialMaterializationRuntimeLease.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialDocument.Partial.cs`
- Create: `agents/Aevatar.GAgents.Channel.Identity/Projection/ManagedCodexCredentialDocumentMetadataProvider.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/Projection/ChannelIdentityCommittedStateProjectionActivationPlanProvider.cs`
- Modify: `agents/Aevatar.GAgents.Channel.Identity/DependencyInjection/IdentityServiceCollectionExtensions.cs`
- Test: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ManagedCodexCredentialGAgentTests.cs`
- Test: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ManagedCodexCredentialProjectorTests.cs`
- Test: `test/Aevatar.GAgents.ChannelRuntime.Tests/Identity/ChannelIdentityCommittedStateProjectionActivationPlanProviderTests.cs`

**Interfaces:**
- Produces: `IManagedCodexCredentialQueryPort.ResolveAsync(ExternalSubjectRef, CancellationToken)`.
- Produces: `IManagedCodexCredentialCommandPort` accepted-only provision, rotate, revoke, queue, and cleanup-track commands.
- Produces: `ManagedCodexCredentialDescriptor` containing only typed non-secret facts.

- [ ] **Step 1: Write actor identity and state-transition tests**

Assert two NyxID subjects map to different actor IDs; provision adopts the first exact descriptor; rotation requires the expected prior key ID; revoke retains lifecycle facts but changes status; queued cleanup contains IDs/references and never raw key text.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --filter 'FullyQualifiedName~ManagedCodexCredential' --nologo`

Expected: FAIL because the managed credential contracts and actor do not exist.

- [ ] **Step 3: Implement protobuf contracts, actor, and command port**

Use a deterministic actor ID from the complete `ExternalSubjectRef`. Validate `platform == "nyxid"`, descriptor owner, exact SecretReference shape, active status, expiry, and compare-and-set rotation fields before persisting domain events.

- [ ] **Step 4: Write projector/query and activation tests, then verify RED**

Assert committed actor state overwrites one document at the authoritative state version, query returns a clone, and every managed credential event maps to the durable materialization lease.

- [ ] **Step 5: Implement projection and DI registration, then verify GREEN**

Run the focused command from Step 2 and require zero failures.

### Task 2: Exact NyxID Key And Vault Lifecycle

**Files:**
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`
- Modify: `src/Aevatar.Foundation.Abstractions/Credentials/CredentialSecretPurposes.cs`
- Create: `src/Aevatar.AI.Infrastructure.ChronoSandbox/ManagedCodexOptions.cs`
- Create: `src/Aevatar.AI.Infrastructure.ChronoSandbox/ManagedCodexCredentialLifecycle.cs`
- Create: `src/Aevatar.AI.Infrastructure.ChronoSandbox/ManagedCodexNyxIdCatalogResolver.cs`
- Create: `src/Aevatar.AI.Infrastructure.ChronoSandbox/ManagedCodexOpaqueSecret.cs`
- Test: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexCredentialLifecycleTests.cs`
- Test: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ManagedCodexOptionsValidatorTests.cs`

**Interfaces:**
- Produces: `IManagedCodexCredentialLifecycle.ProvisionAsync`, `RotateAsync`, and `RevokeAsync`.
- Consumes: actor command/query ports, `INyxIdApiClientFactory`, `ISecretVault`, and `TimeProvider`.

- [ ] **Step 1: Write exact-policy and ownership tests**

Test exact `/api/v1/user-services` resolution, usable `chrono-llm-public` readiness, `scopes="proxy"`, `allow_all_services=false`, one allowed sandbox service ID, no nodes, finite expiry, `/users/me` claim match, and P0 allowlist rejection.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter 'FullyQualifiedName~ManagedCodexCredentialLifecycle' --nologo`

Expected: FAIL because the lifecycle project does not exist.

- [ ] **Step 3: Implement provision and compensation**

Create the key only after exact service/readiness validation. Validate the NyxID response, wrap `full_key` in the opaque local type, store it with purpose `managed.codex-invocation-agent-key`, and dispatch only the resulting reference. On Vault failure, revoke immediately or queue the non-secret cleanup locator.

- [ ] **Step 4: Write rotation/revocation and Vault isolation tests, then verify RED**

Assert rotation uses NyxID rotation plus a distinct deterministic Vault reference for the new key, previous key material becomes unusable, wrong purpose/owner/subject fails resolution, revoke executes independent NyxID/Vault tracks, and serialized results never contain `full_key`.

- [ ] **Step 5: Implement rotate/revoke and verify GREEN**

Run all tests in the new ChronoSandbox test project and require zero failures.

### Task 3: Fixed Chrono-Sandbox Execution Adapter

**Files:**
- Create: `src/Aevatar.AI.Infrastructure.ChronoSandbox/Aevatar.AI.Infrastructure.ChronoSandbox.csproj`
- Create: `src/Aevatar.AI.Infrastructure.ChronoSandbox/ChronoSandboxCodexExecutionAdapter.cs`
- Create: `src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdChronoSandboxCodexClient.cs`
- Create: `src/Aevatar.AI.Infrastructure.ChronoSandbox/ServiceCollectionExtensions.cs`
- Create: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj`
- Create: `test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/ChronoSandboxCodexExecutionAdapterTests.cs`

**Interfaces:**
- Produces: one `ICodexExecutionPort` for `ManagedSandbox`.
- Consumes: actor projection query, `ISecretVault`, and `NyxIdApiClient.ProxyRequestAsync`.

- [ ] **Step 1: Write fixed-request and failure-mapping tests**

Assert started/terminal event ordering; body contains only prompt, timeout, and `empty_git`; Authorization uses the Vault key rather than the interactive bearer; `_nyxid_via` binds the fixed route to the granted personal UserService; fixed slug/path cannot be caller-controlled; and proxy, timeout, malformed, nonzero, and secret-bearing failures are sanitized.

- [ ] **Step 2: Run tests and verify RED**

Run: `dotnet test test/Aevatar.AI.Infrastructure.ChronoSandbox.Tests/Aevatar.AI.Infrastructure.ChronoSandbox.Tests.csproj --filter 'FullyQualifiedName~ChronoSandboxCodexExecutionAdapter' --nologo`

Expected: FAIL because the adapter is absent.

- [ ] **Step 3: Implement the adapter and just-in-time resolver**

Reject disabled, incomplete authority, absent/revoked/expired/mismatched references before the proxy call. Resolve the raw key immediately before `POST /codex/execute`, parse only the fixed terminal contract, and emit sanitized typed failures.

- [ ] **Step 4: Run all ChronoSandbox tests and verify GREEN**

Run the complete new test project and require zero failures.

### Task 4: Mainnet Lifecycle Endpoints And Composition

**Files:**
- Create: `src/Aevatar.Mainnet.Host.Api/ManagedCodex/ManagedCodexCredentialEndpoints.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetAgentProjectionDocumentStoresExtensions.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj`
- Modify: `src/Aevatar.Mainnet.Host.Api/appsettings.json`
- Test: `test/Aevatar.Capabilities.Tests/MainnetManagedCodexCredentialEndpointsTests.cs`

**Interfaces:**
- Produces: authenticated self-service `GET/POST/rotate/DELETE` lifecycle routes.
- Consumes: `IManagedCodexCredentialLifecycle`, authenticated NyxID claim, and bearer token.

- [ ] **Step 1: Write endpoint auth and response tests**

Assert missing auth is rejected, request bodies cannot nominate another user, claim and `/users/me` must match, mutations return honest accepted receipts without secret references or raw keys, kill switch blocks provision/rotate but not revoke/status, and status reads only the projection.

- [ ] **Step 2: Run endpoint tests and verify RED**

Run: `dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --filter 'FullyQualifiedName~MainnetManagedCodexCredentialEndpoints' --nologo`

- [ ] **Step 3: Implement endpoints, store registration, and composition**

Register the managed credential document for both Elasticsearch and in-memory providers, map endpoints with `RequireAuthorization`, and enable `codex_exec` discovery from the new options section.

- [ ] **Step 4: Run focused endpoint and DI tests and verify GREEN**

Run the focused endpoint test plus existing NyxID tool registration tests.

### Task 5: Delete Direct OpenSandbox Path And Migrate Documentation

**Files:**
- Delete: `src/Aevatar.AI.Infrastructure.OpenSandbox/`
- Delete: `test/Aevatar.AI.Infrastructure.OpenSandbox.Tests/`
- Delete: `tools/opensandbox-codex-smoke/`
- Modify: `aevatar.slnx`
- Modify: `Directory.Packages.props`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdCodexExecTool.cs`
- Modify: `docs/canon/managed-codex-execution.md`
- Modify: `docs/canon/workflow-primitives.md`
- Modify: `docs/operations/2026-07-16-managed-codex-exec-rollout.md`
- Modify: `containers/codex-runner/README.md`

- [ ] **Step 1: Update semantic guard tests/search expectations**

Add a repository assertion or focused source scan ensuring production and smoke projects no longer reference `Alibaba.OpenSandbox`, `OpenSandboxCodexExecutionAdapter`, direct endpoint/API-key options, or Aevatar-owned runner configuration.

- [ ] **Step 2: Remove the direct project/tool and update solution/package references**

Keep `containers/codex-runner` because chrono-sandbox consumes the published image; rewrite its owner language so Aevatar is not described as the OpenSandbox client.

- [ ] **Step 3: Rewrite canonical and operations docs**

Document the actor/Vault/NyxID/chrono ownership chain, self-provisioning constraint, exact service config, kill switch, canary evidence, rollback, and #2899 upgrade boundary.

- [ ] **Step 4: Verify stale terms are absent**

Run: `rg -n 'Alibaba.OpenSandbox|OpenSandboxCodexExecutionAdapter|AddOpenSandboxCodexExecution' src test tools docs containers aevatar.slnx Directory.Packages.props`

Expected: no normal-path references.

### Task 6: Repository Verification

- [ ] **Step 1: Run focused tests**

Run the Channel Identity, ChronoSandbox, Capabilities, and NyxID AI test slices changed above.

- [ ] **Step 2: Run required guards**

Run `bash tools/ci/test_stability_guards.sh`, projection state/version/current-state guards, `bash tools/ci/architecture_guards.sh`, and `bash tools/docs/lint.sh`.

- [ ] **Step 3: Build the solution**

Run: `dotnet build aevatar.slnx --nologo`

- [ ] **Step 4: Review diff and security invariants**

Search the diff for raw key names/values, credentials in protobuf/envelopes/logs/results, wildcard grants, stale direct OpenSandbox ownership, and unrelated changes. Record any external chrono/operations dependency that cannot be verified in this repository.
