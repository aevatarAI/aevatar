# NyxID Service Access Review Return Route Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve the initiating Account settings URL through the complete NyxID service-access-review flow so Workflow Activity vNext returns to its scoped Account route instead of legacy Settings.

**Architecture:** Product surfaces construct their own canonical URLs, while `NyxIDAuthClient` only sanitizes, persists, and restores caller-owned return targets. A discriminated option union makes the review target mandatory, and the callback page sanitizes error navigation before back or retry actions.

**Tech Stack:** React 19, TypeScript 5.6, Umi, Jest 29, Testing Library, Biome, pnpm

---

## File Map

- `apps/aevatar-console-web/src/shared/auth/client.ts`: typed redirect contract and pending OAuth return-target round trip.
- `apps/aevatar-console-web/src/shared/auth/client.test.ts`: review persistence, success, denial, failure, and unsafe-target regressions.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/navigation.ts`: canonical vNext Settings URL builder.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/navigation.test.ts`: route-builder contract.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/settings/SettingsPage.tsx`: scoped route owner for Settings tabs and Account review.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/settings/AccountPanel.tsx`: semantically named Account route consumer.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/settings/AccountPanel.test.tsx`: explicit review option contract.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`: integrated vNext Account initiation assertion.
- `apps/aevatar-console-web/src/pages/settings/accountContent.tsx`: legacy Settings owns its local review return URL.
- `apps/aevatar-console-web/src/pages/settings/index.test.tsx`: legacy return behavior remains explicit.
- `apps/aevatar-console-web/src/pages/auth/callback/index.tsx`: sanitized callback error target and type-safe retry branches.
- `apps/aevatar-console-web/src/pages/auth/callback/index.test.tsx`: scoped success, failure, retry, and unsafe fallback coverage.

### Task 1: Make The Auth Contract Preserve Caller-Owned Review Routes

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/auth/client.test.ts`
- Modify: `apps/aevatar-console-web/src/shared/auth/client.ts`

- [ ] **Step 1: Change the review tests to use a distinct scoped route**

Define one explicit test value and replace every review constant assertion:

```ts
const reviewReturnTo =
  '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account';
```

For review initiation, assert that pending state keeps the supplied value:

```ts
await new NyxIDAuthClient(runtimeConfig).loginWithRedirect({
  flow: 'serviceAccessReview',
  returnTo: reviewReturnTo,
});

expect(pending).toEqual(
  expect.objectContaining({
    flow: 'serviceAccessReview',
    returnTo: reviewReturnTo,
    state: authorizeUrl.searchParams.get('state'),
  }),
);
```

Use the same distinct value in success, backend failure, and OAuth denial
fixtures and assertions. Add a tampered pending-state case whose `returnTo` is
`//evil.example/account`; assert the callback error contains
`CONSOLE_HOME_ROUTE`.

- [ ] **Step 2: Run the auth regression test and confirm the old override fails**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest src/shared/auth/client.test.ts --runInBand
```

Expected: FAIL because review pending state or callback results contain
`/settings?section=account` instead of the scoped route.

- [ ] **Step 3: Replace the permissive option interface with a discriminated union**

Implement the exported option type:

```ts
export type LoginRedirectOptions =
  | {
      readonly flow?: 'signIn';
      readonly returnTo?: string;
      readonly prompt?: 'none' | 'consent' | 'login';
    }
  | {
      readonly flow: 'serviceAccessReview';
      readonly returnTo: string;
      readonly prompt?: never;
    };
```

Delete `SERVICE_ACCESS_REVIEW_RETURN_TO`. Make return resolution independent of
flow while keeping its signature suitable for both pending and current options:

```ts
function resolveReturnTo(returnTo?: string | null): string {
  return sanitizeReturnTo(returnTo);
}
```

Use `resolveReturnTo(options.returnTo)` before persisting and
`resolveReturnTo(storedPending?.pending.returnTo)` when restoring. Keep:

```ts
const prompt = flow === 'serviceAccessReview' ? 'consent' : options.prompt;
```

- [ ] **Step 4: Run the auth test and confirm the round trip passes**

Run the Step 2 command again.

Expected: PASS, including custom scoped return values and unsafe pending-state
fallback.

### Task 2: Give Each Settings Surface Ownership Of Its Route

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/navigation.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/navigation.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/settings/AccountPanel.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/settings/AccountPanel.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/settings/SettingsPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/settings/accountContent.tsx`
- Modify: `apps/aevatar-console-web/src/pages/settings/index.test.tsx`

