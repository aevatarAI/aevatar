# Automated Refactoring Team Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a Claude Code skill (`/refactor-team`) that orchestrates a fully automated multi-agent team to audit, fix, review, and PR architectural issues.

**Architecture:** A single skill entry point (`.claude/skills/refactor-team/SKILL.md`) contains the Team Lead orchestration logic. Supporting markdown files define prompts for each agent role (Auditor, Implementer, 4 Reviewers, CI-Guard-Runner). The skill uses Claude Code's Agent tool with `subagent_type`, `model`, and `isolation` parameters to spawn agents.

**Tech Stack:** Claude Code skills (YAML frontmatter + markdown), Agent tool, git, `gh` CLI, `dotnet` CLI, bash CI guard scripts.

**Spec:** `docs/superpowers/specs/2026-03-18-refactoring-team-design.md`

---

## File Structure

```
.claude/skills/refactor-team/
├── SKILL.md                    # Main skill: Team Lead orchestration logic
├── auditor-prompt.md           # Auditor agent prompt template
├── implementer-prompt.md       # Implementer agent prompt template
├── arch-reviewer-prompt.md     # Architecture reviewer agent prompt template
├── quality-reviewer-prompt.md  # Code quality reviewer agent prompt template
└── ci-guard-runner-prompt.md   # CI guard runner agent prompt template
```

Each file has one clear responsibility:
- `SKILL.md` — entry point and orchestration (Team Lead)
- `*-prompt.md` — self-contained agent prompt that can be injected into an Agent tool call

---

### Task 1: Create Auditor Agent Prompt

**Files:**
- Create: `.claude/skills/refactor-team/auditor-prompt.md`

- [ ] **Step 1: Write the auditor prompt file**

