# Team Automation Canonical Query Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Team Automations surface resolve a canonical member owner and query that member's projected schedules through the owner-aware `/api/schedules` read API.

**Architecture:** Keep `TeamAutomationView` as the page-facing model, but decode it from the canonical `ScheduledDispatchSummary` wire shape at the `teamAutomationApi` boundary. Reuse one exported schedule-owner query encoder so every read carries the exact `scopeId + teamId + memberId` tuple. Treat the Team-level Automations route as a selector shell that canonicalizes an explicit or sole eligible member before enabling the query.

**Tech Stack:** React 18, TypeScript, TanStack Query, Ant Design, Jest, Testing Library, Umi history integration, pnpm.

---

## File Map

- Modify `apps/aevatar-console-web/src/shared/api/scheduledDispatchApi.ts`: export the existing typed owner-query encoder without changing its semantics.
- Modify `apps/aevatar-console-web/src/shared/api/teamAutomationApi.ts`: send list/detail reads to `/api/schedules`, decode the schedule current-state fields, and validate the exact owner tuple.
- Modify `apps/aevatar-console-web/src/shared/api/teamAutomationApi.test.ts`: replace nested-read expectations with canonical owner-aware read and mapping tests.
- Modify `apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.tsx`: resolve canonical member authority, navigate selector shells, and present a chooser for unresolved multiple-member Teams.
- Modify `apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.test.tsx`: cover sole, explicitly selected, ambiguous, and invalid member resolution.
- Modify `apps/aevatar-console-web/src/pages/teams/detail.test.tsx`: prove the real Team route canonicalizes and triggers exactly one owner query while query hints cannot override a path member.
- Keep `src/Aevatar.Studio.Hosting/Endpoints/StudioMemberAutomationEndpoints.cs` unchanged: nested Studio HTTP remains preflight-only.
- Keep `src/platform/Aevatar.GAgentService.Hosting/Endpoints/Schedules/ScheduledDispatchEndpoints.cs` unchanged: the existing canonical schedule endpoint already supplies the required read contract.

