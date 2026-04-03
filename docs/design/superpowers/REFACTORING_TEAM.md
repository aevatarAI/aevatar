# Automated Refactoring Team

## Overview

A fully automated multi-agent refactoring team that autonomously discovers architectural issues in the codebase, fixes them, and submits PRs with multi-reviewer approval. The user only intervenes at the integration branch merge stage.

The team uses a pipeline-style autonomous collaboration model where teammates communicate directly via DM, minimizing Lead bottleneck. A single skill entry point (`.claude/skills/refactor-team/SKILL.md`) contains the Team Lead orchestration logic. It uses `TeamCreate` to establish a Claude Code Team with shared task list, then spawns named team members via `Agent` tool with `team_name`, `subagent_type`, `model`, and `isolation` parameters. Supporting markdown files define prompts for each agent role (Auditor, Implementer, 4 Reviewers, CI-Guard-Runner). Team is cleaned up with `TeamDelete` on completion.

**Tech Stack:** Claude Code skills (YAML frontmatter + markdown), Agent tool, git, `gh` CLI, `dotnet` CLI, bash CI guard scripts.

### Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Autonomy level | Fully automated | User intervenes only at integration branch merge |
| Collaboration model | Pipeline autonomous | Teammates self-organize via DM + shared TaskList; Lead only handles startup, PR submission, and summary |
| Issue discovery | Self-audit against CLAUDE.md + CI guards | No dependency on pre-existing audit docs |
| Implementer count | 1 (serial) | Avoids worktree conflicts, branch collisions, and merge complexity |
| Implementer persistence | One-shot per issue with worktree | New implementer spawned per issue with `isolation: "worktree"`; rework handled by review lead DM within same worktree session |
| Review panel | 5 reviewers (2 arch + 2 quality + 1 CI) | Ensemble review counters LLM attention drift |
| Model mix | Opus + Sonnet per review category | Cross-model diversity catches more issues |
| Review coordinator | arch-reviewer-opus (review lead) | Collects all reviewer verdicts, makes pass/fail decision, DMs implementer for rework |
| Convergence owner | Review lead (not Team Lead) | Fully autonomous review cycle; Lead only intervenes for PR |
| Convergence | Severity-based (Critical/High: any-one; Medium/Low: majority) | Balances thoroughness with efficiency |
| PR scope | Single issue per PR | Small PRs are easier to review and revert |
| PR target | Integration branch, not master | Safety buffer before mainline |
| PR submission | Team Lead | Only Lead has full context for PR description with review records |
| Integration base | Current working branch (not master) | Refactoring builds on in-progress work |
| Max issues per run | 5 (configurable) | Balances cost per run with meaningful progress per session |
| Sequential processing | One issue at a time | Avoids merge conflicts between concurrent fixes; simpler orchestration |

---

## Pipeline Mode

### Team Roles (8 Teammates + Lead)

#### Lead (Main Session)

- **Not a spawned agent** -- the main conversation session
- **Model**: Opus
- **Duties**:
  1. Create team and integration branch
  2. Spawn auditor -> wait for issue list -> create Tasks
  3. Spawn 2 implementers + 5 reviewers (persistent teammates)
  4. Receive "APPROVED" DM from review lead -> execute `gh pr create`
  5. After all Tasks complete -> output summary -> TeamDelete

#### Auditor

| Field | Value |
|-------|-------|
| Name | `auditor` |
| Model | opus |
| Type | Explore (read-only) |
| Persistence | One-shot (done after creating Tasks) |

- Scans codebase against CLAUDE.md rules
- Creates a Task per issue (severity-ranked)
- DMs Lead when done

**Output format** (flat list, no markdown headings to avoid embedding conflicts):

```
[CRITICAL] Issue title
  Violated rule: Specific CLAUDE.md clause
  File location: src/Xxx/Yyy.cs:L42-L58
  Description: What is violated and why it matters
  Fix direction: Suggested approach (no code)
  Related CI guard: tools/ci/xxx_guard.sh

[HIGH] Issue title
  ...
```

