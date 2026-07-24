# Agent Profile Final Review Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close all five Important whole-branch Agent Profile review findings without adding a Phase 1 runtime consumer.

**Architecture:** Application-originated commands receive an RSA-PSS signed typed proof at the Infrastructure port and Core verifies it before any decision. Discovery adds the missing scope predicate, system reconciliation keys retries by observed authority version, Actor state compacts operation replay records to documented count-bounded windows, and default-binding multiplicity becomes publish-only validation.

**Tech Stack:** .NET 10, C#, Protobuf, RSA-PSS/SHA-256, xUnit, FluentAssertions, existing Actor event sourcing and Projection Pipeline.

## Global Constraints

- Preserve strict `Domain / Application / Infrastructure / Host` layering; Core depends only on an ingress-proof verifier abstraction and Infrastructure owns signing/key parsing.
- All command authorization, retention, retry, and draft-validity semantics are strongly typed; do not use `Metadata`, `Headers`, `Items`, JSON, or string-key bags.
- Queries read only existing read models and never prime, activate, replay, or side-read Actor/event-store state.
- Actor facts and compaction remain Actor-owned; do not add a service-level dictionary/cache/registry.
- Phase 1 still adds no Profile field or consumer to Chat, WebSocket, NyxID, conversation, member, or channel paths.
- The only accepted skill dependency remains `ExactOrnnSkillReference`; do not add name/latest/range fallback.
- Mutating responses remain accepted-for-dispatch only.
- Tests must use distinct Profile, member, workflow, service, operation, command, and correlation identities.
- Follow strict RED -> GREEN -> REFACTOR for every behavioral correction and record exact commands/output in the report.

---

### Task 1: Remediate Whole-Branch Review Findings

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Protos/AgentProfiles/agent_profiles.proto`
- Create: `src/platform/Aevatar.GAgentService.Abstractions/AgentProfiles/IAgentProfileIngressProofVerifier.cs`
- Create: `src/platform/Aevatar.GAgentService.Abstractions/AgentProfiles/AgentProfileIngressProofIntegrity.cs`
- Create: `src/platform/Aevatar.GAgentService.Infrastructure/AgentProfiles/AgentProfileIngressProofOptions.cs`
- Create: `src/platform/Aevatar.GAgentService.Infrastructure/AgentProfiles/AgentProfileIngressProofService.cs`
- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/AgentProfiles/AgentProfileActorPort.cs`
- Modify: `src/platform/Aevatar.GAgentService.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs`
- Modify: `src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs`
- Create: `src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileOperationRetentionPolicy.cs`
- Modify: `src/platform/Aevatar.GAgentService.Application/AgentProfiles/AgentProfileQueryApplicationService.cs`
- Modify: `src/platform/Aevatar.GAgentService.Application/AgentProfiles/SystemAgentProfileProvisioningService.cs`
- Modify: `tools/ci/agent_profile_boundary_guard.sh`
- Modify: `docs/superpowers/specs/2026-07-22-agent-profile-design.md`
- Modify: `docs/canon/agent-profiles.md`
- Test: `test/Aevatar.GAgentService.Tests/Abstractions/AgentProfileContractsTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Infrastructure/AgentProfileActorPortTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Core/AgentProfileNamespaceGAgentTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Core/AgentProfileGAgentTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/AgentProfileQueryApplicationServiceTests.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/SystemAgentProfileProvisioningServiceTests.cs`
- Test: `test/Aevatar.GAgentService.Integration.Tests/SystemAgentProfileBootstrapTests.cs`
- Test: relevant Hosting/endpoint DI tests that compose the proof key ring.

**Interfaces:**
- Produces `AgentProfileIngressProof` and `AgentProfileIngressProofSigningMaterial` Protobuf messages.
- Produces `IAgentProfileIngressProofVerifier.Verify(string targetActorId, IMessage command)`.
- Produces `AgentProfileIngressProofIntegrity.ComputeCanonicalCommandSha256(...)` and deterministic signing material for only the five Application command types.
- Produces `AgentProfileIngressProofService`, whose public surface verifies and whose Infrastructure-only surface signs.
- Produces `AgentProfileOperationRetentionPolicy.MaxRetainedProfileMutationOperations = 256` and `MaxRetainedNamespaceTerminalOperations = 1024`.

- [ ] **Step 1: Write failing ingress-proof contract and cryptographic tests**