- [ ] **Step 1: Add failing canonical Settings route tests**

Add these assertions to `navigation.test.ts`:

```ts
expect(buildWorkflowActivitySettingsHref('scope with space', 'ai')).toBe(
  '/scopes/scope%20with%20space/workflow-activity-vnext/settings',
);
expect(buildWorkflowActivitySettingsHref('scope-alpha', 'account')).toBe(
  '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account',
);
expect(buildWorkflowActivitySettingsHref('scope-alpha', 'advanced')).toBe(
  '/scopes/scope-alpha/workflow-activity-vnext/settings?section=advanced',
);
```

Rename every Account panel test prop to `accountSettingsHref`. Add a successful
review test that clicks `Manage service access` and asserts:

```ts
expect(mockLoginWithRedirect).toHaveBeenCalledWith({
  flow: 'serviceAccessReview',
  returnTo:
    '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account',
});
```

- [ ] **Step 2: Run the route and panel tests and confirm the new API fails**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest src/pages/workflow-activity-vnext/navigation.test.ts src/pages/workflow-activity-vnext/settings/AccountPanel.test.tsx --runInBand
```

Expected: FAIL because the builder and renamed prop do not exist.

- [ ] **Step 3: Implement the canonical builder and vNext ownership**

In `navigation.ts`, add:

```ts
export type WorkflowActivitySettingsSection = 'ai' | 'account' | 'advanced';

export function buildWorkflowActivitySettingsHref(
  scopeId: string,
  section: WorkflowActivitySettingsSection,
): string {
  const base = buildWorkflowActivitySectionHref(scopeId, 'settings');
  return section === 'ai' ? base : `${base}?section=${section}`;
}
```

In `SettingsPage.tsx`, import the type and builder, remove the pathname-based
`settingsSectionHref`, and build every Settings tab href with `scopeId`. Pass:

```tsx
<AccountPanel
  accountSettingsHref={buildWorkflowActivitySettingsHref(scopeId, 'account')}
  identity={accountIdentity}
/>
```

In `AccountPanel.tsx`, rename the prop and use `accountSettingsHref` for both
the sign-in recovery URL and review `returnTo`.

- [ ] **Step 4: Move the legacy URL into legacy Settings**

Remove the deleted shared constant import and define:

```ts
const LEGACY_ACCOUNT_SETTINGS_HREF = '/settings?section=account';
```

Pass that local constant to the legacy review call. Keep the legacy integration
assertion expecting `/settings?section=account` and update no vNext expectation
to use the legacy path.

- [ ] **Step 5: Run the affected Settings tests**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest src/pages/workflow-activity-vnext/navigation.test.ts src/pages/workflow-activity-vnext/settings/AccountPanel.test.tsx src/pages/workflow-activity-vnext/index.test.tsx src/pages/settings/index.test.tsx --runInBand
```

Expected: PASS with vNext producing the scoped Account href and legacy Settings
producing only its local legacy href.

### Task 3: Make Callback Error Navigation Safe And Route-Preserving

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/auth/callback/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/auth/callback/index.tsx`

- [ ] **Step 1: Change callback tests to scoped review routes and add unsafe input**

Remove the deleted route constant from the client mock. Change review success,
denial, localized failure, and retry fixtures to:

```ts
const reviewReturnTo =
  '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account';
```

Assert both retry and the back link retain `reviewReturnTo`. Add a structured
review error with `returnTo: '//evil.example/account'` and assert the back link
uses `CONSOLE_HOME_ROUTE` and retry passes that same safe target.

- [ ] **Step 2: Run the callback test and confirm unsafe or legacy behavior fails**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest src/pages/auth/callback/index.test.tsx --runInBand
```

Expected: FAIL because the implementation still imports the deleted constant,
falls back to legacy Settings, or accepts a protocol-relative error target.

- [ ] **Step 3: Sanitize callback error targets and split retry variants**

