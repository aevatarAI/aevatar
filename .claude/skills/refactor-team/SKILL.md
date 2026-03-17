---
name: refactor-team
description: Orchestrate a fully automated multi-agent team to audit, fix, review, and PR architectural issues against CLAUDE.md rules. Use when user wants to run automated code audit, refactoring, or architecture compliance checks.
argument-hint: [max-issues]
---

# Automated Refactoring Team

You are the **Team Lead** orchestrating a fully automated software refactoring team. Your job is to coordinate a Claude Code Team of agents that audit the codebase, fix issues, review fixes, and submit PRs.

**Max issues this run:** $ARGUMENTS (default: 5 if not specified)

---

## Phase 0: Setup

### 0.1 Create the Team

Use TeamCreate to establish the refactoring team:

```
TeamCreate(
  team_name: "refactor-team",
  description: "Automated architecture audit, fix, review, and PR workflow"
)
```

### 0.2 Determine Branches

```bash
CURRENT_BRANCH=$(git branch --show-current)
INTEGRATION_BRANCH="refactor/$(date +%Y-%m-%d)_auto-audit"
```

Create the integration branch from the current working branch HEAD (if it does not already exist):

```bash
git checkout -b "$INTEGRATION_BRANCH" "$CURRENT_BRANCH" 2>/dev/null || git checkout "$INTEGRATION_BRANCH"
git push -u origin "$INTEGRATION_BRANCH" 2>/dev/null || true
git checkout "$CURRENT_BRANCH"
```

### 0.3 Initialize Tracking

- `issues_processed = 0`
- `issues_succeeded = 0`
- `issues_skipped = 0`
- `max_issues` = first argument or 5
- `round = 0` (per issue, reset for each new issue)

---

## Phase 1: Audit

### 1.1 Load Auditor Prompt

Use the Read tool to load `.claude/skills/refactor-team/auditor-prompt.md`.

### 1.2 Spawn Auditor as Team Member

```
Agent(
  subagent_type: "Explore",
  model: "opus",
  name: "auditor",
  team_name: "refactor-team",
  prompt: <auditor-prompt.md contents>
)
```

### 1.3 Process Auditor Results

Parse the auditor's output into a structured list of issues. If zero issues found, output "No architectural issues found.", clean up team with TeamDelete, and END.

Sort issues by severity: CRITICAL → HIGH → MEDIUM → LOW. Take only the top `max_issues`.

Create a TaskCreate for each issue to track in the shared task list:
```
TaskCreate(
  subject: "[SEVERITY] Issue title",
  description: <full issue details>
)
```

---

## Phase 2: Issue Processing Loop

For each issue (up to max_issues):

### Step 2.1: Assign and Spawn Implementer

1. Use TaskUpdate to mark the current issue task as `in_progress`
2. Read `.claude/skills/refactor-team/implementer-prompt.md`
3. Determine branch type based on issue category:
   - Architecture violations → `refactor/`
   - Bugs → `fix/`
   - Naming/style → `chore/`
   - Test gaps → `test/`
4. Spawn Implementer as team member:

```
Agent(
  subagent_type: "general-purpose",
  model: "opus",
  name: "implementer",
  team_name: "refactor-team",
  isolation: "worktree",
  prompt: <implementer-prompt.md contents>
    + "\n\n## Issue to Fix\n\n" + <issue details>
    + "\n\n## Branch Name\n\nCreate and work on branch: <type>/<date>_<issue-slug>"
    + "\n\n## Relevant CLAUDE.md Rules\n\n" + <relevant rules>
)
```

5. **If implementer returns FAILED** (build/test failure) AND round < 3:
   - Increment `round`
   - Re-spawn implementer with `isolation: "worktree"`, adding to prompt:
     - "This is a RETRY round. First recover prior work: `git fetch origin && git checkout <impl-branch>`"
     - The failure details
     - "Fix the issues and push to the SAME branch `<impl-branch>`"
   - If fails on round 3 → TaskUpdate to completed with note "skipped: implementer failed after 3 attempts", increment `issues_skipped`, continue to next issue

6. **If implementer times out** → skip issue, log reason, continue

7. Record the implementer's branch name and commit hash.

### Step 2.2: Get Diff for Reviewers

The Team Lead runs these commands itself:

```bash
git fetch origin
DIFF_OUTPUT=$(git diff $INTEGRATION_BRANCH...origin/<impl-branch>)
CHANGED_FILES=$(git diff --name-only $INTEGRATION_BRANCH...origin/<impl-branch>)
```

### Step 2.3: Spawn 5 Reviewers in Parallel as Team Members

Read ALL prompt templates first:
- `.claude/skills/refactor-team/arch-reviewer-prompt.md`
- `.claude/skills/refactor-team/quality-reviewer-prompt.md`
- `.claude/skills/refactor-team/ci-guard-runner-prompt.md`

Then launch ALL 5 in a SINGLE message with 5 parallel Agent tool calls:

**Arch-Reviewer-1 (Opus):**
```
Agent(
  subagent_type: "Explore",
  model: "opus",
  name: "arch-reviewer-opus",
  team_name: "refactor-team",
  prompt: <arch-reviewer-prompt.md>
    + "\n\n## Review Context\n\nIntegration branch: $INTEGRATION_BRANCH\nImplementer branch: <impl-branch>\n\n## Diff\n\n```\n" + $DIFF_OUTPUT + "\n```\n\n## Original Issue\n\n" + <issue details>
)
```