### Task 1: Lock The Canonical Schedule Read Contract In Failing API Tests

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/api/teamAutomationApi.test.ts`
- Test: `apps/aevatar-console-web/src/shared/api/teamAutomationApi.test.ts`

- [ ] **Step 1: Replace the legacy automation view fixture with the real schedule current-state shape**

Use a fixture whose identity fields cannot be confused with workflow or service identities:

```ts
function scheduleSummary(overrides?: Record<string, unknown>) {
  return {
    scheduleId: "sch-alpha",
    displayName: "Daily review",
    targetKind: "ServiceInvocation",
    targetActorId: "actor-alpha",
    payloadTypeUrl: "type.googleapis.com/aevatar.ChatRequestEvent",
    serviceKey: "scope-alpha:default:default:svc-alpha",
    serviceId: "svc-alpha",
    serviceEndpointId: "chat",
    prompt: "Summarize open work.",
    cronExpression: "0 9 * * 1-5",
    timezone: "Asia/Singapore",
    enabled: true,
    createdAt: "2026-07-15T00:00:00Z",
    updatedAt: "2026-07-16T00:00:00Z",
    nextFireAt: "2026-07-17T01:00:00Z",
    lastFireAt: null,
    lastTargetActorId: "",
    lastCommandId: "",
    lastCorrelationId: "",
    lastError: "",
    fireCount: 0,
    failureCount: 0,
    headers: {},
    scheduleActorId: "schedule-actor-alpha",
    scheduleKind: "Workflow",
    deleted: false,
    teamOwned: true,
    teamOwnerScopeId: "scope-alpha",
    teamOwnerMemberId: "m-alpha",
    teamId: "team-alpha",
    credentialSourceKind: "ScheduledInvocationAgentKey",
    teamAutomationLifecycleStatus: "Active",
    credentialExpiresAt: "2026-10-14T00:00:00Z",
    teamAutomationOperationId: "op-alpha",
    credentialGeneration: 1,
    revocationPending: false,
    lastAuthorizationErrorCode: "",
    stateVersion: 4,
    ownerLLMRouteKind: "nyx_id_user_service",
    ownerLLMRoute: "us-alpha",
    ownerLLMUserServiceId: "us-alpha",
    ownerLLMServiceSlug: "connector-alpha",
    ownerLLMModel: "gpt-5",
    nyxIdRevocationStatus: "NotRequired",
    vaultRevocationStatus: "NotRequired",
    ...overrides,
  };
}
```

- [ ] **Step 2: Change list and pagination assertions to require the canonical owner query**

The list test must expect:

```ts
expect(fetchMock.mock.calls[0][0]).toBe(
  "/api/schedules?ownerKind=studio_member_automation&ownerScopeId=scope-alpha&ownerTeamId=team-alpha&ownerMemberId=m-alpha&includeTotalCount=true&take=200",
);
```

The pagination test must prove the same four owner fields are present on every page and that only the cursor changes on page two.

- [ ] **Step 3: Add canonical detail and fail-closed owner tests**

Add a detail response shaped as `{ schedule: scheduleSummary(), recentFires: [] }` and assert:

```ts
await expect(teamAutomationApi.get(draft, "sch/alpha")).resolves.toEqual(
  expect.objectContaining({
    memberId: "m-alpha",
    publishedServiceId: "svc-alpha",
    scheduleId: "sch-alpha",
  }),
);
expect(fetchMock.mock.calls[0][0]).toBe(
  "/api/schedules/sch%2Falpha?ownerKind=studio_member_automation&ownerScopeId=scope-alpha&ownerTeamId=team-alpha&ownerMemberId=m-alpha",
);
```

Add separate rejected cases for `teamOwnerScopeId`, `teamId`, and `teamOwnerMemberId` mismatches, and one rejected case with `teamOwned: false`.

- [ ] **Step 4: Run the API suite and verify the intended red state**

Run:

```bash
pnpm --dir apps/aevatar-console-web test --runInBand -- src/shared/api/teamAutomationApi.test.ts
```

Expected: FAIL because list/detail still request nested Studio routes and the decoder still requires the removed nested response field names.

### Task 2: Implement The Canonical Team Automation Read Adapter

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/api/scheduledDispatchApi.ts`
- Modify: `apps/aevatar-console-web/src/shared/api/teamAutomationApi.ts`
- Test: `apps/aevatar-console-web/src/shared/api/teamAutomationApi.test.ts`
- Test: `apps/aevatar-console-web/src/shared/api/scheduledDispatchApi.test.ts`

- [ ] **Step 1: Export the typed owner query encoder and keep the generic adapter on it**

Rename and export the existing helper:

```ts
export function encodeScheduledDispatchOwnerQuery(
  owner: ScheduledDispatchOwner | undefined,
) {
  const normalizedOwner = encodeOwner(owner);
  return normalizedOwner
    ? {
        ownerKind: normalizedOwner.kind,
        ownerScopeId: normalizedOwner.scopeId,
        ownerTeamId: normalizedOwner.teamId,
        ownerMemberId: normalizedOwner.memberId,
      }
    : {};
}
```

Replace all internal `encodeOwnerQuery(...)` calls with `encodeScheduledDispatchOwnerQuery(...)`. Do not export `encodeOwner` or accept untyped owner bags.

- [ ] **Step 2: Add one route-to-owner conversion and canonical read URL builders**

In `teamAutomationApi.ts`, import the exported encoder and typed owner, then add:

