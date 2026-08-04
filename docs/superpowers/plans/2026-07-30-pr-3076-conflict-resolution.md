# PR 3076 Squash-History Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair the false divergence created by squash-merging PR #2996, keep `apps/**` exactly from `dev`, and update PR #3076 without rewriting the production branch.

**Architecture:** Construct a two-parent merge commit from a conflict-free virtual merge tree whose explicit base is the tree-equivalent PR #2996 head. Override only the user-owned frontend subtree from `dev`, then verify and push with a normal ref update.

**Tech Stack:** Git merge-tree/commit-tree, .NET 10, xUnit, pnpm, Jest, TypeScript, repository CI guards.

## Global Constraints

- `apps/**` must match `origin/dev` exactly.
- The bridge commit parents must be current `origin/feature/integrate` then current `origin/dev`.
- Preserve Domain/Application/Infrastructure/Host layering and typed Protobuf business semantics.
- Do not rebase or force-push `feature/integrate`.
- Do not modify the primary checkout or include its uncommitted files.

---

### Task 1: Prove and construct the bridge tree

**Files:** Git trees only.

- [ ] Prove `86c5688ec^{tree} == 0073f0eb4^{tree}`.
- [ ] Run `git merge-tree --write-tree --merge-base 86c5688ec origin/feature/integrate origin/dev` and require exit 0 with no conflict paths.
- [ ] Create a provisional two-parent commit from that tree.

### Task 2: Apply the authoritative frontend tree

**Files:**
- Replace: `apps/**` from `origin/dev`.
- Add: this design and implementation plan.

- [ ] Restore `apps/**` from `origin/dev` into the bridge tree.
- [ ] Assert `git diff --exit-code origin/dev -- apps`.
- [ ] Write the final tree and create a two-parent merge commit.

### Task 3: Verify the bridge

**Files:** No additional production changes.

- [ ] Verify parents, ancestry, no conflict markers, and no unintended non-frontend drift from the virtual merge tree.
- [ ] Run architecture and test-stability guards.
- [ ] Run full .NET restore/build/test and frontend install/typecheck/test/build.

### Task 4: Deliver safely

**Files:** No local file changes.

- [ ] Fetch and require `origin/feature/integrate` to equal the first parent.
- [ ] Push `HEAD:feature/integrate` without force.
- [ ] Verify PR #3076 is mergeable and document that it must use merge-commit strategy.
