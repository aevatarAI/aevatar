---
name: arch-audit
description: Use when auditing architecture compliance against CLAUDE.md, checking for drift before milestones, validating code health after large merges, or running periodic architecture checks. Triggers on "架构审计", "architecture audit", "check drift", "code health".
---

# Architecture Audit

Milestone-oriented architecture compliance audit. Runs CI guards, deep-dives hot paths, checks for blind spots, outputs a scored health card.

## When to Use

- After merging large feature branches (100+ files)
- Before milestone deliveries (demos, releases, M0/M1 gates)
- When CEO/architect suspects drift from CLAUDE.md
- Periodic health check (monthly recommended)

## Execution Flow

```
Step 0.5: Frontend Build Check
    |
Step 1a: Environment Validation (dotnet restore/build, rg)
    |
Step 1b: CI Guards (architecture_guards.sh + specialized guards + Architecture.Tests)
    |
Step 2: Hot Path Deep Dive (serial, checkpoints between each)
    2a: Projection spine integrity (host bypass detection)
    2b: Query/read honesty (readmodel-only verification)
    2c: Middleware state leaks (ConcurrentDictionary singleton scan)
    2d: Governance substance (config CRUD vs domain model)
    |
Step 3: Milestone Path Verification (E2E for next milestone)
    |
Output: docs/audit-scorecard/YYYY-MM-DD-architecture-audit.md
```

## Step 0.5: Frontend Build Check (~10min)

Check all frontend projects can build. Use whichever package manager is available (pnpm > npm > bun).

```bash
# Find frontend projects
find . -name "package.json" -not -path "*/node_modules/*" -not -path "*/obj/*" | head -10
# Try building each
npm run build 2>&1 | tail -10
```

If build fails: tag as `DEMO_BLOCKER` with missing dependencies listed.

## Step 1: Automated Baseline

### 1a. Environment Validation (~10min)

```bash
dotnet restore <solution> --nologo
dotnet build <solution> --nologo     # 0 errors required to continue
which rg                              # ripgrep required for guards
```

If build fails: **STOP**. Build failure IS the audit finding. Report and fix build first.

Record build warnings. High coupling warnings (CA1506 > 96 types) are architectural signals.

### 1b. CI Guards Execution (~35min)

```bash
bash tools/ci/architecture_guards.sh
# Plus specialized guards (can run in parallel):
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/ci/workflow_binding_boundary_guard.sh
# Architecture tests:
dotnet test test/Aevatar.Architecture.Tests/ --nologo
```

**Failure classification:**
- `VIOLATION` — real architecture rule breach
- `env-tooling` — missing tool (rg, pnpm, dotnet version)
- `env-structural` — script assumes directory/file that was moved/deleted → treat as VIOLATION

## Step 2: Hot Path Deep Dive (~40min)

Run **serial with 2-minute checkpoints** between each step.

### 2a. Projection Spine Integrity (highest priority)

Check if host layer bypasses the unified projection pipeline.

```bash
rg "SubscribeAsync<EventEnvelope>|AGUISseWriter|TryMapEnvelopeToAguiEvent|TaskCompletionSource" \
  src/platform/Aevatar.GAgentService.Hosting src/workflow
```

**Judgment criteria:**
- `TaskCompletionSource` used to block-wait for actor event reply (create TCS → subscribe → await TCS) = **VIOLATION** (bypass)
- Endpoint directly subscribing to actor event stream without going through Projector = **VIOLATION**
- SSE streaming based on already-materialized readmodel = legitimate

Compare against formal projection path (e.g., `WorkflowExecutionRunEventProjector`).

### 2b. Query/Read Honesty

Verify Application-layer query services only read from readmodel interfaces.

```bash
rg "IEventStore|Replay|GetGrain|\.State\b" \
  src/platform/Aevatar.GAgentService.Application \
  src/workflow/Aevatar.Workflow.Application/Queries
```

Any match = **VIOLATION** (query-time replay/priming forbidden by CLAUDE.md).