```ts
function scheduleOwner(route: TeamAutomationRoute): ScheduledDispatchOwner {
  const normalized = normalizeRoute(route);
  return {
    kind: "studio_member_automation",
    scopeId: normalized.scopeId,
    teamId: normalized.teamId,
    memberId: normalized.memberId,
  };
}

function scheduleCollectionPath(
  route: TeamAutomationRoute,
  query?: { readonly cursor?: string; readonly take?: number },
): string {
  return withQuery("/api/schedules", {
    ...encodeScheduledDispatchOwnerQuery(scheduleOwner(route)),
    cursor: query?.cursor,
    includeTotalCount: true,
    take: query?.take,
  });
}

function scheduleDetailPath(route: TeamAutomationRoute, scheduleId: string): string {
  const normalizedScheduleId = scheduleId.trim();
  if (!normalizedScheduleId) {
    throw new Error("Team automation scheduleId is required.");
  }
  return withQuery(`/api/schedules/${encodeURIComponent(normalizedScheduleId)}`, {
    ...encodeScheduledDispatchOwnerQuery(scheduleOwner(route)),
  });
}
```

Keep `basePath` and the nested schedule mutation path only for the separately documented preflight/write debt; do not use them for list or detail.

- [ ] **Step 3: Decode only the canonical schedule current-state fields into `TeamAutomationView`**

Change the view decoder's identity and lifecycle sources to:

```ts
const teamOwned = readBoolean(record, ["teamOwned", "TeamOwned"], `${label}.teamOwned`);
if (!teamOwned) {
  throw new Error(`${label} is not a Team-owned automation schedule.`);
}

return {
  scopeId: requiredString(record, ["teamOwnerScopeId", "TeamOwnerScopeId"], `${label}.teamOwnerScopeId`),
  teamId: requiredString(record, ["teamId", "TeamId"], `${label}.teamId`),
  memberId: requiredString(record, ["teamOwnerMemberId", "TeamOwnerMemberId"], `${label}.teamOwnerMemberId`),
  scheduleId: requiredString(record, ["scheduleId", "ScheduleId"], `${label}.scheduleId`),
  publishedServiceId: requiredString(record, ["serviceId", "ServiceId"], `${label}.serviceId`),
  credentialSourceKind: normalizeCredentialSourceKind(field(record, "credentialSourceKind", "CredentialSourceKind")),
  displayName: readString(record, ["displayName", "DisplayName"], `${label}.displayName`),
  prompt: readString(record, ["prompt", "Prompt"], `${label}.prompt`),
  cronExpression: requiredString(record, ["cronExpression", "CronExpression"], `${label}.cronExpression`),
  timezone: requiredString(record, ["timezone", "Timezone"], `${label}.timezone`),
  enabled: readBoolean(record, ["enabled", "Enabled"], `${label}.enabled`),
  authorizationStatus: normalizeStatus(field(record, "teamAutomationLifecycleStatus", "TeamAutomationLifecycleStatus")),
  credentialExpiresAtUtc: decodeNullableTimestamp(field(record, "credentialExpiresAt", "CredentialExpiresAt"), `${label}.credentialExpiresAt`),
  lastAuthorizationErrorCode: readString(record, ["lastAuthorizationErrorCode", "LastAuthorizationErrorCode"], `${label}.lastAuthorizationErrorCode`),
  operationId: requiredString(record, ["teamAutomationOperationId", "TeamAutomationOperationId"], `${label}.teamAutomationOperationId`),
  credentialGeneration: requiredNonNegativeInteger(record, ["credentialGeneration", "CredentialGeneration"], `${label}.credentialGeneration`),
  revocationPending: readBoolean(record, ["revocationPending", "RevocationPending"], `${label}.revocationPending`),
  nextFireAt: decodeNullableTimestamp(field(record, "nextFireAt", "NextFireAt"), `${label}.nextFireAt`),
  lastFireAt: decodeNullableTimestamp(field(record, "lastFireAt", "LastFireAt"), `${label}.lastFireAt`),
  nyxIdRevocationStatus: normalizeRevocationTrack(field(record, "nyxIdRevocationStatus", "NyxIdRevocationStatus")),
  vaultRevocationStatus: normalizeRevocationTrack(field(record, "vaultRevocationStatus", "VaultRevocationStatus")),
  ownerLLMRouteKind: requiredString(record, ["ownerLlmRouteKind", "OwnerLlmRouteKind", "ownerLLMRouteKind", "OwnerLLMRouteKind"], `${label}.ownerLlmRouteKind`),
  ownerLLMRoute: requiredString(record, ["ownerLlmRoute", "OwnerLlmRoute", "ownerLLMRoute", "OwnerLLMRoute"], `${label}.ownerLlmRoute`),
  ownerLLMUserServiceId: readString(record, ["ownerLlmUserServiceId", "OwnerLlmUserServiceId", "ownerLLMUserServiceId", "OwnerLLMUserServiceId"], `${label}.ownerLlmUserServiceId`),
  ownerLLMServiceSlug: readString(record, ["ownerLlmServiceSlug", "OwnerLlmServiceSlug", "ownerLLMServiceSlug", "OwnerLLMServiceSlug"], `${label}.ownerLlmServiceSlug`),
  ownerLLMModel: requiredString(record, ["ownerLlmModel", "OwnerLlmModel", "ownerLLMModel", "OwnerLLMModel"], `${label}.ownerLlmModel`),
  stateVersion: requiredNonNegativeInteger(record, ["stateVersion", "StateVersion"], `${label}.stateVersion`),
  updatedAt: decodeTimestamp(field(record, "updatedAt", "UpdatedAt"), `${label}.updatedAt`),
};
```

