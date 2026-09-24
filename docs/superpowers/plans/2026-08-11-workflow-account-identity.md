# Workflow Activity Account Identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every Workflow Activity vNext route render one subject-safe account identity in the global header and Settings account surface.

**Architecture:** A shared React Query hook owns `/api/auth/me` for the vNext surface. A pure resolver merges missing presentation fields from the stored NyxID session only when backend and stored subjects match, and exposes the same resolved data to the shell and Settings.

**Tech Stack:** React 19, TypeScript, TanStack React Query, Jest, Testing Library

---

### Task 1: Specify identity resolution

**Files:**
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/account/resolveWorkflowActivityAccount.test.ts`
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/account/resolveWorkflowActivityAccount.ts`
- Modify: `apps/aevatar-console-web/src/shared/studio/models.ts`

- [x] **Step 1: Write failing tests for matching subject fallback, subject mismatch, and authoritative sign-out**
- [x] **Step 2: Run the new Jest file and confirm it fails because the resolver does not exist**
- [x] **Step 3: Add the top-level auth subject type and implement the minimal pure resolver**
- [x] **Step 4: Run the new Jest file and confirm all resolver cases pass**

### Task 2: Make the vNext shell the identity consumer

**Files:**
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/account/useWorkflowActivityAccount.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/WorkflowActivityVNextShell.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/settings/SettingsPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/settings/accountIdentity.ts`

- [x] **Step 1: Add a failing Settings integration test that reproduces the missing avatar with a matching stored session**
- [x] **Step 2: Run the focused integration test and confirm the header still renders `Account`**
- [x] **Step 3: Add the shared account hook, remove the route-level shell prop, and feed Settings the resolved auth session**
- [x] **Step 4: Run the integration and resolver tests and confirm they pass**

### Task 3: Focused validation and delivery

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: pull request description for the follow-up identity-fix PR

- [x] **Step 1: Run the frontend scope analyzer against `origin/feat/2026-08-04_workflow-activity-vnext`**
- [x] **Step 2: Run dependency-related Jest tests and explicit changed test files**
- [x] **Step 3: Run Biome only on analyzer `staticCheckFiles`; skip local full typecheck/build**
- [x] **Step 4: Inspect the complete diff and browser-smoke Settings and Workflows in the user's Chrome**
- [x] **Step 5: Stage only task files, commit, push, and create the follow-up PR with exact validation evidence**