Import `sanitizeReturnTo`, remove the shared route constant, and resolve error
navigation with:

```ts
const fallbackReturnTo =
  flow === 'serviceAccessReview' ? CONSOLE_HOME_ROUTE : '/login';
const returnTo =
  typeof record?.returnTo === 'string'
    ? sanitizeReturnTo(record.returnTo)
    : fallbackReturnTo;
```

Make retry options type-correct by branching:

```ts
if (callbackError.flow === 'serviceAccessReview') {
  await client.loginWithRedirect({
    flow: 'serviceAccessReview',
    returnTo: callbackError.returnTo,
  });
} else {
  await client.loginWithRedirect({
    flow: 'signIn',
    returnTo: callbackError.returnTo,
    ...(callbackError.reason === 'requiredServiceAccessMissing'
      ? { prompt: 'consent' as const }
      : {}),
  });
}
```

- [ ] **Step 4: Run the callback test and confirm safe round trips pass**

Run the Step 2 command again.

Expected: PASS for scoped success, denial, failure, retry, back navigation, and
unsafe fallback.

### Task 4: Focused Verification And Delivery

**Files:**
- Verify all files listed in the File Map.

- [ ] **Step 1: Run the frontend change-scope analyzer**

Run:

```bash
python3 /Users/abigaildeng/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

Expected: the report lists the changed frontend files, explicit Jest files, and
changed-file static checks. Do not run a full frontend test, typecheck, lint, or
build locally.

- [ ] **Step 2: Run every directly changed Jest file together**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest src/shared/auth/client.test.ts src/pages/auth/callback/index.test.tsx src/pages/settings/index.test.tsx src/pages/workflow-activity-vnext/navigation.test.ts src/pages/workflow-activity-vnext/settings/AccountPanel.test.tsx src/pages/workflow-activity-vnext/index.test.tsx --runInBand
```

Expected: all suites PASS.

- [ ] **Step 3: Run dependency-related Jest coverage**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --findRelatedTests src/shared/auth/client.ts src/pages/auth/callback/index.tsx src/pages/settings/accountContent.tsx src/pages/workflow-activity-vnext/navigation.ts src/pages/workflow-activity-vnext/settings/AccountPanel.tsx src/pages/workflow-activity-vnext/settings/SettingsPage.tsx --runInBand
```

Expected: every discovered related suite PASS.

- [ ] **Step 4: Run changed-file static checks**

Run Biome only with source and test paths reported by the scope analyzer:

```bash
pnpm --dir apps/aevatar-console-web exec biome check src/shared/auth/client.ts src/shared/auth/client.test.ts src/pages/auth/callback/index.tsx src/pages/auth/callback/index.test.tsx src/pages/settings/accountContent.tsx src/pages/settings/index.test.tsx src/pages/workflow-activity-vnext/navigation.ts src/pages/workflow-activity-vnext/navigation.test.ts src/pages/workflow-activity-vnext/settings/AccountPanel.tsx src/pages/workflow-activity-vnext/settings/AccountPanel.test.tsx src/pages/workflow-activity-vnext/settings/SettingsPage.tsx src/pages/workflow-activity-vnext/index.test.tsx
```

Expected: the changed-file Biome check exits 0.

- [ ] **Step 5: Run the test stability guard**

Run:

```bash
bash tools/ci/test_stability_guards.sh
```

Expected: the guard exits 0. Full-project TypeScript verification and the full
frontend suite/build are delegated to GitHub CI.

- [ ] **Step 6: Review and commit only task files**

Review `git diff --check`, the complete diff against the target base, and
`git status --short`. Stage only the design, plan, source, and test files from
this task, then commit:

```bash
git commit -m "Fix NyxID review return routing"
```

Expected: the worktree is clean and the branch contains the design commit plus
one focused implementation commit.

- [ ] **Step 7: Push and create the pull request**

Push `fix/2026-09-03_nyxid-review-return-route` and create a ready PR targeting
`feat/2026-08-04_workflow-activity-vnext`. The PR body records the problem,
route-ownership solution, affected paths, exact focused commands and results,
and that GitHub CI owns the full frontend typecheck, suite, and build.
