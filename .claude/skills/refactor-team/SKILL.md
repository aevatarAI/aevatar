---
name: refactor-team
description: Orchestrate a fully automated multi-agent team to audit, fix, review, and PR architectural issues against CLAUDE.md rules. Use when user wants to run automated code audit, refactoring, or architecture compliance checks.
argument-hint: [max-issues]
---

# Automated Refactoring Team — Pipeline Mode

You are the **Team Lead** orchestrating a Claude Code Team that audits the codebase, fixes issues serially, and submits PRs. The review lead coordinates reviewers autonomously via DM.

**Max issues this run:** $ARGUMENTS (default: 5 if not specified)

---

## Your Role as Lead

- Startup: create team, run audit, create tasks
- Per issue: spawn implementer → wait for review lead's verdict → create PR
- Cleanup: shutdown teammates, delete team, output summary

---

## Phase 0: Setup

### 0.1 Create the Team

```
TeamCreate(
  team_name: "refactor-team",
  description: "Automated architecture audit, fix, review, and PR workflow"
)
```

### 0.2 Determine Branches

```bash
CURRENT_BRANCH=$(git branch --show-current)
INTEGRATION_BRANCH="refactor/$(date +%Y-%m-%d)_auto-audit-base"
```

Ensure integration branch exists:

```bash
git checkout -b "$INTEGRATION_BRANCH" 2>/dev/null || git checkout "$INTEGRATION_BRANCH"
```

### 0.3 Initialize Tracking

- `issues_processed = 0`, `issues_succeeded = 0`, `issues_skipped = 0`
- `max_issues` = first argument or 5

---

## Phase 1: Audit

### 1.1 Spawn Auditor

Read `.claude/skills/refactor-team/auditor-prompt.md`, then:

```
Agent(
  subagent_type: "Explore",
  model: "opus",
  name: "auditor",
  team_name: "refactor-team",
  prompt: <auditor-prompt.md contents>
)
```

### 1.2 Process Results

If zero issues → "No issues found.", TeamDelete, END.

Sort by severity. Take top `max_issues`. Create a Task per issue.

---

## Phase 2: Spawn Persistent Reviewers

Read prompt files, then spawn 5 persistent reviewers in parallel:

- `arch-reviewer-opus` (Explore, opus) — review lead, coordinates all reviews
- `arch-reviewer-sonnet` (Explore, sonnet) — naming/namespace/Metadata focus
- `quality-reviewer-opus` (Explore, opus) — code quality/security/tests
- `quality-reviewer-sonnet` (Explore, sonnet) — editorconfig/style focus
- `ci-guard-runner` (general-purpose, sonnet) — runs CI guards + build + test

All reviewers wait for DMs from `arch-reviewer-opus`.

Prompt templates: `.claude/skills/refactor-team/review-lead-prompt.md`, `arch-reviewer-prompt.md`, `quality-reviewer-prompt.md`, `ci-guard-runner-prompt.md`.

---

## Phase 3: Issue Processing Loop

For each issue (serial, one at a time):

### Step 3.1: Spawn Implementer

Read `.claude/skills/refactor-team/implementer-prompt.md`. Spawn implementer as team member (**no worktree**, works directly in repo on a new branch):

```
Agent(
  subagent_type: "general-purpose",
  model: "opus",
  name: "implementer",
  team_name: "refactor-team",
  prompt: <implementer-prompt.md contents>
    + "\n\n## Issue to Fix\n\n" + <issue details>
    + "\n\n## Branch\n\nFrom $INTEGRATION_BRANCH, create: <type>/$(date +%Y-%m-%d)_<issue-slug>"
    + "\n\n## Workflow\n\n1. git checkout -b <branch> $INTEGRATION_BRANCH\n2. Fix, build, test\n3. Commit and push\n4. DM arch-reviewer-opus with branch name and changed files\n5. Wait for review feedback. If CHANGES_REQUESTED, fix and push to same branch, DM again.\n6. If APPROVED, DM team-lead.\n7. git checkout $INTEGRATION_BRANCH when done."
    + "\n\n## Relevant CLAUDE.md Rules\n\n" + <relevant rules>
)
```

### Step 3.2: Wait for Review Lead Decision

The pipeline flows: implementer → review lead → 4 reviewers → convergence → verdict.

**On "APPROVED" DM from review lead:**

Create PR:

```bash
gh pr create \
  --base "$INTEGRATION_BRANCH" \
  --head "<impl-branch>" \
  --title "<type>: <short description>" \
  --body "$(cat <<'PREOF'
## Issue

<original issue description>

## Fix Summary

<implementer's summary>

## Review Record

| Reviewer | Model | Verdict |
|----------|-------|---------|
| arch-reviewer-opus | Opus | ... |
| arch-reviewer-sonnet | Sonnet | ... |
| quality-reviewer-opus | Opus | ... |
| quality-reviewer-sonnet | Sonnet | ... |
| ci-guard-runner | Sonnet | ... |

**Review rounds:** N/3

## Referenced CLAUDE.md Rules

<quoted rules>

🤖 Generated with [Claude Code](https://claude.com/claude-code) Refactoring Team
PREOF
)"
```

TaskUpdate issue to `completed`. Increment `issues_succeeded`.

**On "skipped" DM:** TaskUpdate with skip reason. Increment `issues_skipped`.

### Step 3.3: Next Issue

Increment `issues_processed`. Ensure on `$INTEGRATION_BRANCH`. If more issues → Step 3.1 (spawn new implementer).

---

## Phase 4: Cleanup and Summary

### 4.1 Shutdown all teammates

```
SendMessage(to: "arch-reviewer-opus", message: {type: "shutdown_request"})
SendMessage(to: "arch-reviewer-sonnet", message: {type: "shutdown_request"})
SendMessage(to: "quality-reviewer-opus", message: {type: "shutdown_request"})
SendMessage(to: "quality-reviewer-sonnet", message: {type: "shutdown_request"})
SendMessage(to: "ci-guard-runner", message: {type: "shutdown_request"})
```

### 4.2 Delete Team

```
TeamDelete()
```

### 4.3 Output Summary

```markdown
## Refactoring Team Run Summary

**Date:** YYYY-MM-DD
**Integration branch:** $INTEGRATION_BRANCH
**Team:** refactor-team (1 auditor + 1 impl + 5 reviewers)

### Results

| # | Issue | Severity | Status | PR |
|---|-------|----------|--------|-----|
| 1 | <title> | CRITICAL | ✅ PR #XX | <url> |
| 2 | <title> | HIGH | ❌ Skipped (reason) | — |

**Processed:** X / max_issues
**Succeeded:** Y PRs
**Skipped:** Z
```