**Arch-Reviewer-2 (Sonnet):**
```
Agent(
  subagent_type: "Explore",
  model: "sonnet",
  name: "arch-reviewer-sonnet",
  team_name: "refactor-team",
  prompt: <arch-reviewer-prompt.md>
    + "\n\nADDITIONAL FOCUS: Pay extra attention to naming consistency, namespace/directory alignment, API field single-semantics, and Metadata naming restrictions."
    + "\n\n## Review Context\n\n..." + <same diff and issue context>
)
```

**Quality-Reviewer-1 (Opus):**
```
Agent(
  subagent_type: "Explore",
  model: "opus",
  name: "quality-reviewer-opus",
  team_name: "refactor-team",
  prompt: <quality-reviewer-prompt.md>
    + "\n\n## Review Context\n\n..." + <same diff and issue context>
)
```

**Quality-Reviewer-2 (Sonnet):**
```
Agent(
  subagent_type: "Explore",
  model: "sonnet",
  name: "quality-reviewer-sonnet",
  team_name: "refactor-team",
  prompt: <quality-reviewer-prompt.md>
    + "\n\nADDITIONAL FOCUS: Pay extra attention to editorconfig compliance, import ordering, blank line conventions, and spelling consistency."
    + "\n\n## Review Context\n\n..." + <same diff and issue context>
)
```

**CI-Guard-Runner (Sonnet):**
```
Agent(
  subagent_type: "general-purpose",
  model: "sonnet",
  name: "ci-guard-runner",
  team_name: "refactor-team",
  isolation: "worktree",
  prompt: <ci-guard-runner-prompt.md>
    + "\n\n## Run Context\n\nImplementer branch: <impl-branch>\nChanged files:\n" + $CHANGED_FILES
)
```

### Step 2.4: Convergence

Collect all reviewer outputs. If any reviewer timed out or returned an error, proceed with the remaining reviewers' verdicts (minimum 1 needed; if all 5 failed, treat as CRITICAL and re-run once before skipping).

**Deduplicate:**
- Group issues by file path
- Within same file, merge issues targeting lines within 5 lines of each other
- Issues from different reviewers on the same file+region are considered equivalent

**Severity filter:**
- CRITICAL or HIGH → must fix (any single reviewer triggers)
- MEDIUM or LOW → must fix only if 3+ distinct reviewers flagged it
- CI-Guard-Runner FAILED → must fix (treated as CRITICAL)

**Decision:**
- If must-fix issues exist AND round < 3:
  - Compile merged fix list
  - Increment `round`
  - Re-spawn Implementer with `isolation: "worktree"`, including:
    - "RETRY round. First: `git fetch origin && git checkout <impl-branch>`"
    - The merged fix list from reviewers
    - "Push to the SAME branch `<impl-branch>`"
  - After Implementer completes, re-collect diff and re-invoke ALL 5 reviewers
- If must-fix issues exist AND round >= 3:
  - TaskUpdate issue to completed with "skipped: exceeded 3 review rounds"
  - Increment `issues_skipped`, continue to next issue
- If no must-fix issues → APPROVED, proceed to Step 2.5

### Step 2.5: Submit PR

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
| arch-reviewer-opus | Opus | APPROVED/CHANGES_REQUESTED |
| arch-reviewer-sonnet | Sonnet | APPROVED/CHANGES_REQUESTED |
| quality-reviewer-opus | Opus | APPROVED/CHANGES_REQUESTED |
| quality-reviewer-sonnet | Sonnet | APPROVED/CHANGES_REQUESTED |
| ci-guard-runner | Sonnet | PASSED/FAILED |

**Review rounds:** N/3
**Non-blocking notes:** <any Medium/Low issues logged but not required to fix>

## CI Guard Results

<CI guard runner output>

## Referenced CLAUDE.md Rules

<quoted rules that were violated>

🤖 Generated with [Claude Code](https://claude.com/claude-code) Refactoring Team
PREOF
)"
```

If PR creation fails due to merge conflict → skip issue, log as "skipped: merge conflict". Do NOT attempt rebase or force push.

TaskUpdate issue to completed. Increment `issues_succeeded`.

### Step 2.6: Next Issue

Increment `issues_processed`. Reset `round = 0`. If `issues_processed < max_issues` and issues remain → go to Step 2.1 with next issue.

---

## Phase 3: Cleanup and Summary

### 3.1 Shutdown Teammates

Send shutdown request to all active teammates:

```
SendMessage(to: "auditor", message: {type: "shutdown_request"})
SendMessage(to: "implementer", message: {type: "shutdown_request"})
SendMessage(to: "arch-reviewer-opus", message: {type: "shutdown_request"})
SendMessage(to: "arch-reviewer-sonnet", message: {type: "shutdown_request"})
SendMessage(to: "quality-reviewer-opus", message: {type: "shutdown_request"})
SendMessage(to: "quality-reviewer-sonnet", message: {type: "shutdown_request"})
SendMessage(to: "ci-guard-runner", message: {type: "shutdown_request"})
```

### 3.2 Delete Team

```
TeamDelete()
```

### 3.3 Output Summary Report

```markdown
## Refactoring Team Run Summary

**Date:** YYYY-MM-DD
**Integration branch:** refactor/YYYY-MM-DD_auto-audit
**Base branch:** <original working branch>
**Team:** refactor-team (7 agents)

### Results

| # | Issue | Severity | Status | PR |
|---|-------|----------|--------|-----|
| 1 | <title> | CRITICAL | ✅ PR #XX | <url> |
| 2 | <title> | HIGH | ❌ Skipped (reason) | — |
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
