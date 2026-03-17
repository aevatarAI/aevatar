# Automated Software Refactoring Team Design

## Overview

A fully automated multi-agent refactoring team that autonomously discovers architectural issues in the codebase, fixes them, and submits PRs with multi-reviewer approval. The user only intervenes at the integration branch merge stage.

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Autonomy level | Fully automated | User intervenes only at integration branch merge |
| Issue discovery | Self-audit against CLAUDE.md + CI guards | No dependency on pre-existing audit docs |
| Review panel | 5 reviewers (2 arch + 2 quality + 1 CI) | Ensemble review counters LLM attention drift |
| Model mix | Opus + Sonnet per review category | Cross-model diversity catches more issues |
| Convergence | Severity-based (Critical/High: any-one; Medium/Low: majority) | Balances thoroughness with efficiency |
| PR scope | Single issue per PR | Small PRs are easier to review and revert |
| PR target | Integration branch, not master | Safety buffer before mainline |
| Integration base | Current working branch (not master) | Refactoring builds on in-progress work |
| Issues per run | 5 (configurable) | Balances cost per run with meaningful progress per session |
| Sequential processing | One issue at a time | Avoids merge conflicts between concurrent fixes; simpler orchestration |

## Team Roles

### 0. Team Lead (Coordinator)

- **Model**: Opus
- **Agent type**: The main conversation session (not a spawned agent)
- **Responsibility**: Owns the entire lifecycle described in "Single Run Lifecycle"

**Duties**:
1. Create integration branch from the current working branch HEAD at run start
2. Spawn Auditor and collect issue list
3. For each issue: spawn Implementer, wait for completion, then spawn 5 Reviewers in parallel
4. Collect all review verdicts, execute convergence logic (dedup + severity-based filtering)
5. If fixes needed: send merged fix list back to Implementer, re-spawn Reviewers (max 3 rounds)
6. If approved: create branch, push, submit PR via `gh pr create`
7. Track progress via TaskCreate/TaskUpdate
8. On timeout or 3-round exhaustion: log issue as skipped, move to next
9. After all issues processed: output run summary report

### 1. Auditor

- **Model**: Opus
- **subagent_type**: `Explore` (read-only tools, no file editing)
- **Input**: CLAUDE.md architecture rules + CI guard scripts + codebase
- **Output**: Severity-ranked structured issue list

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

### 2. Implementer

- **Model**: Opus
- **subagent_type**: `general-purpose` (needs Bash, Edit, Write for code changes)
- **Isolation**: `isolation: "worktree"` parameter on Agent tool — Claude Code creates an isolated git worktree automatically
- **Input**: Single issue description + violated files + relevant CLAUDE.md rules
- **Output**: Commit in worktree branch (code fix + tests + build passing)

**Workflow**:
1. Read issue description and related source files
2. Design minimal fix
3. Implement fix
4. Add/update tests for behavioral changes
5. `dotnet build aevatar.slnx --nologo` — confirm compilation
6. `dotnet test aevatar.slnx --nologo` — confirm tests pass
7. Run issue-related CI guard scripts
8. Commit (imperative message, single purpose)
9. Push worktree branch to remote (so reviewers can access it)

**Constraints**:
- No changes beyond issue scope
- No unnecessary comments, docs, or type annotations
- Follow `.editorconfig` and existing code style

### 3. Arch-Reviewer-1 (Opus)

- **Model**: Opus (`model: "opus"` on Agent tool)
- **subagent_type**: `Explore` (read-only, cannot modify files)
- **Perspective**: Deep architectural reasoning
- **Input**: Implementer's branch name + CLAUDE.md full text + original issue description
- **Branch access**: Run `git fetch origin` first, then `git diff <integration-branch>...origin/<impl-branch>` to read the diff

