# Agent Profile NyxID Mutation Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every Agent Profile mutation usable through the required NyxID production ingress while preserving the existing standard Header contract.

**Architecture:** Resolve optional typed body fallbacks at the Mainnet Host boundary, then pass the existing single idempotency key and expected authority-state version into `AgentProfileApplicationService`. Header and body representations must agree when both exist; no Actor, Application, projection, Admin JavaScript, or NyxID repository changes are required.

**Tech Stack:** ASP.NET Core Minimal APIs, C# records with `JsonUnmappedMemberHandling.Disallow`, xUnit, FluentAssertions.

## Global Constraints

- Keep `Domain / Application / Infrastructure / Host` layering; transport fallback logic stays in Host.
- Keep command/event and read-model semantics unchanged.
- Preserve standard `Idempotency-Key` and strong `If-Match` behavior for direct and Admin clients.
- Reject missing, malformed, negative, conflicting, and stale values before Actor dispatch.
- Do not change binding state while creating the production `aevatar-operator` Profile.
- Use `nyxid proxy request aevatar` for every authenticated production API call.

---

### Task 1: Add typed body fallback contract and endpoint behavior

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/AgentProfiles/AgentProfileEndpoints.cs`
- Test: `test/Aevatar.Capabilities.Tests/MainnetAgentProfileEndpointHandlerTests.cs`

**Interfaces:**
- Consumes: existing `Idempotency-Key`, `If-Match`, strong profile/binding ETags, and Agent Profile input records.
- Produces: optional JSON `idempotencyKey` and `expectedVersion` inputs resolved to the existing Application request fields.

- [ ] **Step 1: Write failing create and draft body-fallback tests**

Add endpoint tests that omit both standard headers, send `idempotencyKey` and (for draft) `expectedVersion` in JSON, require `202 Accepted`, and assert the recorded Actor command contains the resolved authority version and stable operation identity.

- [ ] **Step 2: Write failing conflict and missing-value tests**

Add tests proving unequal header/body idempotency keys return `400`, unequal header/body expected versions return `400`, stale body-only expected versions return `412`, and missing concurrency still returns `428`. Assert no Actor command was recorded.

- [ ] **Step 3: Write failing publish and binding wiring tests**

Exercise publish and clear-binding with body-only fields so their recorded commands carry the resolved expected version. Exercise set-binding conflict handling before dispatch. Keep the test sealer and read models deterministic.

- [ ] **Step 4: Run RED tests**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~MainnetAgentProfileEndpointHandlerTests
```

Expected: the new body-only requests fail because current DTOs reject the added JSON fields or still require headers.

- [ ] **Step 5: Implement the minimum Host fallback**

Extend only the existing Host input records. Resolve one idempotency key from Header/body and one authority version from strong ETag/body. If both representations are supplied, validate equality; if body-only expected version differs from the current read-model ETag version, return `412`. Pass only resolved primitives into the unchanged Application request records.

- [ ] **Step 6: Run GREEN tests and guards**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~MainnetAgentProfileEndpointHandlerTests
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
dotnet build aevatar.slnx --no-restore --nologo
dotnet test aevatar.slnx --no-restore --nologo
```

Expected: all commands exit 0; the existing Header tests and new body-fallback tests both pass.

- [ ] **Step 7: Commit and push safely**

```bash
git fetch origin feature/integrate
test "$(git merge-base HEAD origin/feature/integrate)" = "$(git rev-parse origin/feature/integrate)"
git add src/Aevatar.Mainnet.Host.Api/AgentProfiles/AgentProfileEndpoints.cs test/Aevatar.Capabilities.Tests/MainnetAgentProfileEndpointHandlerTests.cs docs/superpowers/specs/2026-07-31-agent-profile-guided-creation-design.md docs/superpowers/plans/2026-07-31-agent-profile-nyxid-mutation-fallback.md
git commit -m "Enable NyxID Agent Profile mutations"
git push origin HEAD:feature/integrate
```

Never force-push. If the remote moved, inspect and merge only after confirming non-overlap.

### Task 2: Complete production Profile and UX acceptance

**Files:**
- No production source changes unless verification exposes a new defect.

**Interfaces:**
- Consumes: deployed body fallback, `aevatar-platform@1.14` exact closure evidence, Agent Profile read models, and the canonical Admin route.
- Produces: published personal `aevatar-operator` with unchanged `nyxid.chat` binding and recorded visual acceptance.

- [ ] **Step 1: Prove deployment and identity**

Require the production Pod image to match the pushed commit, rerun an exact-skill canary, `nyxid whoami`, and `/api/workflow/observatory/me`.

- [ ] **Step 2: Create, save, validate, and publish**

Use fresh body `idempotencyKey` values and read-model `authorityStateVersion` as body `expectedVersion`. Match every accepted operation by `operationId` and typed terminal outcome. Require validation `isValid=true` with empty diagnostics and publication `PROFILE_PUBLISHED` with `executionAvailable=true`.

- [ ] **Step 3: Prove binding unchanged**

Read `/api/scopes/{scopeId}/agent-profile-bindings/nyxid.chat` and compare it with the saved baseline. Do not call binding PUT or DELETE.

- [ ] **Step 4: Perform canonical Admin acceptance**

Open `https://aevatar-console-backend-api.aevatar.ai/admin#/agent-profiles` and check desktop/narrow layout, keyboard focus, three-stage creation, exact evidence/tool chips, collapsed advanced controls, honest accepted/projection/published states, and absence of `#/agentProfiles` routing.