- [ ] **Step 4: Point list, listAll, and detail at the canonical read builders**

Use `requestTeamAutomation` so typed retry/error details remain intact:

```ts
function listTeamAutomations(
  route: TeamAutomationRoute,
  query?: { readonly cursor?: string; readonly take?: number },
): Promise<TeamAutomationListResult> {
  return requestTeamAutomation(
    scheduleCollectionPath(route, query),
    (value, label) => decodeListForRoute(value, route, label),
  );
}

function decodeDetailForRoute(
  value: unknown,
  route: TeamAutomationRoute,
  label = "ScheduledDispatchDetail",
): TeamAutomationView {
  const record = expectRecord(value, label);
  return decodeViewForRoute(field(record, "schedule", "Schedule"), route, `${label}.schedule`);
}
```

Update `get` to request `scheduleDetailPath(route, scheduleId)` and decode the nested schedule. Leave preflight and all write methods unchanged.

- [ ] **Step 5: Run both adapter suites and verify green**

Run:

```bash
pnpm --dir apps/aevatar-console-web test --runInBand -- src/shared/api/teamAutomationApi.test.ts src/shared/api/scheduledDispatchApi.test.ts
```

Expected: 2 suites PASS; list and detail use `/api/schedules`; existing generic schedule behavior remains green.

- [ ] **Step 6: Commit the canonical read adapter**

```bash
git add apps/aevatar-console-web/src/shared/api/scheduledDispatchApi.ts apps/aevatar-console-web/src/shared/api/teamAutomationApi.ts apps/aevatar-console-web/src/shared/api/teamAutomationApi.test.ts
git commit -m "Fix Team automation schedule queries"
```

### Task 3: Lock Canonical Member Resolution In Failing Component Tests

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.test.tsx`

- [ ] **Step 1: Let the test renderer provide multiple member fixtures**

Replace the fixed helper with:

```ts
function renderTab(
  routeMemberId = "",
  members: readonly TeamAutomationMemberRow[] = [member],
) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <TeamAutomationsTab
        members={members}
        routeMemberId={routeMemberId}
        scopeId="scope-alpha"
        teamId="team-alpha"
      />
    </QueryClientProvider>,
  );
}
```

Import `TeamAutomationMemberRow` and extend the history mock to include `replace: jest.fn()`.

- [ ] **Step 2: Replace the bug-locking zero-query test with authority-resolution tests**

Add tests that prove:

```ts
it("canonicalizes the sole eligible member from the Team shell", async () => {
  renderTab();
  await waitFor(() => expect(history.replace).toHaveBeenCalledWith(member.automationsHref));
  expect(teamAutomationApi.listAll).not.toHaveBeenCalled();
});