### 2c. Middleware State Leaks

Scan for forbidden singleton state patterns. **Include all directories that contain runtime code, not just src/.**

```bash
rg "Dictionary<|ConcurrentDictionary<|HashSet<|Queue<" \
  src/platform src/Aevatar.CQRS.Projection.Core \
  src/workflow/Aevatar.Workflow.Application \
  src/Aevatar.Scripting.Infrastructure \
  agents/
```

**Exclude:** method-local variables, readmodel metadata configuration, ImmutableDictionary (read-only semantics).

**Flag:** Any `ConcurrentDictionary` field in a Singleton-registered service = **VIOLATION** (middle-layer state constraint).

### 2d. Governance Substance

Read the Governance subsystem core files and answer one question: **is this organizational governance or config CRUD?**

Key files:
- `ServiceConfigurationGAgent.cs` — what commands does it handle?
- `InvokeAdmissionService.cs` — what does it evaluate?
- `ServiceGovernanceQueryApplicationService.cs` — what does it expose?

Look for: Goal/Scope/ObjectiveFunction modeling, three-layer governance (order definition / optimization / adaptability), recursive composition patterns. Absence is a finding.

## Step 3: Milestone Path Verification (~30min)

1. Identify next milestone (check CEO repo or TODOS.md)
2. Find the critical code path for that milestone
3. Check if that path has integration tests: `find test/ -name "*<keyword>*"`
4. Run relevant integration tests: `dotnet test <test-project> --filter "FullyQualifiedName~<keyword>"`
5. Tag test failures as `MILESTONE_BLOCKER`

**Blind spot check:** Does `architecture_guards.sh` scan the milestone-critical directories? If not, that's a guard supplement finding.

## Scorecard Template

Write to `docs/audit-scorecard/YYYY-MM-DD-architecture-audit.md`:

```markdown
---
title: Architecture Audit — [Scope Description]
status: active
owner: [auditor]
---

# Architecture Health Scorecard — YYYY-MM-DD

Audit scope: [milestone-oriented / full / targeted]
Next milestones: [list with dates]

## Dimension Scores

| Dimension | Score | Evidence |
|-----------|-------|---------|
| CI Guards 合规 | X/10 | [pass/fail summary] |
| 分层合规 | X/10 | [coupling warnings, dependency direction] |
| 投影一致性 | X/10 | [bypass count, formal path coverage] |
| 读写分离 | X/10 | [query honesty results] |
| 序列化 | X/10 | [Protobuf compliance] |
| Actor 生命周期 | X/10 | [state leak count, shadow state] |
| 前端可构建性 | X/10 | [build results] |
| 测试覆盖 (关键路径) | X/10 | [milestone path test results] |
| 产品 Thesis 体现度 | X/10 | [Harness Theory code evidence] |

## 漂移清单

### MILESTONE_BLOCKER

| # | 位置 | 违反规则 | 严重度 | 描述 |
|---|------|---------|--------|------|

### BACKLOG

| # | 位置 | 违反规则 | 严重度 | 保质期 |
|---|------|---------|--------|--------|

## Guard 补充建议

| 建议 | 优先级 | 原因 |
|------|--------|------|
```

## Common Blind Spots

From first audit (2026-04-08):
- `agents/` directory not scanned by `architecture_guards.sh`
- Frontend build not checked by any CI guard
- `StreamingProxyGAgent._proxyState` shadow state not caught by existing mutation detectors
- Host-layer direct actor subscription (TCS pattern) not covered by guards

## Key Rules

1. **Milestone-orient everything.** Tag each finding as MILESTONE_BLOCKER or BACKLOG. Only MILESTONE_BLOCKER gets fixed now.
2. **Classify guard failures.** VIOLATION vs env-tooling vs env-structural. env-structural = VIOLATION.
3. **Check blind spots.** Every audit should verify CI guard scan roots cover all runtime code directories.
4. **Scorecard goes to team meeting.** Ask: "which of these are intentional vs accidental?" Intentional = ADR. Accidental = fix.
