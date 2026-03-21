# Refactor Team — Pipeline Autonomous Mode

## Overview

A Claude Code Team that autonomously discovers architectural issues, fixes them in parallel, and submits PRs with multi-reviewer approval. Uses pipeline-style autonomous collaboration where teammates communicate directly via DM, minimizing Lead bottleneck.

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Collaboration model | Pipeline autonomous | Teammates self-organize via DM + shared TaskList; Lead only handles startup, PR submission, and summary |
| Implementer count | 1 (serial) | Avoids worktree conflicts, branch collisions, and merge complexity |
| Implementer persistence | One-shot per issue with worktree | New implementer spawned per issue with `isolation: "worktree"`; rework handled by review lead DM within same worktree session |
| Review panel | 5 reviewers (2 arch + 2 quality + 1 CI) | Ensemble review counters LLM attention drift |
| Model mix | Opus + Sonnet per review category | Cross-model diversity catches more issues |
| Review coordinator | arch-reviewer-opus (review lead) | Collects all reviewer verdicts, makes pass/fail decision, DMs implementer for rework |
| Convergence owner | Review lead (not Team Lead) | Fully autonomous review cycle; Lead only intervenes for PR |
| PR submission | Team Lead | Only Lead has full context for PR description with review records |
| PR target | Integration branch, not master | Safety buffer before mainline |
| Max issues per run | 5 (configurable) | Balances cost with progress |

## Team Roles (8 Teammates + Lead)

### Lead (Main Session)

- **Not a spawned agent** — the main conversation session
- **Duties**:
  1. Create team and integration branch
  2. Spawn auditor → wait for issue list → create Tasks
  3. Spawn 2 implementers + 5 reviewers (persistent teammates)
  4. Receive "APPROVED" DM from review lead → execute `gh pr create`
  5. After all Tasks complete → output summary → TeamDelete

### Auditor

| Field | Value |
|-------|-------|
| Name | `auditor` |
| Model | opus |
| Type | Explore (read-only) |
| Persistence | One-shot (done after creating Tasks) |

- Scans codebase against CLAUDE.md rules
- Creates a Task per issue (severity-ranked)
- DMs Lead when done

### Implementer (one per issue)

| Field | Value |
|-------|-------|
| Name | `implementer` |
| Model | opus |
| Type | general-purpose |
| Persistence | One-shot per issue (new spawn with worktree per task) |
| Isolation | `isolation: "worktree"` on Agent spawn |

- Spawned by Lead for each issue with `isolation: "worktree"`
- Fix in isolated worktree → build → test → CI guard → commit → push
- DM `arch-reviewer-opus`: "Issue #X fixed, branch `refactor/xxx`, please review"
- On rework DM from review lead: fix in same worktree session → push → DM review lead again
- After 3 failed rounds: review lead marks Task as skipped

### Review Lead (arch-reviewer-opus)

| Field | Value |
|-------|-------|
| Name | `arch-reviewer-opus` |
| Model | opus |
| Type | Explore (read-only) |
| Persistence | Persistent |
| Special role | Review coordinator |

- Receives "ready for review" DM from implementer
- Performs own architecture review
- DMs the other 4 reviewers to review the same diff
- Collects all 5 verdicts (including own) → runs convergence logic
- APPROVED → DM Lead with branch name and review record
- CHANGES_REQUESTED → DM implementer with merged fix list
- Tracks review round count (max 3)

### Arch-Reviewer-Sonnet

| Field | Value |
|-------|-------|
| Name | `arch-reviewer-sonnet` |
| Model | sonnet |
| Type | Explore (read-only) |
| Persistence | Persistent |

- Architecture review with extra focus on naming, namespace/directory alignment, API field semantics, Metadata restrictions
- Responds to DM from review lead with VERDICT

### Quality-Reviewer-Opus

| Field | Value |
|-------|-------|
| Name | `quality-reviewer-opus` |
| Model | opus |
| Type | Explore (read-only) |
| Persistence | Persistent |

- Code quality, security (OWASP Top 10), test coverage, over-engineering
- Responds to DM from review lead with VERDICT

### Quality-Reviewer-Sonnet

| Field | Value |
|-------|-------|
| Name | `quality-reviewer-sonnet` |
| Model | sonnet |
| Type | Explore (read-only) |
| Persistence | Persistent |

- Code quality with extra focus on editorconfig, import ordering, blank lines, spelling
- Responds to DM from review lead with VERDICT

### CI-Guard-Runner

| Field | Value |
|-------|-------|
| Name | `ci-guard-runner` |
| Model | sonnet |
| Type | general-purpose |
| Persistence | Persistent |
| Isolation | EnterWorktree per review request |

- Receives DM from review lead → checkout implementer branch → run CI guards + build + test
- Responds to DM from review lead with VERDICT (PASSED/FAILED + details)