**Strategy**:
- Scan code against CLAUDE.md section by section, skip nothing
- Cross-validate each finding by checking if related CI guard scripts would catch it
- Exclude known exemptions (e.g., InMemory implementations for testing)
- Merge multiple symptoms of the same root cause into one issue

#### Implementer (one per issue)

| Field | Value |
|-------|-------|
| Name | `implementer` |
| Model | opus |
| Type | general-purpose |
| Persistence | One-shot per issue (new spawn with worktree per task) |
| Isolation | `isolation: "worktree"` on Agent spawn |

- Spawned by Lead for each issue with `isolation: "worktree"`
- Fix in isolated worktree -> build -> test -> CI guard -> commit -> push
- DM `arch-reviewer-opus`: "Issue #X fixed, branch `refactor/xxx`, please review"
- On rework DM from review lead: fix in same worktree session -> push -> DM review lead again
- After 3 failed rounds: review lead marks Task as skipped

**Workflow**:
1. Read issue description and related source files
2. Design minimal fix
3. Implement fix
4. Add/update tests for behavioral changes
5. `dotnet build aevatar.slnx --nologo` -- confirm compilation
6. `dotnet test aevatar.slnx --nologo` -- confirm tests pass
7. Run issue-related CI guard scripts
8. Commit (imperative message, single purpose)
9. Push worktree branch to remote (so reviewers can access it)

**Constraints**:
- No changes beyond issue scope
- No unnecessary comments, docs, or type annotations
- Follow `.editorconfig` and existing code style

#### Review Lead (arch-reviewer-opus)

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
- Collects all 5 verdicts (including own) -> runs convergence logic
- APPROVED -> DM Lead with branch name and review record
- CHANGES_REQUESTED -> DM implementer with merged fix list
- Tracks review round count (max 3)

**Architecture review checklist**:
- Layering correctness (Domain/Application/Infrastructure/Host)
- Dependency inversion direction
- Actor boundary integrity
- Read/write separation maintained
- Serialization uniformity (Protobuf)
- Naming follows semantic-first principle
- No new in-process state mappings introduced
- Fix addresses root cause, not workaround

#### Arch-Reviewer-Sonnet

| Field | Value |
|-------|-------|
| Name | `arch-reviewer-sonnet` |
| Model | sonnet |
| Type | Explore (read-only) |
| Persistence | Persistent |

- Architecture review with extra focus on naming, namespace/directory alignment, API field semantics, Metadata restrictions
- Responds to DM from review lead with VERDICT

#### Quality-Reviewer-Opus

| Field | Value |
|-------|-------|
| Name | `quality-reviewer-opus` |
| Model | opus |
| Type | Explore (read-only) |
| Persistence | Persistent |

- Code quality, security (OWASP Top 10), test coverage, over-engineering
- No prohibited `Task.Delay`/`WaitUntilAsync` in tests
- No `GetAwaiter().GetResult()` patterns
- No unnecessary backward-compatibility hacks
- Changes are minimal (no unrelated modifications)
- Responds to DM from review lead with VERDICT

#### Quality-Reviewer-Sonnet

| Field | Value |
|-------|-------|
| Name | `quality-reviewer-sonnet` |
| Model | sonnet |
| Type | Explore (read-only) |
| Persistence | Persistent |

- Code quality with extra focus on editorconfig compliance, import ordering, blank lines, spelling
- Responds to DM from review lead with VERDICT

#### CI-Guard-Runner

| Field | Value |
|-------|-------|
| Name | `ci-guard-runner` |
| Model | sonnet |
| Type | general-purpose |
| Persistence | Persistent |
| Isolation | EnterWorktree per review request |

- Receives DM from review lead -> checkout implementer branch -> run CI guards + build + test
- Responds to DM from review lead with VERDICT (PASSED/FAILED + details)

**Script selection based on changed files**:
- Projection changes -> `projection_*.sh`
- Workflow changes -> `workflow_*.sh`
- Query changes -> `query_projection_priming_guard.sh`
- Test changes -> `test_stability_guards.sh`
- All changes -> `architecture_guards.sh` (always run)

