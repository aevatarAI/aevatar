# Workflow Create Result Unknown Implementation Plan

**Goal:** Report an interrupted draft-create response honestly without
resubmitting or guessing Workflow identity.

**Scope:** Frontend-only Workflow Activity vNext creation flow.

**Tech stack:** React, TypeScript, TanStack React Query, ConsoleToast, Jest,
Testing Library.

## Task 1: Protect The Product Contract

Add a focused `NewWorkflowPage` test that rejects `createWorkflowDraft` with a
`504`. Assert the warning copy, retained YAML, one create call, drafts-query
refetch, and absence of navigation. Run only this test and confirm the expected
failure before implementation.

## Task 2: Classify Only The Create Stage

Add a narrow create-result classifier for `408`, `504`, `TypeError`, and
aborted transport errors. Catch these errors directly around
`createWorkflowDraft`, emit an internal result-unconfirmed signal, and leave
the outer catch responsible for all definite failures.

The result-unconfirmed path must not call `finishSave`, retry create, inspect
drafts to identify a Workflow, or navigate.

## Task 3: Refresh And Warn

On the result-unconfirmed signal, refresh the existing scoped drafts query and
show the localized warning. Preserve current form state. Treat refresh failure
as non-authoritative for the create outcome and keep the warning visible.

## Task 4: Focused Verification And Delivery

Run the owning test, analyzer-selected related tests, changed-file Biome
checks, test stability guard, docs lint, and `git diff --check`. Reuse the
existing Chrome tab for a non-destructive UI check, review the diff, stage only
task files, commit, push, and update Draft PR #3498 with exact commands and
results. Full frontend validation remains owned by GitHub CI.
