---
title: Auto-refactor loop (14 iterations) — summary scorecard for PR #678
status: active
owner: codex-refactor-loop
issue: 678
---

# Auto-refactor loop summary — 2026-05-19

Unattended refactor pass against `CLAUDE.md` philosophy violations using the
`codex-refactor-loop` skill (analyze → implement → verify per cluster in
isolated git worktrees, with /loop dynamic wakeups as the pacing primitive).

## Headline

| Metric | Count |
|---|---|
| Iterations | 14 |
| Clusters merged | 23 |
| CI guards installed | 3 |
| Commits to trunk | 46 |
| Reworks (verify-fail → re-implement → pass) | 4 |
| External repo changes (NyxID / chrono-*) | 0 |
| New features added | 0 |
| Targeted test pass count (accumulated) | 600+ |

## Cluster ledger (by iteration)

| Iter | Cluster | Theme | Net lines |
|---|---|---|---|
| 1 | 001 | Catalog actor no longer owns runner execution facts | −237 (post-rework: deletion-heavy) |
| 1 | 002 | Authoring lifecycle commands accepted-only | −290 |
| 1 | 003 | ScopeService AGUI moved to projection pipeline | +160 |
| 1 | 004 | Agent SSE through typed projection sessions | +338 |
| 1 | 005 | StreamingToolExecutor Channel<T> coordinator | +59 |
| 2 | 006 | ScopeBinding command accepted-only | −24 |
| 2 | 007 | Household tool dispatch + ChatStreamAsync | +123 |
| 4 | 008 | StreamingProxy dispatch-tail (closes cluster-004) | +104 |
| 4 | 009 | UserAgentCatalogCommandPort accepted-only | −206 |
| 5 | 010 | ScopeGAgent host AGUI mapper residual | +21 |
| 5 | 011 | retired command-pipeline canon drift | −49 |
| 5 | 012 | CatalogCommandOutcome dead-enum removal | +63 |
| 6 | 013 | A2A InMemory default registration leak | +83 |
| 6 | 014 | ChannelRegistrationTool delete polling removal | −12 |
| 6 | 015 | Workflow streaming canon retired-name rewrite | +3 |
| 7 | **016** | **CI guard**: dispatch/projection source regression | +58 |
| 7 | 017 | workflow-runtime.md canon drift fix | ±0 |
| 8 | **018** | **CI guard**: forbidden Web/API port 5000/5050 | +47 |
| 10 | 019 | Tool-provider HTTP clients → IHttpClientFactory | +290 |
| 11 | 020 | StaticHandlerAdapter compiled-delegate handler | +151 |
| 11 | 021 | Workflow backpressure queue O(1) cursor | +146 |
| 12 | **022** | **CI guard**: workflow actor query endpoint authz | +96 |
| 13 | 023 | OpenTelemetry per-package CVE patch | +9 |

## CI guards installed

Each guard was probe-tested (trunk passes → temporary anti-pattern probe makes
guard fail → probe removed → guard passes again) before merge.

1. **Dispatch / projection source regression guard** (cluster-016)
   `tools/ci/architecture_guards.sh`. Blocks new non-runtime production calls
   to `actor.HandleEventAsync`, `.HandleEventAsync(`, or
   `SubscribeAsync<EventEnvelope>`. Allowlist restricted to 3 runtime transport
   files. Terminates the "same anti-pattern in another file → new cluster"
   cycle that filled clusters 008/014.

2. **Forbidden Web/API port guard** (cluster-018)
   `tools/ci/architecture_guards.sh`. Honors AGENTS.md hard rule that
   forbids `5000`/`5050` for Web/API; flags `localhost:5000`, `localhost:5050`,
   `127.0.0.1:5000`, `127.0.0.1:5050`, and `defaultPort: 5000/5050` patterns
   in production source, docs, demos, and tests.