**Review output format** (all reviewers):

```
VERDICT: APPROVED / CHANGES_REQUESTED

Issues:
- [CRITICAL] Description | file:line
- [HIGH] Description | file:line
- [MEDIUM] Description | file:line
- [LOW] Description | file:line

Summary: One-sentence summary
```

**CI-Guard-Runner output format**:

```
VERDICT: PASSED / FAILED

Scripts Executed:
- [PASS] architecture_guards.sh
- [FAIL] projection_state_version_guard.sh -- error summary

Build: PASS/FAIL
Test: PASS/FAIL (X passed, Y failed)
```

### Pipeline Flow

```
Lead
  |
  +- TeamCreate("refactor-team")
  +- Create integration branch: refactor/YYYY-MM-DD_auto-audit
  |
  +- Spawn auditor --> scan --> TaskCreate per issue --> DM Lead "done"
  |
  +- Spawn impl-1, impl-2 (persistent, idle until tasks exist)
  +- Spawn 5 reviewers (persistent, idle until DM'd)
  |
  |  FOR EACH ISSUE (serial):
  |  +--- implementer (worktree) ----------------------------+
  |  | Fix -> build -> test -> commit -> push                |
  |  | DM arch-reviewer-opus: "ready for review"             |
  |  |    +--- arch-reviewer-opus (review lead) -----------+  |
  |  |    | Own review + DM 4 other reviewers              |  |
  |  |    | Collect 5 verdicts                             |  |
  |  |    | Convergence:                                   |  |
  |  |    |   APPROVED -> DM Lead                          |  |
  |  |    |   CHANGES_REQUESTED -> DM implementer          |  |
  |  |    +------------------------------------------------+  |
  |  | Rework if needed (max 3 rounds)                        |
  |  +--------------------------------------------------------+
  |
  +- Lead receives "APPROVED" DMs -> gh pr create per issue
  |
  +- All Tasks completed -> summary report
  +- TeamDelete
```

### DM Protocol

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

### Task States

| Status | Meaning | Set by |
|--------|---------|--------|
| `pending` | Created by auditor, awaiting implementer | Auditor |
| `in_progress` | Claimed by implementer, being fixed/reviewed | Implementer |
| `completed` | PR submitted or marked skipped | Lead |

---

## Convergence Logic

After collecting results from all 5 reviewers, the review lead (or Team Lead in non-pipeline mode) executes the following convergence mechanism:

```
1. Deduplicate issues:
   - Group by file path
   - Within same file, merge issues targeting lines within 5 lines of each other
   - Issues from different reviewers on the same file+region are considered equivalent

2. Apply severity filter:
   for each deduplicated issue:
       if issue.severity in [CRITICAL, HIGH]:
           -> must fix (any single reviewer triggers)
       if issue.severity in [MEDIUM, LOW]:
           count = number of distinct reviewers in the merged group
           if count >= 3:
               -> must fix
           else:
               -> log in PR body but don't block

3. CI-Guard-Runner FAILED:
   -> must fix (treated as CRITICAL)

4. Decision:
   if must-fix issues exist:
       -> compile merged fix list -> send to Implementer
       -> after fix, re-invoke ALL 5 reviewers (full cumulative diff)
       -> max 3 rounds total
   if 3 rounds exhausted with remaining issues:
       -> log as "skipped: exceeded review rounds" -> move to next issue
   if no must-fix issues:
       -> APPROVED -> proceed to PR
```

---

## Implementation Plan

### File Structure

```
.claude/skills/refactor-team/
+-- SKILL.md                    # Main skill: Team Lead orchestration logic
+-- auditor-prompt.md           # Auditor agent prompt template
+-- implementer-prompt.md       # Implementer agent prompt template
+-- arch-reviewer-prompt.md     # Architecture reviewer agent prompt template
+-- quality-reviewer-prompt.md  # Code quality reviewer agent prompt template
+-- ci-guard-runner-prompt.md   # CI guard runner agent prompt template
```