it("canonicalizes the explicitly selected eligible member", async () => {
  const selected = { ...member, memberId: "m-beta", key: "m-beta", isSelectedMember: true,
    automationsHref: "/scopes/scope-alpha/teams/team-alpha/members/m-beta/automations" };
  renderTab("", [member, selected]);
  await waitFor(() => expect(history.replace).toHaveBeenCalledWith(selected.automationsHref));
});
```

The component test intentionally does not expect a query before the mocked navigation rerenders the canonical route.

- [ ] **Step 3: Add ambiguous and invalid authority tests**

For two eligible, unselected members, assert no navigation and no query, then choose `m-beta` from a visible `Automation member` select and assert `history.push` receives its canonical href. Keep the existing invalid path-member test and add an assertion that neither `push` nor `replace` selects another member.

- [ ] **Step 4: Run the component suite and verify the intended red state**

Run:

```bash
pnpm --dir apps/aevatar-console-web test --runInBand -- src/pages/teams/tabs/TeamAutomationsTab.test.tsx
```

Expected: FAIL because the current component silently chooses the first eligible member, has no shell chooser, and never canonicalizes the Team shell.

### Task 4: Implement Canonical Member Navigation And The Ambiguous-Member Chooser

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.tsx`
- Test: `apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.test.tsx`

- [ ] **Step 1: Resolve member authority without roster-order fallback**

Replace the current first-eligible fallback with memoized, explicit resolution:

```ts
const eligibleMembers = React.useMemo(
  () => members.filter((member) => member.canAutomateMember),
  [members],
);
const routeMember = members.find((member) => trim(member.memberId) === routeMemberId);
const selectedTeamMember = eligibleMembers.find((member) => member.isSelectedMember);
const canonicalMember = routeMember?.canAutomateMember
  ? routeMember
  : !routeMemberId
    ? selectedTeamMember ?? (eligibleMembers.length === 1 ? eligibleMembers[0] : undefined)
    : undefined;
```

Rename local `selectedMember` uses to `canonicalMember` so an invalid path member can never fall through to a different roster member.

- [ ] **Step 2: Canonicalize only a resolved Team selector shell**

Add:

```ts
React.useEffect(() => {
  if (routeMemberId || !canonicalMember) {
    return;
  }
  history.replace(canonicalMember.automationsHref);
}, [canonicalMember, routeMemberId]);
```

Keep `route.memberId` derived only from `routeMemberId`; the query remains disabled until the route has actually become canonical.

- [ ] **Step 3: Render a real chooser for multiple unresolved eligible members**

In the `!routeMember && !canonicalMember && eligibleMembers.length > 1` shell state, render:

```tsx
<Select
  aria-label={copy("teams.automations.form.memberAria", "Automation member")}
  onChange={(memberId) => {
    const member = eligibleMembers.find((candidate) => candidate.memberId === memberId);
    if (member) history.push(member.automationsHref);
  }}
  options={eligibleMembers.map((member) => ({
    label: member.name,
    value: member.memberId,
  }))}
  placeholder={copy("teams.automations.member.select", "Select a member")}
/>
```

Do not set a default value. Do not create a Team-wide list query. Keep create actions disabled until navigation establishes a canonical route member.

- [ ] **Step 4: Run the component suite and verify green**

Run:

```bash
pnpm --dir apps/aevatar-console-web test --runInBand -- src/pages/teams/tabs/TeamAutomationsTab.test.tsx
```

Expected: suite PASS with the sole/selected canonicalization, ambiguous chooser, canonical path query, and invalid path tests green.

- [ ] **Step 5: Commit member resource resolution**

```bash
git add apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.tsx apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.test.tsx
git commit -m "Canonicalize Team automation members"
```

