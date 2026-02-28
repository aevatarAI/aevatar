# Dynamic Runtime Architecture Scorecard (Re-Score v7)

## 1. Metadata
- Date: 2026-02-28
- Scope: `src/Aevatar.DynamicRuntime.*`, `test/Aevatar.DynamicRuntime.Application.Tests`
- Baseline docs:
  - `docs/architecture/dynamic-gagent-csharp-script-runtime-requirements.md` (v3.3)
  - `docs/architecture/ai-script-runtime-implementation-change-plan.md` (v1.4)
- Scoring intent: architecture alignment score (implementation evidence based)

## 2. Overall Score
- Total: **69 / 100**
- Grade: **C+**
- Verdict: core model has converged on `Script + RoleAgent capability + Container/Compose` semantics, but **strict CQRS single-pipeline** is still not fully established.

## 3. Weighted Breakdown
| Dimension | Weight | Score | Weighted |
|---|---:|---:|---:|
| Layering & Dependency Direction | 10 | 8 | 8.0 |
| CQRS Read/Write Separation & Unified Projection Pipeline | 25 | 11 | 11.0 |
| Actor Source of Truth & Event Sourcing Discipline | 20 | 13 | 13.0 |
| Script Runtime Capability Governance | 15 | 13 | 13.0 |
| Docker/Compose Semantic Alignment | 15 | 11 | 11.0 |
| State Model & Schema Evolution Discipline | 10 | 6 | 6.0 |
| Policy/Security/Operability Guards | 5 | 4 | 4.0 |
| **Total** | **100** |  | **69.0** |

## 4. Key Positives
1. Script capability surface was tightened to command-side-safe primitives (`Chat/Publish/GetState/SetState`), removing direct ReadModel mutation APIs from runtime contract.
2. Event envelope normalization includes trace/correlation/causation/dedup/type metadata and run/container/service context.
3. Idempotency + optimistic version checks are consistently present in command entry points.
4. Daemon/Event/Hybrid service-mode guard exists and blocks illegal replica combinations.
5. Tests now explicitly validate removed ReadModel APIs fail at script compile stage (`CS1061`).

## 5. Major Gaps (Blocking Higher Score)

### 5.1 CQRS not yet single-pipeline (Critical)
- Command path still writes read models directly via `_readStore.Upsert*` / `Append*` in application service.
- This bypasses a strict `Event -> Projection` only read-model construction path.
- Evidence:
  - `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:153`
  - `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:186`
  - `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:319`
  - `src/Aevatar.DynamicRuntime.Application/DynamicRuntimeApplicationService.cs:666`

### 5.2 Projection layer is still store-wrapper style (High)
- `ProjectionBackedDynamicRuntimeReadStore` exposes direct upsert methods instead of reducer-driven event projection flow.
- No reducer/routing definitions found in `Aevatar.DynamicRuntime.Projection`.
- Evidence:
  - `src/Aevatar.DynamicRuntime.Projection/ProjectionBackedDynamicRuntimeReadStore.cs:47`
  - `src/Aevatar.DynamicRuntime.Projection/ProjectionBackedDynamicRuntimeReadStore.cs:136`
  - `src/Aevatar.DynamicRuntime.Projection/ProjectionBackedDynamicRuntimeReadStore.cs:157`

### 5.3 Event model still contains stale ReadModel-mutation event contracts (Medium)
- Proto and run-agent handlers still keep ReadModel mutation events although script runtime API removed these calls.
- This creates conceptual drift and future maintenance ambiguity.
- Evidence:
  - `src/Aevatar.DynamicRuntime.Abstractions/dynamic_runtime_messages.proto:197`
  - `src/Aevatar.DynamicRuntime.Abstractions/dynamic_runtime_messages.proto:216`
  - `src/Aevatar.DynamicRuntime.Core/Agents/ScriptRunGAgent.cs:30`

### 5.4 State evolution model lacks explicit version contract (Medium)
- Current state conflict check is TypeUrl-based only; no first-class `state_version` / expected-version transition in state event contract.
- This limits deterministic merge/conflict semantics under concurrent or replay-heavy scenarios.
- Evidence:
  - `src/Aevatar.DynamicRuntime.Application/ScriptSideEffects/ScriptSideEffectPlanner.cs:36`

### 5.5 Policy ports are minimal-pass guards (Medium)
- Sandbox/resource/build approval policies are skeletal; they satisfy extension points but not production-grade governance depth.
- Evidence:
  - `src/Aevatar.DynamicRuntime.Infrastructure/DefaultScriptSandboxPolicy.cs:5`
  - `src/Aevatar.DynamicRuntime.Infrastructure/DefaultScriptResourceQuotaPolicy.cs:5`
  - `src/Aevatar.DynamicRuntime.Infrastructure/DefaultBuildApprovalPort.cs:5`

## 6. Score Rationale by Dimension
1. Layering & Dependency Direction: clean project split is present, but Application still mixes orchestration and read-side persistence concerns.
2. CQRS Separation & Unified Projection: strongest penalty; direct read-store mutations in command flow violate strict architecture target.
3. Actor & Event Sourcing: run actor persistence and event-first side effect handling are positives, but model still has stale event types.
4. Script Governance: improved materially after removing ReadModel APIs from runtime interface and validating by compile-failure tests.
5. Docker/Compose Alignment: image/container/stack/service/build concepts are present and coherent, with decent lifecycle surface.
6. State Model: `Any` polymorphism works, but versioned state transition protocol is not complete.
7. Policy/Operability: extension points exist, but policy depth is not yet aligned to “production hardening” target.

## 7. Score Uplift Path (to >=85)
1. Enforce strict `Command -> Domain Event` only in application service and delete direct `_readStore.Upsert*` command-path writes.
2. Build explicit dynamic-runtime reducers and route all read-model construction through unified projection runtime.
3. Remove obsolete ReadModel mutation events/handlers from proto and run-agent once reducer-based path is finalized.
4. Introduce `StateEnvelope` + `state_version` + expected-version semantics in `ScriptCustomStateUpdatedEvent`.
5. Upgrade default policy ports with concrete deny-by-default controls and measurable limits.

## 8. Final Assessment
- The refactor direction is correct and the script runtime capability boundary is significantly improved.
- The architecture is **not yet fully aligned** with the repository’s strict CQRS/projection philosophy.
- Current state is a viable transitional architecture; not yet the target end-state defined by v3.3/v1.4 docs.