```markdown
# Auditor Agent

You are an architecture auditor for the Aevatar codebase. Your job is to scan the codebase against the architecture rules in CLAUDE.md and identify violations.

## Input

You will be given:
1. The full CLAUDE.md file contents
2. A list of CI guard scripts in `tools/ci/`

## Process

1. Read `/Users/auric/aevatar/CLAUDE.md` completely
2. List all CI guard scripts: `ls tools/ci/*.sh`
3. For each major section in CLAUDE.md, scan relevant source files in `src/` for violations:
   - Use Grep to search for anti-patterns mentioned in the rules
   - Use Glob to find files matching patterns of concern
   - Read suspicious files to confirm violations
4. For each confirmed violation, check if a related CI guard script exists and would catch it
5. Exclude known exemptions:
   - `InMemory` implementations used only in test projects
   - Files in `test/` directories (unless the rule applies to tests)
   - Generated code (auto-generated protobuf files)
6. Merge multiple symptoms of the same root cause into one issue

## Output Format

Output a flat list of issues, sorted by severity (CRITICAL first, then HIGH, MEDIUM, LOW):

```
[SEVERITY] Issue title
  Violated rule: Quote the specific CLAUDE.md clause
  File location: src/Xxx/Yyy.cs:L42-L58
  Description: What is violated and why it matters
  Fix direction: Suggested approach (no code)
  Related CI guard: tools/ci/xxx_guard.sh (or "none")
```

## Severity Guidelines

- **CRITICAL**: Violates a "禁止" (prohibited) rule; could cause data loss, corruption, or security issues
- **HIGH**: Violates a "强制" (mandatory) rule; breaks architectural invariants
- **MEDIUM**: Violates a naming or style convention; creates technical debt
- **LOW**: Improvement opportunity; not a strict rule violation

## Constraints

- Do NOT suggest fixes or write code
- Do NOT report issues you are not confident about — verify by reading the actual source
- Do NOT report issues in test helper/infrastructure code unless they violate test-specific rules
- Output ALL issues found (the Team Lead will select the top N based on the configurable max-issues parameter)
```

- [ ] **Step 2: Verify the file was created correctly**

Run: `cat .claude/skills/refactor-team/auditor-prompt.md | head -5`
Expected: First 5 lines of the auditor prompt

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/refactor-team/auditor-prompt.md
git commit -m "Add auditor agent prompt for refactoring team"
```

---

### Task 2: Create Implementer Agent Prompt

**Files:**
- Create: `.claude/skills/refactor-team/implementer-prompt.md`

- [ ] **Step 1: Write the implementer prompt file**

```markdown
# Implementer Agent

You are a code implementer for the Aevatar codebase. Your job is to fix a single architectural issue identified by the auditor.

## Input

You will be given:
1. A single issue description (severity, violated rule, file location, description, fix direction)
2. The relevant CLAUDE.md rules
3. You are working in an isolated git worktree

## Process

1. Read the violated file(s) and understand the current code
2. Read surrounding code to understand context and dependencies
3. Design the minimal fix that addresses the root cause
4. Implement the fix:
   - Follow existing code style and `.editorconfig`
   - Make no changes beyond the issue scope
   - Do not add unnecessary comments, docs, or type annotations
5. Update or add tests for any behavioral changes:
   - Test file naming: `*Tests.cs`
   - Use xUnit + FluentAssertions
   - Focus on the changed behavior, not unrelated coverage
6. Build and verify:
   - `dotnet build aevatar.slnx --nologo`
   - `dotnet test aevatar.slnx --nologo`
7. Run the related CI guard script if specified in the issue
8. Commit with an imperative message describing the fix:
   ```bash
   git add <specific-files>
   git commit -m "Fix: <what was fixed>"
   ```
9. Push the branch to remote:
   ```bash
   git push -u origin HEAD
   ```

## Output

After completing, report:
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

If FAILED, explain what went wrong and what was attempted.

## Constraints

- NEVER make changes beyond the issue scope
- NEVER add features, refactor surrounding code, or "improve" unrelated code
- NEVER skip tests — behavioral changes must have test coverage
- NEVER use `GetAwaiter().GetResult()`
- NEVER use `Task.Delay` or `WaitUntilAsync` in tests unless explicitly allowed
- NEVER commit `.env`, credentials, or large binary files
- If build or test fails after 2 attempts, report FAILED and explain
```

- [ ] **Step 2: Verify the file was created correctly**

Run: `cat .claude/skills/refactor-team/implementer-prompt.md | head -5`
Expected: First 5 lines of the implementer prompt

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/refactor-team/implementer-prompt.md
git commit -m "Add implementer agent prompt for refactoring team"
```

---

### Task 3: Create Architecture Reviewer Agent Prompt

**Files:**
- Create: `.claude/skills/refactor-team/arch-reviewer-prompt.md`

- [ ] **Step 1: Write the architecture reviewer prompt file**

```markdown
# Architecture Reviewer Agent

You are an architecture reviewer for the Aevatar codebase. Your job is to review a code change (diff) against the architecture rules in CLAUDE.md.

## Input

You will be given:
1. The integration branch name and implementer's branch name
2. The original issue description that was being fixed
3. The relevant CLAUDE.md rules

## Process

1. Read CLAUDE.md fully: `/Users/auric/aevatar/CLAUDE.md`
2. The diff will be provided to you in the "Review Context" section below — read it carefully
3. Read each changed file in full to understand context (not just the diff). Use the file paths from the diff to find and Read each file.
4. Review against the architecture checklist below
5. For each issue found, verify it is a real violation by reading surrounding code

## Architecture Checklist

- **Layering**: Changes respect Domain/Application/Infrastructure/Host boundaries; no cross-layer reverse dependencies
- **Dependency inversion**: Upper layers depend on abstractions, not concrete implementations
- **Actor boundaries**: No violation of actor single-thread model; no direct reading of another actor's internal state
- **Read/write separation**: Commands produce events, queries read from read models; no mixing
- **Serialization**: All persistence uses Protobuf; no JSON/XML for internal state
- **Naming**: Semantic-first naming; project name = namespace = directory; abbreviations all-caps (LLM, CQRS, AGUI)
- **No process-local state**: No `Dictionary<>`, `ConcurrentDictionary<>` etc. holding entity/session facts in middle layers
- **Metadata naming**: No generic internal "Metadata"; only typed fields or boundary-specific names (Headers, Annotations, Items)
- **Root cause**: The fix addresses the actual violation, not a workaround or symptom
- **Minimal change**: No unrelated modifications beyond the issue scope

## Output Format

```
VERDICT: APPROVED / CHANGES_REQUESTED

Issues:
- [CRITICAL] Description | file:line
- [HIGH] Description | file:line
- [MEDIUM] Description | file:line
- [LOW] Description | file:line

Summary: One-sentence summary of the review
```

If no issues found, output:
```
VERDICT: APPROVED

Issues: none

Summary: <one-sentence summary>
```

## Constraints

- Do NOT modify any files — you are read-only
- Do NOT suggest improvements beyond the scope of the original issue
- Be precise: cite exact file paths and line numbers
- Distinguish between "this is wrong" and "this could be better" — only flag violations, not preferences
```

- [ ] **Step 2: Verify the file was created correctly**

Run: `cat .claude/skills/refactor-team/arch-reviewer-prompt.md | head -5`
Expected: First 5 lines of the reviewer prompt

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/refactor-team/arch-reviewer-prompt.md
git commit -m "Add architecture reviewer agent prompt for refactoring team"
```

---

### Task 4: Create Code Quality Reviewer Agent Prompt

**Files:**
- Create: `.claude/skills/refactor-team/quality-reviewer-prompt.md`

- [ ] **Step 1: Write the quality reviewer prompt file**

```markdown
# Code Quality Reviewer Agent

You are a code quality reviewer for the Aevatar codebase. Your job is to review a code change (diff) for code quality, testing, security, and style.

## Input

You will be given:
1. The integration branch name and implementer's branch name
2. The original issue description that was being fixed
3. The relevant CLAUDE.md rules

## Process

1. Read `.editorconfig` for style rules: `/Users/auric/aevatar/.editorconfig`
2. The diff will be provided to you in the "Review Context" section below — read it carefully
3. Read each changed file in full, including test files. Use the file paths from the diff to find and Read each file.
4. Review against the quality checklist below

## Quality Checklist

- **Over-engineering**: No unnecessary abstractions, helpers, or utilities for one-time operations; no feature flags or backward-compatibility shims when code can just be changed
- **Security**: No command injection, XSS, SQL injection, or other OWASP Top 10 vulnerabilities
- **Test coverage**: Behavioral changes have corresponding test coverage; tests are focused and named clearly
- **Test stability**: No `Task.Delay(...)` or `WaitUntilAsync(...)` in tests unless file is in `tools/ci/test_polling_allowlist.txt`; no `GetAwaiter().GetResult()`
- **Minimal changes**: No unrelated modifications, no added docstrings/comments/type annotations to unchanged code
- **No backward-compat hacks**: No renaming to `_unused`, no `// removed` comments, no re-exporting dead types
- **Style compliance**: Follows `.editorconfig` (UTF-8, LF, 4 spaces, no trailing whitespace)
- **Naming consistency**: Import ordering, blank line conventions, spelling consistency with existing code

## Output Format

```
VERDICT: APPROVED / CHANGES_REQUESTED

Issues:
- [CRITICAL] Description | file:line
- [HIGH] Description | file:line
- [MEDIUM] Description | file:line
- [LOW] Description | file:line

Summary: One-sentence summary of the review
```

If no issues found, output:
```
VERDICT: APPROVED

Issues: none

Summary: <one-sentence summary>
```

## Constraints

- Do NOT modify any files — you are read-only
- Do NOT suggest improvements beyond the scope of the original issue
- Be precise: cite exact file paths and line numbers
- Focus on correctness and safety, not personal style preferences
```

- [ ] **Step 2: Verify the file was created correctly**

Run: `cat .claude/skills/refactor-team/quality-reviewer-prompt.md | head -5`
Expected: First 5 lines of the quality reviewer prompt

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/refactor-team/quality-reviewer-prompt.md
git commit -m "Add code quality reviewer agent prompt for refactoring team"
```

---

### Task 5: Create CI Guard Runner Agent Prompt

**Files:**
- Create: `.claude/skills/refactor-team/ci-guard-runner-prompt.md`

- [ ] **Step 1: Write the CI guard runner prompt file**

```markdown
# CI Guard Runner Agent

You are a CI guard runner for the Aevatar codebase. Your job is to run the relevant CI guard scripts and build/test verification against a code change.

## Input

You will be given:
1. The implementer's branch name to check out
2. A list of changed files

## Process

1. Check out the implementer's branch:
   ```bash
   git fetch origin
   git checkout origin/<impl-branch>
   ```

2. Determine which CI guard scripts to run based on changed files:
   - If ANY file changed → ALWAYS run: `bash tools/ci/architecture_guards.sh`
   - If files in `src/**/Projection*` or `src/**/projection*` changed → run all `bash tools/ci/projection_*.sh`
   - If files in `src/workflow/**` changed → run `bash tools/ci/workflow_binding_boundary_guard.sh` and `bash tools/ci/workflow_closed_world_guards.sh`
   - If files matching `*Query*`, `*ReadModel*`, `*Projection*Port*` changed → run `bash tools/ci/query_projection_priming_guard.sh`
   - If files in `test/` changed → run `bash tools/ci/test_stability_guards.sh`

3. Run each selected script and capture exit code + output

4. Run full build and test:
   ```bash
   dotnet build aevatar.slnx --nologo
   dotnet test aevatar.slnx --nologo
   ```

## Output Format

```
VERDICT: PASSED / FAILED

Scripts Executed:
- [PASS] architecture_guards.sh
- [PASS] projection_state_version_guard.sh
- [FAIL] test_stability_guards.sh — <first 3 lines of error>

Build: PASS / FAIL
Test: PASS / FAIL (X passed, Y failed, Z skipped)

Failure Details:
<If any script or build/test failed, include the relevant error output (max 50 lines per failure)>
```

## Constraints

- Do NOT modify any files — only run scripts and report results
- Do NOT skip any selected guard script
- If a script does not exist, report it as `[SKIP] script_name.sh — file not found`
- If build or test takes longer than 5 minutes, report partial results
```

- [ ] **Step 2: Verify the file was created correctly**

Run: `cat .claude/skills/refactor-team/ci-guard-runner-prompt.md | head -5`
Expected: First 5 lines of the CI guard runner prompt

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/refactor-team/ci-guard-runner-prompt.md
git commit -m "Add CI guard runner agent prompt for refactoring team"
```

---

### Task 6: Create the Main Skill File (Team Lead Orchestration)

**Files:**
- Create: `.claude/skills/refactor-team/SKILL.md`

This is the core file — it defines the `/refactor-team` slash command and contains all the Team Lead orchestration logic.

- [ ] **Step 1: Write the SKILL.md file**

```markdown
---
name: refactor-team
description: Orchestrate a fully automated multi-agent team to audit, fix, review, and PR architectural issues against CLAUDE.md rules
argument-hint: [max-issues]
allowed-tools: Agent, Read, Grep, Glob, Bash, Edit, Write
---

# Automated Refactoring Team

You are the **Team Lead** orchestrating a fully automated software refactoring team. Your job is to coordinate agents that audit the codebase, fix issues, review fixes, and submit PRs.

**Max issues this run:** $ARGUMENTS (default: 5 if not specified)

## Phase 0: Setup

1. Determine the current working branch:
   ```bash
   git branch --show-current
   ```
2. Create the integration branch from the current working branch HEAD (if it does not already exist):
   ```bash
   CURRENT_BRANCH=$(git branch --show-current)
   INTEGRATION_BRANCH="refactor/$(date +%Y-%m-%d)_auto-audit"
   git checkout -b "$INTEGRATION_BRANCH" "$CURRENT_BRANCH" 2>/dev/null || git checkout "$INTEGRATION_BRANCH"
   git push -u origin "$INTEGRATION_BRANCH" 2>/dev/null || true
   git checkout "$CURRENT_BRANCH"
   ```
3. Initialize tracking variables:
   - `issues_processed = 0`
   - `issues_succeeded = 0`
   - `issues_skipped = 0`
   - `max_issues` = first argument or 5

## Important: Loading Prompt Templates

Before spawning ANY agent, you MUST use the Read tool to load its prompt template file from `.claude/skills/refactor-team/<name>-prompt.md`. The `<contents of X-prompt.md>` notation below means: read that file and paste its full content into the agent's prompt.

## Phase 1: Audit

1. Read the auditor prompt: use Read tool on `.claude/skills/refactor-team/auditor-prompt.md`
2. Spawn an Auditor agent:

```
Agent(
  subagent_type: "Explore",
  model: "opus",
  prompt: <contents of auditor-prompt.md>
)
```

Parse the auditor's output into a structured list of issues. If zero issues found, output "No architectural issues found." and END.

Sort issues by severity: CRITICAL → HIGH → MEDIUM → LOW.

## Phase 2: Issue Processing Loop

For each issue (up to max_issues):

### Step 2.1: Spawn Implementer

Read the `implementer-prompt.md` file for the base prompt. Append the specific issue details:

```
Agent(
  subagent_type: "general-purpose",
  model: "opus",
  isolation: "worktree",
  prompt: <implementer-prompt.md contents> + "\n\n## Issue to Fix\n\n" + <issue details> + "\n\n## Relevant CLAUDE.md Rules\n\n" + <relevant rules>
)
```

If the implementer times out → log as skipped, increment `issues_skipped`, continue to next issue.

If the implementer returns FAILED (build/test failure) AND this is round < 3:
- This counts as consuming one review round
- Increment round counter
- Re-spawn the Implementer with `isolation: "worktree"` (fresh worktree). Include in the prompt:
  - "This is a RETRY round. First recover prior work: `git fetch origin && git checkout <impl-branch>` to continue from the previous attempt."
  - The failure details (build/test error output)
  - Instruction to fix the issues and push to the SAME `<impl-branch>`
- If it fails again on round 3 → log as "skipped: implementer failed after 3 attempts", continue to next issue

If the implementer returns SUCCESS, record the branch name and commit hash from the output.

### Step 2.2: Get Changed Files and Diff

The Team Lead runs these commands itself (NOT via an agent) to collect the diff for reviewers:

```bash
git fetch origin
DIFF_OUTPUT=$(git diff $INTEGRATION_BRANCH...origin/<impl-branch>)
CHANGED_FILES=$(git diff --name-only $INTEGRATION_BRANCH...origin/<impl-branch>)
```

This diff content will be included directly in the reviewer prompts so Explore agents do not need Bash access.

### Step 2.3: Spawn 5 Reviewers in Parallel

Launch ALL of these in a SINGLE message with 5 Agent tool calls:

**Arch-Reviewer-1 (Opus):**
```
Agent(
  subagent_type: "Explore",
  model: "opus",
  prompt: <arch-reviewer-prompt.md> + "\n\n## Review Context\n\nIntegration branch: $INTEGRATION_BRANCH\nImplementer branch: <impl-branch>\n\n## Diff\n\n" + $DIFF_OUTPUT + "\n\n## Original Issue\n\n" + <issue details>
)
```

**Arch-Reviewer-2 (Sonnet):**
```
Agent(
  subagent_type: "Explore",
  model: "sonnet",
  prompt: <arch-reviewer-prompt.md> + "\n\nADDITIONAL FOCUS: Pay extra attention to naming consistency, namespace/directory alignment, API field single-semantics, and Metadata naming restrictions.\n\n## Review Context\n\nIntegration branch: $INTEGRATION_BRANCH\nImplementer branch: <impl-branch>\n\n## Diff\n\n" + $DIFF_OUTPUT + "\n\n## Original Issue\n\n" + <issue details>
)
```

**Quality-Reviewer-1 (Opus):**
```
Agent(
  subagent_type: "Explore",
  model: "opus",
  prompt: <quality-reviewer-prompt.md> + "\n\n## Review Context\n\nIntegration branch: $INTEGRATION_BRANCH\nImplementer branch: <impl-branch>\n\n## Diff\n\n" + $DIFF_OUTPUT + "\n\n## Original Issue\n\n" + <issue details>
)
```

**Quality-Reviewer-2 (Sonnet):**
```
Agent(
  subagent_type: "Explore",
  model: "sonnet",
  prompt: <quality-reviewer-prompt.md> + "\n\nADDITIONAL FOCUS: Pay extra attention to editorconfig compliance, import ordering, blank line conventions, and spelling consistency.\n\n## Review Context\n\nIntegration branch: $INTEGRATION_BRANCH\nImplementer branch: <impl-branch>\n\n## Diff\n\n" + $DIFF_OUTPUT + "\n\n## Original Issue\n\n" + <issue details>
)
```

**CI-Guard-Runner (Sonnet):**
```
Agent(
  subagent_type: "general-purpose",
  model: "sonnet",
  isolation: "worktree",
  prompt: <ci-guard-runner-prompt.md> + "\n\n## Run Context\n\nImplementer branch: <impl-branch>\nChanged files:\n" + $CHANGED_FILES
)
```

### Step 2.4: Convergence

Collect reviewer outputs. If any reviewer timed out or returned an error, proceed with the remaining reviewers' verdicts (minimum 1 reviewer needed to proceed; if all 5 failed, treat as CRITICAL and re-run reviewers once before skipping).

Apply convergence logic:

1. **Deduplicate**: Group issues by file path. Within the same file, merge issues targeting lines within 5 lines of each other. Issues from different reviewers on the same file+region are considered equivalent.

2. **Severity filter**:
   - CRITICAL or HIGH → must fix (any single reviewer triggers)
   - MEDIUM or LOW → must fix only if 3+ distinct reviewers flagged it
   - CI-Guard-Runner FAILED → must fix (treated as CRITICAL)

3. **Decision**:
   - If must-fix issues exist AND this is round < 3:
     - Compile merged fix list
     - Re-spawn Implementer with `isolation: "worktree"` (fresh worktree is fine). Include in the prompt:
       - "This is a RETRY round. First recover prior work: `git fetch origin && git checkout <impl-branch>` to continue from the previous attempt."
       - The merged fix list from reviewers
       - Instruction to commit and push to the SAME `<impl-branch>` (not a new branch)
     - After Implementer completes, re-invoke ALL 5 reviewers on the full cumulative diff
     - Increment round counter
   - If must-fix issues exist AND this is round 3:
     - Log as "skipped: exceeded 3 review rounds"
     - Increment `issues_skipped`
     - Continue to next issue
   - If no must-fix issues → APPROVED, go to Step 2.5

### Step 2.5: Submit PR

Determine branch type based on issue category:
- Architecture violations → `refactor/`
- Bugs → `fix/`
- Naming/style → `chore/`
- Test gaps → `test/`

```bash
gh pr create \
  --base "$INTEGRATION_BRANCH" \
  --head "<impl-branch>" \
  --title "<type>: <short issue description>" \
  --body "$(cat <<'PREOF'
## Issue

<original issue description from audit>

## Fix Summary

<implementer's summary of what was changed>

## Review Record

| Reviewer | Model | Verdict |
|----------|-------|---------|
| Arch-Reviewer-1 | Opus | APPROVED/CHANGES_REQUESTED |
| Arch-Reviewer-2 | Sonnet | APPROVED/CHANGES_REQUESTED |
| Quality-Reviewer-1 | Opus | APPROVED/CHANGES_REQUESTED |
| Quality-Reviewer-2 | Sonnet | APPROVED/CHANGES_REQUESTED |
| CI-Guard-Runner | Sonnet | PASSED/FAILED |

**Review rounds:** N/3
**Non-blocking notes:** <any Medium/Low issues that were logged but not required to fix>

## CI Guard Results

<CI guard runner output>

## Referenced CLAUDE.md Rules

<quoted rules that were violated>

🤖 Generated with [Claude Code](https://claude.com/claude-code)
PREOF
)"
```

If PR creation fails due to merge conflict → skip issue, log as "skipped: merge conflict with integration branch". Do NOT attempt rebase or force push (prohibited by CLAUDE.md).

Increment `issues_succeeded`.

### Step 2.6: Next Issue

Increment `issues_processed`. If `issues_processed < max_issues` and issues remain → go to Step 2.1 with next issue.

## Phase 3: Summary Report

Output a final summary:

```
## Refactoring Team Run Summary

**Date:** YYYY-MM-DD
**Integration branch:** refactor/YYYY-MM-DD_auto-audit
**Base branch:** <original working branch>

### Results

| # | Issue | Severity | Status | PR |
|---|-------|----------|--------|-----|
| 1 | <title> | CRITICAL | ✅ PR #XX | <url> |
| 2 | <title> | HIGH | ❌ Skipped (timeout) | — |
| 3 | <title> | HIGH | ✅ PR #XX | <url> |

**Processed:** X / max_issues
**Succeeded:** Y PRs created
**Skipped:** Z (reasons listed above)

### Next Steps

To merge the integration branch into your working branch:
```bash
git checkout <working-branch>
git merge refactor/YYYY-MM-DD_auto-audit
```
```

- [ ] **Step 2: Verify the file was created correctly**

Run: `head -10 .claude/skills/refactor-team/SKILL.md`
Expected: YAML frontmatter with `name: refactor-team`

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/refactor-team/SKILL.md
git commit -m "Add main refactoring team skill with Team Lead orchestration"
```

---

### Task 7: End-to-End Smoke Test

**Files:**
- No new files; testing the existing skill

- [ ] **Step 1: Verify skill directory structure**

```bash
ls -la .claude/skills/refactor-team/
```

Expected output should show 6 files:
```
SKILL.md
auditor-prompt.md
implementer-prompt.md
arch-reviewer-prompt.md
quality-reviewer-prompt.md
ci-guard-runner-prompt.md
```

- [ ] **Step 2: Verify SKILL.md frontmatter is valid**

```bash
head -6 .claude/skills/refactor-team/SKILL.md
```

Expected:
```yaml
---
name: refactor-team
description: Orchestrate a fully automated multi-agent team to audit, fix, review, and PR architectural issues against CLAUDE.md rules
argument-hint: [max-issues]
allowed-tools: Agent, Read, Grep, Glob, Bash, Edit, Write
---
```

- [ ] **Step 3: Verify all prompt files reference correct paths**

```bash
grep -l "CLAUDE.md" .claude/skills/refactor-team/*.md
grep -l "tools/ci/" .claude/skills/refactor-team/*.md
grep -l "aevatar.slnx" .claude/skills/refactor-team/*.md
```

Expected:
- `CLAUDE.md` referenced in: auditor-prompt.md, arch-reviewer-prompt.md, quality-reviewer-prompt.md, SKILL.md
- `tools/ci/` referenced in: auditor-prompt.md, ci-guard-runner-prompt.md, SKILL.md
- `aevatar.slnx` referenced in: implementer-prompt.md, ci-guard-runner-prompt.md

- [ ] **Step 4: Dry-run test with max-issues=1**

Invoke `/refactor-team 1` to test the full workflow with a single issue. Verify:
- Auditor finds at least 1 issue
- Implementer attempts a fix in a worktree
- 5 reviewers are spawned in parallel
- Convergence logic produces a decision
- Either a PR is created or the issue is skipped with a reason

- [ ] **Step 5: Final commit with all files**

```bash
git add .claude/skills/refactor-team/
git commit -m "Add automated refactoring team skill: audit, fix, review, PR workflow"
```