### Task 5: Prove The Real Team Route Triggers The Exact Query

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/teams/detail.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/teams/detail.test.tsx`

- [ ] **Step 1: Replace the Team-shell zero-request regression with canonical navigation and query assertions**

Starting from:

```text
/scopes/scope-1/teams/t-alpha?memberId=member-team-alpha&workflowId=wf-alpha&serviceId=svc-alpha&tab=automations
```

wait for both outcomes:

```ts
await waitFor(() => {
  expect(window.location.pathname).toBe(
    "/scopes/scope-1/teams/t-alpha/members/member-team-alpha/automations",
  );
  expect(teamAutomationApi.listAll).toHaveBeenCalledWith(
    {
      scopeId: "scope-1",
      teamId: "t-alpha",
      memberId: "member-team-alpha",
    },
    { take: 200 },
  );
});
expect(window.location.search).toBe("");
expect(teamAutomationApi.listAll).toHaveBeenCalledTimes(1);
```

This is the regression test for the screenshot symptom: entering the resolvable Team Automations shell now produces the exact member query.

- [ ] **Step 2: Preserve path authority against conflicting query candidates**

Keep the canonical route test with:

```text
/scopes/scope-1/teams/t-alpha/members/member-team-alpha/automations?memberId=m-other&workflowId=wf-alpha&serviceId=svc-alpha
```

Assert the only queried member is `member-team-alpha`, and add `expect(teamAutomationApi.listAll).toHaveBeenCalledTimes(1)`.

- [ ] **Step 3: Run the Team detail suite and verify green**

Run:

```bash
pnpm --dir apps/aevatar-console-web test --runInBand -- src/pages/teams/detail.test.tsx
```

Expected: suite PASS; the selector shell canonicalizes and queries once; conflicting query identities do not change the path owner.

- [ ] **Step 4: Commit the integration regression**

```bash
git add apps/aevatar-console-web/src/pages/teams/detail.test.tsx
git commit -m "Test Team automation query navigation"
```

### Task 6: Verify Boundaries, Build, And Final Diff

**Files:**
- Verify: all files changed in Tasks 1-5
- Remove: `task_plan.md`, `findings.md`, `progress.md` from the isolated worktree after their investigation role is complete

- [ ] **Step 1: Run all focused regression suites together**

```bash
pnpm --dir apps/aevatar-console-web test --runInBand -- src/shared/api/teamAutomationApi.test.ts src/shared/api/scheduledDispatchApi.test.ts src/pages/teams/tabs/TeamAutomationsTab.test.tsx src/pages/teams/detail.test.tsx
```

Expected: 4 suites PASS with zero failures.

- [ ] **Step 2: Run mandatory repository guards for the changed tests and read path**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
```

Expected: both commands exit 0. No polling allowlist entry is needed because the React tests use Testing Library's existing deterministic async utilities and add no `Task.Delay`/`WaitUntilAsync`.

- [ ] **Step 3: Run frontend type checking**

```bash
pnpm --dir apps/aevatar-console-web tsc
```

Expected: exit 0 with no TypeScript errors.

- [ ] **Step 4: Run the complete frontend test suite**

```bash
pnpm --dir apps/aevatar-console-web test --runInBand
```

Expected: all frontend suites PASS with zero failures.

- [ ] **Step 5: Run the production frontend build**

```bash
pnpm --dir apps/aevatar-console-web build
```

Expected: exit 0 and production assets emitted successfully.

- [ ] **Step 6: Remove only the temporary investigation files**

Delete the untracked worktree-root files `task_plan.md`, `findings.md`, and `progress.md`. Confirm they are not staged or committed; do not delete any user-owned file from the original checkout.

- [ ] **Step 7: Inspect the final branch and scope**

```bash
git status --short
git diff origin/dev...HEAD --check
git diff origin/dev...HEAD --stat
git log --oneline origin/dev..HEAD
```

Expected: the branch contains the approved design, canonical read adapter, member canonicalization, and focused tests only. No backend endpoint is added, no lifecycle write method is redirected, and no temporary planning file remains.

- [ ] **Step 8: Commit any final test-only correction if verification required one**

If verification required a source or test correction, stage only its explicit files and commit with:

```bash
git commit -m "Correct Team automation query regression"
```

If no correction was needed, do not create an empty commit.