## Pipeline Flow

```
Lead
  │
  ├─ TeamCreate("refactor-team")
  ├─ Create integration branch: refactor/YYYY-MM-DD_auto-audit
  │
  ├─ Spawn auditor ──→ scan ──→ TaskCreate per issue ──→ DM Lead "done"
  │
  ├─ Spawn impl-1, impl-2 (persistent, idle until tasks exist)
  ├─ Spawn 5 reviewers (persistent, idle until DM'd)
  │
  │  FOR EACH ISSUE (serial):
  │  ┌─── implementer (worktree) ────────────────────────┐
  │  │ Fix → build → test → commit → push                │
  │  │ DM arch-reviewer-opus: "ready for review"         │
  │  │    ┌─── arch-reviewer-opus (review lead) ───────┐  │
  │  │    │ Own review + DM 4 other reviewers           │  │
  │  │    │ Collect 5 verdicts                          │  │
  │  │    │ Convergence:                                │  │
  │  │    │   APPROVED → DM Lead                        │  │
  │  │    │   CHANGES_REQUESTED → DM implementer        │  │
  │  │    └────────────────────────────────────────────┘  │
  │  │ Rework if needed (max 3 rounds)                    │
  │  └────────────────────────────────────────────────────┘
  │
  ├─ Lead receives "APPROVED" DMs → gh pr create per issue
  │
  ├─ All Tasks completed → summary report
  └─ TeamDelete
```

## DM Protocol

| From | To | Message |
|------|----|---------|
| auditor | Lead | "Audit complete. Created N tasks." |
| impl-N | arch-reviewer-opus | "Issue #X fixed. Branch: `refactor/xxx`. Changed files: ... Please coordinate review." |
| arch-reviewer-opus | arch-reviewer-sonnet | "Review branch `refactor/xxx` diff against integration branch. Original issue: ..." |
| arch-reviewer-opus | quality-reviewer-opus | (same) |
| arch-reviewer-opus | quality-reviewer-sonnet | (same) |
| arch-reviewer-opus | ci-guard-runner | "Run CI guards on branch `refactor/xxx`. Changed files: ..." |
| reviewer | arch-reviewer-opus | "VERDICT: APPROVED/CHANGES_REQUESTED. Issues: ..." |
| arch-reviewer-opus | impl-N | "APPROVED" or "CHANGES_REQUESTED: [merged fix list]" |
| arch-reviewer-opus | Lead | "Issue #X APPROVED. Branch: `refactor/xxx`. Review record: [all 5 verdicts]" |

## Convergence Logic (Review Lead Executes)

```
1. Deduplicate issues:
   - Group by file path
   - Merge issues within 5 lines of each other in same file
   - Same file+region from different reviewers = equivalent

2. Severity filter:
   CRITICAL or HIGH → must fix (any single reviewer)
   MEDIUM or LOW → must fix only if 3+ distinct reviewers flagged
   CI-Guard-Runner FAILED → must fix (treated as CRITICAL)

3. Decision:
   Must-fix exists AND round < 3 → DM implementer with merged list
   Must-fix exists AND round >= 3 → DM Lead "skipped: exceeded 3 rounds"
   No must-fix → DM Lead "APPROVED"
```

## Task States

| Status | Meaning | Set by |
|--------|---------|--------|
| `pending` | Created by auditor, awaiting implementer | Auditor |
| `in_progress` | Claimed by implementer, being fixed/reviewed | Implementer |
| `completed` | PR submitted or marked skipped | Lead |

## Branch Naming

- Integration: `refactor/YYYY-MM-DD_auto-audit`
- Per-issue: `<type>/YYYY-MM-DD_<issue-slug>`
  - Architecture violations → `refactor/`
  - Bugs → `fix/`
  - Naming/style → `chore/`
  - Test gaps → `test/`

## Failure Handling

| Scenario | Action |
|----------|--------|
| Implementer build/test fails | Implementer retries (counts toward 3-round limit) |
| Reviewer timeout | Review lead proceeds with available verdicts (minimum 1 needed) |
| Implementer timeout | Review lead marks task skipped, impl claims next |
| 3 review rounds exhausted | Review lead DMs Lead "skipped"; impl claims next |
| All 5 reviewers fail | Review lead re-requests once; if still all fail, skip |
| PR merge conflict | Lead logs "skipped: merge conflict", no force push |

## Safety Boundaries

| Constraint | Value |
|------------|-------|
| Max review rounds per issue | 3 |
| Max issues per run | 5 (configurable) |
| PR target | Integration branch, never master |
| Implementer isolation | EnterWorktree per task |
| CI runner isolation | EnterWorktree per review |
| Reviewers | Read-only Explore agents |
| Destructive operations | No force push / reset --hard / branch delete |