Each file has one clear responsibility:
- `SKILL.md` -- entry point and orchestration (Team Lead)
- `*-prompt.md` -- self-contained agent prompt that can be injected into an Agent tool call

### Task 1: Create Auditor Agent Prompt

**File:** `.claude/skills/refactor-team/auditor-prompt.md`

The auditor scans the codebase against CLAUDE.md rules and CI guard scripts. It reads CLAUDE.md completely, lists all CI guard scripts, and for each major section scans relevant source files for violations using Grep/Glob/Read. Confirmed violations are output as a flat severity-ranked list.

**Severity guidelines**:
- **CRITICAL**: Violates a "prohibited" rule; could cause data loss, corruption, or security issues
- **HIGH**: Violates a "mandatory" rule; breaks architectural invariants
- **MEDIUM**: Violates a naming or style convention; creates technical debt
- **LOW**: Improvement opportunity; not a strict rule violation

**Constraints**: Do NOT suggest fixes or write code. Do NOT report uncertain issues. Output ALL issues found (the Team Lead will select the top N based on the configurable max-issues parameter).

### Task 2: Create Implementer Agent Prompt

**File:** `.claude/skills/refactor-team/implementer-prompt.md`

The implementer receives a single issue description and fixes it in an isolated worktree. The workflow is: read issue -> design minimal fix -> implement -> add/update tests -> build -> test -> run CI guard -> commit -> push.

**Output format**:
```
STATUS: SUCCESS / FAILED
Branch: <branch-name>
Commit: <commit-hash>
Files changed: <list>
Tests added/modified: <list>
Build: PASS/FAIL
Test: PASS/FAIL
CI guard: PASS/FAIL/NOT_RUN
```

**Constraints**: NEVER make changes beyond issue scope. NEVER skip tests. NEVER use `GetAwaiter().GetResult()`. NEVER use `Task.Delay` or `WaitUntilAsync` in tests unless explicitly allowed. If build or test fails after 2 attempts, report FAILED.

### Task 3: Create Architecture Reviewer Agent Prompt

**File:** `.claude/skills/refactor-team/arch-reviewer-prompt.md`

Reviews code changes against the full CLAUDE.md architecture checklist: layering, dependency inversion, actor boundaries, read/write separation, serialization, naming, no process-local state, metadata naming, root cause addressed, minimal change.

**Constraints**: Read-only. Do NOT suggest improvements beyond scope. Distinguish "this is wrong" from "this could be better" -- only flag violations, not preferences.

### Task 4: Create Code Quality Reviewer Agent Prompt

**File:** `.claude/skills/refactor-team/quality-reviewer-prompt.md`

Reviews code changes for over-engineering, security (OWASP Top 10), test coverage, test stability (no prohibited `Task.Delay`/`WaitUntilAsync`/`GetAwaiter().GetResult()`), minimal changes, no backward-compat hacks, style compliance (`.editorconfig`), naming consistency.

**Constraints**: Read-only. Focus on correctness and safety, not personal style preferences.

### Task 5: Create CI Guard Runner Agent Prompt

**File:** `.claude/skills/refactor-team/ci-guard-runner-prompt.md`

Checks out the implementer's branch in a worktree and runs relevant CI guard scripts based on changed files:
- ANY file changed -> ALWAYS run: `bash tools/ci/architecture_guards.sh`
- Projection files -> run all `bash tools/ci/projection_*.sh`
- Workflow files -> run `bash tools/ci/workflow_binding_boundary_guard.sh` and `bash tools/ci/workflow_closed_world_guards.sh`
- Query/ReadModel/Projection port files -> run `bash tools/ci/query_projection_priming_guard.sh`
- Test files -> run `bash tools/ci/test_stability_guards.sh`

Also runs full `dotnet build` and `dotnet test`.

**Constraints**: Read-only (only run scripts and report results). Do NOT skip any selected guard script.

### Task 6: Create the Main Skill File (Team Lead Orchestration)

**File:** `.claude/skills/refactor-team/SKILL.md`

