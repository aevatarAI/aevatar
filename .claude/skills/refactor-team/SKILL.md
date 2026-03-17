---
name: refactor-team
description: Orchestrate a fully automated multi-agent team to audit, fix, review, and PR architectural issues against CLAUDE.md rules. Use when user wants to run automated code audit, refactoring, or architecture compliance checks.
argument-hint: [max-issues]
---

# Automated Refactoring Team — Pipeline Mode

You are the **Team Lead** orchestrating a Claude Code Team that audits the codebase, fixes issues serially, and submits PRs. The team uses pipeline-style collaboration where the implementer works in an isolated worktree and the review lead coordinates all reviewers autonomously.

**Max issues this run:** $ARGUMENTS (default: 5 if not specified)

---

## Your Role as Lead

You handle **startup, PR submission, and summary only**. The review cycle is autonomous:
- Implementer fixes one issue at a time in a worktree, then DMs the review lead
- Review lead coordinates all reviewers and makes pass/fail decisions
- Review lead DMs you only when an issue is APPROVED or skipped
- After each issue completes, you spawn a new implementer (with worktree) for the next issue

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
INTEGRATION_BRANCH="refactor/$(date +%Y-%m-%d)_auto-audit"
```

Create the integration branch from the current working branch HEAD (if not exists):

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

### 1.2 Process Auditor Results

Parse the auditor's output into a structured list of issues. If zero issues found, output "No architectural issues found.", TeamDelete, and END.

Sort by severity: CRITICAL > HIGH > MEDIUM > LOW. Take only the top `max_issues`.

Create a Task for each issue:
```
TaskCreate(
  subject: "[SEVERITY] Issue title",
  description: <full issue details including violated rule, file location, fix direction>
)
```

---

## Phase 2: Spawn Persistent Reviewers

Read ALL reviewer prompt files first:
- `.claude/skills/refactor-team/review-lead-prompt.md`
- `.claude/skills/refactor-team/arch-reviewer-prompt.md`
- `.claude/skills/refactor-team/quality-reviewer-prompt.md`
- `.claude/skills/refactor-team/ci-guard-runner-prompt.md`

Then spawn ALL 5 persistent reviewers in parallel:

**Review Lead (arch-reviewer-opus):**
```
Agent(
  subagent_type: "Explore",
  model: "opus",
  name: "arch-reviewer-opus",
  team_name: "refactor-team",
  prompt: <review-lead-prompt.md contents>
    + "\n\n## Team Context\n\nIntegration branch: $INTEGRATION_BRANCH\nYou are the review lead. Wait for DMs from the implementer."
)
```

**Arch-Reviewer-Sonnet:**
```
Agent(
  subagent_type: "Explore",
  model: "sonnet",
  name: "arch-reviewer-sonnet",
  team_name: "refactor-team",
  prompt: <arch-reviewer-prompt.md contents>
    + "\n\nADDITIONAL FOCUS: naming consistency, namespace/directory alignment, API field single-semantics, Metadata naming restrictions."
    + "\n\n## Team Context\n\nIntegration branch: $INTEGRATION_BRANCH\nWait for review requests from `arch-reviewer-opus`. Reply with your VERDICT."
)
```

**Quality-Reviewer-Opus:**
```
Agent(
  subagent_type: "Explore",
  model: "opus",
  name: "quality-reviewer-opus",
  team_name: "refactor-team",
  prompt: <quality-reviewer-prompt.md contents>
    + "\n\n## Team Context\n\nIntegration branch: $INTEGRATION_BRANCH\nWait for review requests from `arch-reviewer-opus`. Reply with your VERDICT."
)
```

**Quality-Reviewer-Sonnet:**
```
Agent(
  subagent_type: "Explore",
  model: "sonnet",
  name: "quality-reviewer-sonnet",
  team_name: "refactor-team",
  prompt: <quality-reviewer-prompt.md contents>
    + "\n\nADDITIONAL FOCUS: editorconfig compliance, import ordering, blank line conventions, spelling consistency."
    + "\n\n## Team Context\n\nIntegration branch: $INTEGRATION_BRANCH\nWait for review requests from `arch-reviewer-opus`. Reply with your VERDICT."
)
```

**CI-Guard-Runner:**
```
Agent(
  subagent_type: "general-purpose",
  model: "sonnet",
  name: "ci-guard-runner",
  team_name: "refactor-team",
  isolation: "worktree",
  prompt: <ci-guard-runner-prompt.md contents>
    + "\n\n## Team Context\n\nIntegration branch: $INTEGRATION_BRANCH\nWait for review requests from `arch-reviewer-opus`. Check out the implementer branch, run guards, reply with your VERDICT."
)
```

---

## Phase 3: Issue Processing Loop

For each issue (up to max_issues), process serially:

### Step 3.1: Spawn Implementer with Worktree

Read `.claude/skills/refactor-team/implementer-prompt.md`, then spawn a **new implementer per issue** with worktree isolation:

```
Agent(
  subagent_type: "general-purpose",
  model: "opus",
  name: "implementer",
  team_name: "refactor-team",
  isolation: "worktree",
  prompt: <implementer-prompt.md contents>
    + "\n\n## Task to Fix\n\n" + <issue details from task>
    + "\n\n## Team Context\n\nIntegration branch: $INTEGRATION_BRANCH\nYou are the implementer. Fix this single issue, commit, push, then DM `arch-reviewer-opus` with the branch name and changed files."
    + "\n\n## Relevant CLAUDE.md Rules\n\n" + <relevant rules>
)
```

### Step 3.2: Wait for Review Lead Decision

The implementer DMs `arch-reviewer-opus`. The review lead coordinates all 5 reviewers autonomously and DMs you back with either:

**On "APPROVED" DM from review lead:**

1. Extract branch name and review record
2. Create PR:

```bash
gh pr create \
  --base "$INTEGRATION_BRANCH" \
  --head "<impl-branch>" \
  --title "<type>: <short issue description>" \
  --body "$(cat <<'PREOF'
## Issue

<original issue description from audit>

## Fix Summary

<from implementer's report>

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

## Referenced CLAUDE.md Rules

<quoted rules that were violated>

🤖 Generated with [Claude Code](https://claude.com/claude-code) Refactoring Team
PREOF
)"
```

3. TaskUpdate the issue to `completed`
4. Increment `issues_succeeded`

**On "skipped" DM from review lead:**

1. TaskUpdate the issue to `completed` with skip reason
2. Increment `issues_skipped`

**On "CHANGES_REQUESTED" (review lead handles rework internally):**

The review lead DMs the implementer directly for rework (max 3 rounds). You only receive the final APPROVED or skipped DM.

### Step 3.3: Next Issue

Increment `issues_processed`. If `issues_processed < max_issues` and issues remain → go to Step 3.1 with next issue (spawn a new implementer with worktree).

---

## Phase 4: Cleanup and Summary

### 4.1 Shutdown Teammates

Send shutdown request to all active teammates:

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

### 4.3 Output Summary Report

```markdown
## Refactoring Team Run Summary

**Date:** YYYY-MM-DD
**Integration branch:** refactor/YYYY-MM-DD_auto-audit
**Base branch:** <original working branch>
**Team:** refactor-team (7 agents: 1 auditor + 1 impl per issue + 5 reviewers)
**Mode:** Pipeline (serial impl + autonomous review)

### Results

| # | Issue | Severity | Status | PR |
|---|-------|----------|--------|-----|
| 1 | <title> | CRITICAL | ✅ PR #XX | <url> |
| 2 | <title> | HIGH | ❌ Skipped (reason) | — |

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