Add tests that require proof fields on exactly the five Application-originated commands; deterministic hashing with the proof cleared; RSA-PSS verification for exact target/type/payload; failure after changing target, TypeUrl, owner, expected version, draft/binding/snapshot, digest, signature, or key id; previous-public-key acceptance; revoked-key rejection; and proof/signature absence from events/state/read models/audit. Run the exact new tests and capture expected RED failures caused by missing contracts/types.

- [ ] **Step 2: Implement the minimal signed proof boundary and verify GREEN**

Define the typed proof/signing material. Bind `Aevatar:AgentProfiles:IngressProof` to a current PKCS#8 RSA private key and key-id-indexed SubjectPublicKeyInfo public keys. Require RSA keys of at least 2048 bits and fixed RSA-PSS/SHA-256. Sign in `AgentProfileActorPort` after the final target is known. Verify at the first line of each external Actor handler, before `RequireOperation`, lookup, or persistence. Use stable code `PROFILE_INGRESS_PROOF_INVALID` for every fail-closed Actor rejection and never echo cryptographic details. Run the focused contract/infrastructure/core tests and record GREEN.

- [ ] **Step 3: Write and pass discovery visibility tests**

Write RED tests proving a valid user Profile in `scope-other` is not visible to a caller in `scope-gamma`, no execution query occurs for the hidden entry, the same-scope Profile is visible, and a fully valid `system/*` entry remains globally visible. Implement a typed-owner visibility predicate using the normalized caller scope and ordinal equality, then run the query tests to GREEN.

- [ ] **Step 4: Write and pass reconciliation race-convergence tests**

Write RED tests proving the same definition/digest/step at authority versions 11 and 12 produces distinct operation ids, while two reads at version 11 remain the same operation. Add an integration test that dispatches a stale version, observes committed `DRAFT_VERSION_CONFLICT` through the normal Profile projection, reruns reconciliation after the newer management read model is visible, and observes the desired mutation commit. Include `authority-version:<observedVersion>` only in mutation/publish operation identity; create remains stable. Run focused Application and integration tests to GREEN.

- [ ] **Step 5: Write and pass bounded operation-retention tests**

Write RED Actor tests that exceed 256 Profile mutation operations and 1,024 Namespace terminal operations, assert bounded repeated fields/serialized state size, replay the oldest retained operation without a new event, reject retained payload drift, keep the one initialization recovery record, and keep PROVISIONING/FAILED namespace operations pinned until ACTIVE. Implement ordered compaction only in state-event appliers via `AgentProfileOperationRetentionPolicy`; do not use time, timers, query calls, or process-local collections. Run both Core Actor test files to GREEN.

- [ ] **Step 6: Write and pass draft-versus-publish validation tests**

Replace the old mutation-time rejection expectations with RED tests proving initialization, full update, and upsert can commit two default bindings. Keep/extend tests proving `AgentProfileDraftValidator` and publish sealing return `MULTIPLE_DEFAULT_SKILLS`, no Ornn resolution occurs for that invalid publish shape, and the Actor rejects a forged sealed publish against such a draft. Remove only initialization/update/upsert default-count checks; retain publish defense. Run Core and Application tests to GREEN.

- [ ] **Step 7: Lock semantics in docs and guards**

Update the approved design and canon with signed ingress, same-scope discovery, observed-version retry identity, the exact 256/1,024 retention contract, post-eviction semantics, pinned protocol records, and publish-only default multiplicity. Extend `agent_profile_boundary_guard.sh` to require external-handler proof verification and retention policy usage without string matching TypeUrls. Run `bash tools/ci/agent_profile_boundary_guard.sh`, `bash tools/ci/test_stability_guards.sh`, `bash tools/ci/query_projection_priming_guard.sh`, `bash tools/ci/projection_state_version_guard.sh`, `bash tools/ci/projection_state_mirror_current_state_guard.sh`, and `bash tools/ci/projection_route_mapping_guard.sh`.

- [ ] **Step 8: Run consolidated verification, self-review, and commit**

Run all Agent Profile focused tests plus affected Host/DI tests, `dotnet build aevatar.slnx --nologo`, and `git diff --check`. Review that no proof/private key enters committed or projected data, no runtime consumer changed, and every finding has a regression test. Write RED/GREEN evidence and command results to `.superpowers/sdd/final-review-fix-report.md`, then commit with subject `Harden agent profile authority boundaries`.