YAML frontmatter:
```yaml
---
name: refactor-team
description: Orchestrate a fully automated multi-agent team to audit, fix, review, and PR architectural issues against CLAUDE.md rules
argument-hint: [max-issues]
allowed-tools: Agent, Read, Grep, Glob, Bash, Edit, Write
---
```

The Team Lead orchestration follows these phases:

**Phase 0: Setup** -- Determine current working branch, create integration branch (`refactor/YYYY-MM-DD_auto-audit`), initialize tracking variables.

**Phase 1: Audit** -- Spawn Auditor agent (Explore, Opus). Parse output into structured issue list. If zero issues found, END.

**Phase 2: Issue Processing Loop** (for each issue up to max_issues):
- **Step 2.1**: Spawn Implementer with `isolation: "worktree"`. On timeout -> skip. On FAILED and round < 3 -> retry with fresh worktree, recovering prior work via `git fetch && git checkout <impl-branch>`. On SUCCESS -> record branch/commit.
- **Step 2.2**: Team Lead collects diff and changed files via `git diff`.
- **Step 2.3**: Spawn 5 reviewers in parallel (2 arch + 2 quality + 1 CI). Diff content included directly in reviewer prompts so Explore agents do not need Bash access.
- **Step 2.4**: Convergence -- collect verdicts, deduplicate, apply severity filter. Must-fix + round < 3 -> re-spawn Implementer with merged fix list + re-invoke all 5 reviewers. Must-fix + round 3 -> skip. No must-fix -> APPROVED.
- **Step 2.5**: Submit PR via `gh pr create` targeting integration branch. PR body includes: issue description, fix summary, review record table, CI guard results, referenced CLAUDE.md rules.
- **Step 2.6**: Next issue.

**Phase 3: Summary Report** -- Output final summary table with issue/severity/status/PR for each processed issue. Include merge instructions.

### Task 7: End-to-End Smoke Test

Verify:
1. Skill directory structure contains all 6 files
2. SKILL.md frontmatter is valid YAML
3. All prompt files reference correct paths (CLAUDE.md, tools/ci/, aevatar.slnx)
4. Dry-run test with `max-issues=1` exercises the full workflow

---

## Branch and PR Flow

### Integration branch

- Created from the current working branch HEAD at the start of each run
- Name: `refactor/YYYY-MM-DD_auto-audit` (e.g., `refactor/2026-03-18_auto-audit`)
- Each implementer worktree is based on the current integration branch HEAD

### Per-issue branch

- Created by the implementer in its worktree
- Name: `<type>/YYYY-MM-DD_<issue-slug>` where `<type>` is selected by Team Lead based on issue category:
  - Architectural violations -> `refactor/`
  - Bugs -> `fix/`
  - Naming/style -> `chore/`
  - Test gaps -> `test/`
- Pushed to remote after implementation

### PR submission

After all reviews pass:

1. `gh pr create` targeting integration branch
2. PR body includes:
   - Original issue description (from audit report)
   - Fix summary
   - Review pass record (final verdict from all 5 reviewers, with model noted)
   - CI guard execution results
   - Referenced CLAUDE.md rules
3. If PR has merge conflicts with integration branch: log as "skipped: merge conflict", no force push

---

## Failure Handling

| Scenario | Action |
|----------|--------|
| Implementer build/test fails | Implementer retries (counts toward 3-round limit) |
| Reviewer timeout | Review lead proceeds with available verdicts (minimum 1 needed) |
| Implementer timeout | Review lead marks task skipped, impl claims next |
| 3 review rounds exhausted | Review lead DMs Lead "skipped"; impl claims next |
| All 5 reviewers fail | Review lead re-requests once; if still all fail, skip |
| PR merge conflict | Lead logs "skipped: merge conflict", no force push |
| Auditor finds zero issues | Output summary report noting "no issues found", run ends gracefully |

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
| Agent timeout | 10 minutes per agent lifetime |
| Resume capability | Not supported; run restarts from scratch (accepted limitation) |
