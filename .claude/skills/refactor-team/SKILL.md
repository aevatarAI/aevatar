---
name: refactor-team
description: Orchestrate a fully automated multi-agent refactoring workflow to audit, fix, review, and PR architectural issues against CLAUDE.md rules. Use when user wants to run automated code audit, refactoring, or architecture compliance checks.
argument-hint: [max-issues-per-cycle]
---

# Automated Refactoring Team — Continuous Agent Subprocess Mode

You are the **Team Lead** orchestrating a continuous multi-agent refactoring workflow. Each agent is spawned, completes its task, and is destroyed. You directly control every step.

Each invocation runs **one cycle**: spawn auditor (which handles sync + scan) → process issues → return.

**Max issues per cycle:** $ARGUMENTS (default: 5 if not specified)

---

## Phase 0: Setup

### 0.1 Determine Branches

```bash
CURRENT_BRANCH=$(git branch --show-current)
INTEGRATION_BRANCH="refactor/$(date +%Y-%m-%d)_auto-audit-base"
```

Ensure on integration branch:

```bash
git checkout "$INTEGRATION_BRANCH" 2>/dev/null || git checkout -b "$INTEGRATION_BRANCH"
```

### 0.2 Initialize Tracking

- `max_issues` = first argument or 5

---

## Phase 1: Audit (includes sync)

Read `.claude/skills/refactor-team/auditor-prompt.md`, then spawn auditor.

The auditor handles **everything**: git fetch, change detection, PR status check, pull latest, and full architecture scan. The Lead does NOT do any git sync or change detection.

```
Agent(
  subagent_type: "Explore",
  model: "opus",
  prompt: <auditor-prompt.md contents>
    + any additional exclusions from memory/context
)
```

The auditor will also write an audit report to `docs/audit-scorecard/YYYY-MM-DD-architecture-audit.md`.

If zero issues → output: "Audit clean — no new issues found." and **return**.

Sort by severity: CRITICAL > HIGH > MEDIUM > LOW. Take top `max_issues`.

---

## Phase 2: Issue Processing Loop

For each issue (serial):

### Step 2.1: Spawn Implementer

Read `.claude/skills/refactor-team/implementer-prompt.md`. Determine branch type:
- Architecture → `refactor/`
- Bugs → `fix/`
- Naming/style → `chore/`
- Test gaps → `test/`

```
Agent(
  subagent_type: "general-purpose",
  model: "opus",
  prompt: <implementer-prompt.md contents>
    + "\n\n## Issue to Fix\n\n" + <issue details>
    + "\n\n## Branch\n\ngit checkout -b <type>/$(date +%Y-%m-%d)_<issue-slug> $INTEGRATION_BRANCH"
    + "\n\n## After Fix\n\nCommit, push, switch back to $INTEGRATION_BRANCH. Return your report."
    + "\n\n## Relevant CLAUDE.md Rules\n\n" + <relevant rules>
)
```

If FAILED and round < 3 → re-spawn with failure context. Round 3 → skip.

Record branch name and changed files.

### Step 2.2: Get Diff

```bash
git fetch origin
DIFF_OUTPUT=$(git diff $INTEGRATION_BRANCH...origin/<impl-branch>)
CHANGED_FILES=$(git diff --name-only $INTEGRATION_BRANCH...origin/<impl-branch>)
```

### Step 2.3: Spawn 5 Reviewers in Parallel

Read prompt files:
- `.claude/skills/refactor-team/arch-reviewer-prompt.md`
- `.claude/skills/refactor-team/quality-reviewer-prompt.md`
- `.claude/skills/refactor-team/ci-guard-runner-prompt.md`

Launch ALL 5 in a SINGLE message:

```
Agent(subagent_type: "Explore", model: "opus",
  prompt: <arch-reviewer-prompt.md> + diff + issue)

Agent(subagent_type: "Explore", model: "sonnet",
  prompt: <arch-reviewer-prompt.md> + "ADDITIONAL FOCUS: naming, namespace, Metadata" + diff + issue)

Agent(subagent_type: "Explore", model: "opus",
  prompt: <quality-reviewer-prompt.md> + diff + issue)

Agent(subagent_type: "Explore", model: "sonnet",
  prompt: <quality-reviewer-prompt.md> + "ADDITIONAL FOCUS: editorconfig, style" + diff + issue)

Agent(subagent_type: "general-purpose", model: "sonnet",
  prompt: <ci-guard-runner-prompt.md> + "git fetch origin && git checkout origin/<impl-branch>" + changed files)
```

### Step 2.4: Convergence

Collect all 5 outputs.

**Deduplicate:** Group by file, merge issues within 5 lines.

**Severity filter:**
- CRITICAL/HIGH → must fix (any single reviewer)
- MEDIUM/LOW → must fix only if 3+ reviewers flagged
- CI-Guard-Runner FAILED → must fix

**Decision:**
- Must-fix AND round < 3 → re-spawn implementer with fix list, then re-review
- Must-fix AND round >= 3 → skip issue
- No must-fix → APPROVED → submit PR

### Step 2.5: Submit PR

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
| arch-reviewer | Opus | ... |
| arch-reviewer | Sonnet | ... |
| quality-reviewer | Opus | ... |
| quality-reviewer | Sonnet | ... |
| ci-guard-runner | Sonnet | ... |

**Review rounds:** N/3
**Non-blocking notes:** <Medium/Low not required to fix>

## Referenced CLAUDE.md Rules

<quoted rules>

🤖 Generated with [Claude Code](https://claude.com/claude-code) Refactoring Team
PREOF
)"
```

### Step 2.6: Next Issue

Ensure on `$INTEGRATION_BRANCH`. Increment `issues_processed`. Reset `round = 0`. Continue if issues remain.

---

## Phase 3: Cycle Summary

```markdown
## Cycle Summary

**Issues processed:** X
**Succeeded:** Y PRs
**Skipped:** Z
```

Then update the audit report at `docs/audit-scorecard/YYYY-MM-DD-architecture-audit.md`:
- Update the "Issues Found" section with resolved/remaining status
- Add a "Processing Results" section with PR links and review outcomes

Then **return** — let the external loop trigger the next invocation.

---

## IMPORTANT: No Internal Polling

**NEVER implement internal polling, retry loops, or wait-for-change loops.** Each invocation:

1. Spawn auditor (auditor handles sync + scan)
2. Process issues (if any)
3. Output summary
4. **Return**

The Lead does NOT run git fetch, git pull, gh pr list, or any sync commands. All of that is the auditor's job.
