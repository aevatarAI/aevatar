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

## Team Roles

### 1. Auditor

- **Model**: Opus
- **Agent type**: Explore subagent
- **Input**: CLAUDE.md architecture rules + CI guard scripts + codebase
- **Output**: Severity-ranked structured issue list

**Output format**:

```markdown
### [CRITICAL] Issue title
- **Violated rule**: Specific CLAUDE.md clause
- **File location**: src/Xxx/Yyy.cs:L42-L58
- **Description**: What is violated and why it matters
- **Fix direction**: Suggested approach (no code)
- **Related CI guard**: tools/ci/xxx_guard.sh
```

**Strategy**:
- Scan code against CLAUDE.md section by section, skip nothing
- Cross-validate each finding with related CI guard scripts
- Exclude known exemptions (e.g., InMemory implementations for testing)
- Merge multiple symptoms of the same root cause into one issue

### 2. Implementer

- **Model**: Opus
- **Agent type**: Agent with worktree isolation
- **Input**: Single issue description + violated files + relevant CLAUDE.md rules
- **Output**: Commit in worktree (code fix + tests + build passing)

**Workflow**:
1. Read issue description and related source files
2. Design minimal fix
3. Implement fix
4. Add/update tests for behavioral changes
5. `dotnet build aevatar.slnx --nologo` — confirm compilation
6. `dotnet test aevatar.slnx --nologo` — confirm tests pass
7. Run issue-related CI guard scripts
8. Commit (imperative message, single purpose)

**Constraints**:
- No changes beyond issue scope
- No unnecessary comments, docs, or type annotations
- Follow `.editorconfig` and existing code style

### 3. Arch-Reviewer-1 (Opus)

- **Model**: Opus
- **Agent type**: Agent with worktree (read-only review)
- **Perspective**: Deep architectural reasoning

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

```markdown
## Arch Review (Opus)
- **APPROVED** / **CHANGES_REQUESTED**

### Issues
- [CRITICAL] Description | file:line
- [HIGH] Description | file:line
- [MEDIUM] Description | file:line
- [LOW] Description | file:line

### Summary
One-sentence summary
```

### 4. Arch-Reviewer-2 (Sonnet)

- **Model**: Sonnet
- **Agent type**: Agent with worktree (read-only review)
- **Perspective**: Pattern matching and surface-level violation detection

**Review checklist**:
- Same rules as Arch-Reviewer-1
- Extra focus: naming consistency, namespace/directory alignment, API field single-semantics, Metadata naming restrictions

**Output format**: Same as Arch-Reviewer-1

### 5. Quality-Reviewer-1 (Opus)

- **Model**: Opus
- **Agent type**: Agent with worktree (read-only review)
- **Perspective**: Deep code quality analysis

**Review checklist**:
- Over-engineering (unnecessary abstractions, config, error handling)
- Security vulnerabilities (OWASP Top 10)
- Test coverage for behavioral changes
- No prohibited `Task.Delay`/`WaitUntilAsync` in tests
- No `GetAwaiter().GetResult()` patterns
- No unnecessary backward-compatibility hacks
- Changes are minimal (no unrelated modifications)

**Output format**:

```markdown
## Quality Review (Opus)
- **APPROVED** / **CHANGES_REQUESTED**

### Issues
- [CRITICAL] Description | file:line
- [HIGH] Description | file:line
- [MEDIUM] Description | file:line
- [LOW] Description | file:line

### Summary
One-sentence summary
```

### 6. Quality-Reviewer-2 (Sonnet)

- **Model**: Sonnet
- **Agent type**: Agent with worktree (read-only review)
- **Perspective**: Style consistency and mechanical checks

**Review checklist**:
- Same rules as Quality-Reviewer-1
- Extra focus: editorconfig compliance, import ordering, blank line conventions, spelling consistency

**Output format**: Same as Quality-Reviewer-1

### 7. CI-Guard-Runner

- **Model**: Sonnet
- **Agent type**: Agent with worktree
- **Input**: Worktree branch + changed file list

**Workflow**:
1. Select relevant CI guard scripts based on changed files:
   - Projection changes → `projection_*.sh`
   - Workflow changes → `workflow_*.sh`
   - Query changes → `query_projection_priming_guard.sh`
   - Test changes → `test_stability_guards.sh`
   - All changes → `architecture_guards.sh` (always run)
2. Execute selected guard scripts
3. `dotnet build` + `dotnet test` full verification

**Output format**:

```markdown
## CI Guard Report
- **PASSED** / **FAILED**

### Scripts Executed
- [PASS] architecture_guards.sh
- [FAIL] projection_state_version_guard.sh — error summary

### Build & Test
- Build: PASS/FAIL
- Test: PASS/FAIL (X passed, Y failed)
```

## Convergence Mechanism

After collecting results from all 5 reviewers:

```
for each issue across all reviews:
    if issue.severity in [CRITICAL, HIGH]:
        → must fix (any single reviewer triggers)
    if issue.severity in [MEDIUM, LOW]:
        count = number of reviewers raising same/equivalent issue
        if count >= 3 (majority):
            → must fix
        else:
            → log but don't block

    deduplicate: same file + same line + same description → merge

if must-fix issues exist:
    → send back to Implementer with merged fix list
    → re-enter Review after fix (max 3 rounds)
if still issues after 3 rounds:
    → pause, escalate to user
else:
    → APPROVED, proceed to PR
```

## PR Submission Flow

After all reviews pass:

1. Create formal branch from worktree: `fix/2026-03-18_<issue-slug>`
2. Push to remote
3. `gh pr create` targeting integration branch `refactor/2026-03-18_auto-audit`
4. PR body includes:
   - Original issue description (from audit report)
   - Fix summary
   - Review pass record (final verdict from all 5 reviewers)
   - CI guard execution results
   - Referenced CLAUDE.md rules

## Single Run Lifecycle

```
Start
  │
  ├─ 1. Create integration branch refactor/2026-03-18_auto-audit (if not exists)
  │
  ├─ 2. Auditor scans → outputs issue list (severity-ranked)
  │
  ├─ 3. Pick issue #1
  │     │
  │     ├─ 4. Implementer fixes in worktree
  │     │
  │     ├─ 5. 5 Reviewers review in parallel
  │     │
  │     ├─ 6. Converge → issues? → back to 4 (max 3 rounds)
  │     │
  │     ├─ 7. Pass → submit PR to integration branch
  │     │
  │     └─ 8. Pick next issue → back to 4
  │
  ├─ Processed N issues (default 5) or list exhausted
  │
  └─ Output run summary report
```

## Safety Boundaries

| Constraint | Value |
|------------|-------|
| Max review rounds per issue | 3 |
| Max issues per run | 5 (configurable) |
| PR target | Integration branch, never master |
| Work isolation | Each Implementer uses independent worktree |
| Destructive operations | No force push / reset --hard / branch delete |
| Timeout | 10 minutes per subagent |