3. **Workflow actor query endpoint authz guard** (cluster-022)
   `tools/ci/architecture_guards.sh`. Requires every `ChatQueryEndpoints`
   actor query mapping to call `.RequireAuthorization()` or carry an explicit
   per-endpoint `security-allowlist: <reason>` comment.

## Out-of-scope follow-ups

These were surfaced by the loop but **not** auto-implemented because the
fix crosses from refactor into security feature work; documented for
follow-up manual review.

- **cluster-022 (workflow actor query authz, full fix)**: needs a
  caller-scope contract on workflow query ports, scope threading through
  AI tools (`ActorInspectTool`, `WorkflowStatusTool`, `EventQueryTool`),
  and tenant/owner filtering on readmodel reads. Recommend a security PR
  driven by a threat-model review (e.g. `/cso` skill or manual review).
  Current state: governance CI guard installed; query endpoints carry
  explicit `security-allowlist` dev-only comments.

## Reworks (verify → rework → pass)

- **cluster-001 → cluster-001 (rework)**: verify caught catalog actor still
  writing 5 execution fields + proto messages not deleted. Rework deleted
  fields, added `UserAgentCatalogReadModelEntry` DTO, removed proto
  messages with field reservations.
- **cluster-003 → cluster-003 (rework)**: 5 new projection types lacked
  class-level XML docs. Rework added concise summaries.
- **cluster-004 → cluster-004 (rework)**: NyxID coverage test missing
  symmetric `actor.HandleEventAsync` regression assertion. Rework added it.
- **cluster-001 → cluster-001 (second rework round)**: deeper proto cleanup
  + caller-scope DTO split landed cleanly.

## Test pass counts (representative, per cluster)

- cluster-002: 8 (AgentBuilderToolTests)
- cluster-003: 103 (ScopeServiceEndpointsStreamTests + Tests)
- cluster-004: 89 (NyxIdChat + StreamingProxy coverage)
- cluster-005: 21 (StreamingToolExecutor + ToolApprovalMiddleware)
- cluster-006: 121 (ScopeBindingCommandApplicationService + integration)
- cluster-007: 41 (Household entity)
- cluster-008: 52 (StreamingProxy)
- cluster-009: 39 (UserAgentCatalogCommandPort + tools)
- cluster-010: ScopeGAgentAguiEventMapper + endpoints (clean migration)
- cluster-011: docs lint
- cluster-012: 40 (UserAgentCatalogCommandPort + 2 tools)
- cluster-013: A2A integration tests
- cluster-014: 18 (ChannelRegistrationTool)
- cluster-019: 209 (tool-provider HttpClient registration + behavior)
- cluster-020: 233 (Foundation.Core pipeline)
- cluster-021: 11 (Backpressure)

## Saturation signals

- iter3 strict audit: 0 cluster → confirmed iter1-2 covered the architectural top.
- iter9 strict audit: 0 cluster → confirmed iter4-8 broadened-scope work complete.
- iter14 must-fix audit: 0 cluster → confirmed remaining state has no
  reproducible bug / critical security exposure justifying autonomous work.

## How this was produced

`codex-refactor-loop` skill (in `.claude/skills/codex-refactor-loop/`).

- Controller: Claude Code session driving /loop dynamic wakeups.
- Implementer: `codex exec` subprocesses, one per phase per cluster.
- Isolation: separate `git worktree` per cluster; merge to trunk only after
  verify codex returns pass (or controller override with documented reason).
- Source of truth: `.refactor-loop/` directory (state.json, prompts, logs,
  runs) — git-ignored.

## Recommendation

PR #678 is at a natural review point. Suggested next actions:
1. Backend reviewers (louis4li / eanzhao / jason-aelf) merge after CI green
   on `dev` base.
2. Security PR (separate) addresses cluster-022 full fix via `/cso` or
   manual threat-model review.
3. Optional: future PRs can rely on the 3 new CI guards to prevent
   regressions on the cleaned-up patterns.