**Review checklist**:
- Layering correctness (Domain/Application/Infrastructure/Host)
- Dependency inversion direction
- Actor boundary integrity
- Read/write separation maintained
- Serialization uniformity (Protobuf)
- Naming follows semantic-first principle
- No new in-process state mappings introduced
- Fix addresses root cause, not workaround

**Output format**:

```
VERDICT: APPROVED / CHANGES_REQUESTED

Issues:
- [CRITICAL] Description | file:line
- [HIGH] Description | file:line
- [MEDIUM] Description | file:line
- [LOW] Description | file:line

Summary: One-sentence summary
```

### 4. Arch-Reviewer-2 (Sonnet)

- **Model**: Sonnet (`model: "sonnet"` on Agent tool)
- **subagent_type**: `Explore` (read-only)
- **Perspective**: Pattern matching and surface-level violation detection
- **Input**: Same as Arch-Reviewer-1

**Review checklist**:
- Same rules as Arch-Reviewer-1
- Extra focus: naming consistency, namespace/directory alignment, API field single-semantics, Metadata naming restrictions

**Output format**: Same as Arch-Reviewer-1

### 5. Quality-Reviewer-1 (Opus)

- **Model**: Opus (`model: "opus"` on Agent tool)
- **subagent_type**: `Explore` (read-only)
- **Perspective**: Deep code quality analysis
- **Input**: Implementer's branch name + test files + .editorconfig + original issue description
- **Branch access**: Run `git fetch origin` first, then `git diff <integration-branch>...origin/<impl-branch>` to read the diff

**Review checklist**:
- Over-engineering (unnecessary abstractions, config, error handling)
- Security vulnerabilities (OWASP Top 10)
- Test coverage for behavioral changes
- No prohibited `Task.Delay`/`WaitUntilAsync` in tests
- No `GetAwaiter().GetResult()` patterns
- No unnecessary backward-compatibility hacks
- Changes are minimal (no unrelated modifications)

**Output format**: Same as Arch-Reviewer-1

### 6. Quality-Reviewer-2 (Sonnet)

- **Model**: Sonnet (`model: "sonnet"` on Agent tool)
- **subagent_type**: `Explore` (read-only)
- **Perspective**: Style consistency and mechanical checks
- **Input**: Same as Quality-Reviewer-1

**Review checklist**:
- Same rules as Quality-Reviewer-1
- Extra focus: editorconfig compliance, import ordering, blank line conventions, spelling consistency

**Output format**: Same as Quality-Reviewer-1

### 7. CI-Guard-Runner

- **Model**: Sonnet (`model: "sonnet"` on Agent tool)
- **subagent_type**: `general-purpose` (needs Bash to run scripts and tests)
- **Isolation**: `isolation: "worktree"` parameter on Agent tool — Claude Code creates an isolated git worktree; agent checks out implementer's branch
- **Input**: Implementer's branch name + changed file list (provided by Team Lead via `git diff --name-only <integration-branch>...<impl-branch>`)

**Workflow**:
1. Check out implementer's branch in worktree
2. Select relevant CI guard scripts based on changed files:
   - Projection changes → `projection_*.sh`
   - Workflow changes → `workflow_*.sh`
   - Query changes → `query_projection_priming_guard.sh`
   - Test changes → `test_stability_guards.sh`
   - All changes → `architecture_guards.sh` (always run)
3. Execute selected guard scripts
4. `dotnet build aevatar.slnx --nologo` + `dotnet test aevatar.slnx --nologo`

**Output format**:

```
VERDICT: PASSED / FAILED

Scripts Executed:
- [PASS] architecture_guards.sh
- [FAIL] projection_state_version_guard.sh — error summary

Build: PASS/FAIL
Test: PASS/FAIL (X passed, Y failed)
```

## Convergence Mechanism

After collecting results from all 5 reviewers, the Team Lead executes:

```
1. Deduplicate issues:
   - Group by file path
   - Within same file, merge issues targeting lines within 5 lines of each other
   - Issues from different reviewers on the same file+region are considered equivalent

2. Apply severity filter:
   for each deduplicated issue:
       if issue.severity in [CRITICAL, HIGH]:
           → must fix (any single reviewer triggers)
       if issue.severity in [MEDIUM, LOW]:
           count = number of distinct reviewers in the merged group
           if count >= 3:
               → must fix
           else:
               → log in PR body but don't block

3. CI-Guard-Runner FAILED:
   → must fix (treated as CRITICAL)

4. Decision:
   if must-fix issues exist:
       → compile merged fix list → send to Implementer
       → after fix, re-invoke ALL 5 reviewers (full cumulative diff)
       → max 3 rounds total
   if 3 rounds exhausted with remaining issues:
       → log as "skipped: exceeded review rounds" → move to next issue
   if no must-fix issues:
       → APPROVED → proceed to PR
```

## Branch and PR Flow

### Integration branch

- Created from the current working branch HEAD at the start of each run
- Name: `refactor/YYYY-MM-DD_auto-audit` (e.g., `refactor/2026-03-18_auto-audit`)
- Each implementer worktree is based on the current integration branch HEAD

### Per-issue branch

- Created by the implementer in its worktree
- Name: `<type>/YYYY-MM-DD_<issue-slug>` where `<type>` is selected by Team Lead based on issue category:
  - Architectural violations → `refactor/`
  - Bugs → `fix/`
  - Naming/style → `chore/`
  - Test gaps → `test/`
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
3. If PR has merge conflicts with integration branch: Team Lead rebases the branch; if rebase fails, skip issue with a note

## Single Run Lifecycle

```
Start
  │
  ├─ 1. Create integration branch from current working branch HEAD (if not exists)
  │
  ├─ 2. Auditor (Explore agent) scans → outputs issue list (severity-ranked)
  │     - If zero issues found → output "no issues found" summary → END
  │
  ├─ 3. Pick highest-priority issue
  │     │
  │     ├─ 4. Implementer (worktree agent) fixes + commits + pushes branch
  │     │
  │     ├─ 5. Team Lead gets changed file list via git diff --name-only
  │     │
  │     ├─ 6. 5 Reviewers launched in parallel:
  │     │     - 4 Explore agents read branch diff + files
  │     │     - 1 general-purpose agent runs CI guards in worktree
  │     │
  │     ├─ 7. Team Lead collects verdicts → convergence logic
  │     │     - Must-fix issues? → merged fix list → back to 4 (max 3 rounds)
  │     │     - 3 rounds exhausted? → skip issue, log reason
  │     │
  │     ├─ 8. APPROVED → submit PR to integration branch
  │     │
  │     └─ 9. Pick next issue → back to 4
  │
  ├─ Processed N issues (default 5) or list exhausted
  │
  └─ Output run summary report
```

## Failure Handling

| Scenario | Action |
|----------|--------|
| Implementer build fails | Implementer retries fix (counts as a review round) |
| Implementer test fails | Implementer retries fix (counts as a review round) |
| Reviewer timeout (10 min) | Team Lead proceeds with remaining reviewers' verdicts |
| Implementer timeout (10 min) | Skip issue, log as "skipped: implementer timeout" |
| 3 review rounds exhausted | Skip issue, log as "skipped: exceeded review rounds" |
| PR merge conflict | Team Lead attempts rebase; if fails, skip issue |
| All issues skipped | Run summary notes zero successful PRs |
| Auditor finds zero issues | Output summary report noting "no issues found", run ends gracefully |

## Safety Boundaries

| Constraint | Value |
|------------|-------|
| Max review rounds per issue | 3 |
| Max issues per run | 5 (configurable) |
| PR target | Integration branch, never master |
| Work isolation | Implementer + CI-Guard-Runner use worktree; Reviewers are read-only Explore agents |
| Destructive operations | No force push / reset --hard / branch delete |
| Agent timeout | 10 minutes per agent lifetime |
| Resume capability | Not supported; run restarts from scratch (accepted limitation) |
