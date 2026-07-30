# Hide Archived Teams Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove archived Teams from the Team home-page roster, its summary counts, and its runtime sampling without changing the shared Team API contract.

**Architecture:** Derive one visible Team collection in `TeamsHomePage` after the server roster and pending just-created summaries are merged. Filter only the normalized `archived` lifecycle stage, then use that collection for runtime-query selection and preview construction so every downstream UI result shares the same visibility rule.

**Tech Stack:** React 19, TypeScript, TanStack Query, Jest, Testing Library, pnpm.

---

### Task 1: Capture Archived Team Visibility as a Regression

**Files:**
- Test: `apps/aevatar-console-web/src/pages/teams/home.test.tsx`

- [ ] **Step 1: Add an active-plus-archived Team regression test**

Add this test after `does not show the roster view toggle when only one Team is visible`:

```tsx
it("excludes archived Teams from the roster, summary counts, and runtime sampling", async () => {
  (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
    scopeId: "scope-a",
    teams: [
      defaultTeams[0],
      {
        teamId: "t-archived",
        scopeId: "scope-a",
        displayName: "已归档团队",
        description: "不再参与当前 Team roster",
        lifecycleStage: "archived",
        entryMemberId: "member-archived",
        memberCount: 1,
        createdAt: "2026-05-01T09:00:00Z",
        updatedAt: "2026-05-01T10:03:00Z",
      },
    ],
    nextPageToken: null,
  });
  (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
    scopeId: "scope-a",
    members: [
      ...defaultMembers,
      {
        ...defaultMembers[0],
        memberId: "member-archived",
        displayName: "归档团队成员",
        publishedServiceId: "service-archived",
        teamId: "t-archived",
      },
    ],
    nextPageToken: null,
  });
  (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValueOnce([
    ...defaultServices,
    {
      ...defaultServices[0],
      serviceId: "service-archived",
      displayName: "归档团队运行时",
    },
  ]);

  renderWithQueryClient(React.createElement(TeamsHomePage));

  expect(
    await screen.findByRole("heading", { level: 3, name: "客服团队" }),
  ).toBeTruthy();
  expect(
    screen.queryByRole("heading", { level: 3, name: "已归档团队" }),
  ).toBeNull();
  expect(screen.getByText("AI 团队总数").previousElementSibling).toHaveTextContent(
    "1",
  );
  expect(screen.getByText("待启动团队").previousElementSibling).toHaveTextContent(
    "1",
  );
  expect(screen.getByText("已有稳定运行").previousElementSibling).toHaveTextContent(
    "0",
  );
  await waitFor(() => {
    expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledTimes(1);
  });
  expect(scopeRuntimeApi.listServiceRuns).toHaveBeenCalledWith(
    "scope-a",
    "service-alpha",
    { take: 1 },
  );
  expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalledWith(
    "scope-a",
    "service-archived",
    { take: 1 },
  );
});
```

- [ ] **Step 2: Add an archived-only empty-roster regression test**

Add this test after the active-plus-archived case:

```tsx
it("shows the empty roster when every Team is archived", async () => {
  (studioApi.listTeams as jest.Mock).mockResolvedValueOnce({
    scopeId: "scope-a",
    teams: [
      {
        teamId: "t-archived",
        scopeId: "scope-a",
        displayName: "已归档团队",
        description: "不再参与当前 Team roster",
        lifecycleStage: "archived",
        entryMemberId: "member-archived",
        memberCount: 1,
        createdAt: "2026-05-01T09:00:00Z",
        updatedAt: "2026-05-01T10:03:00Z",
      },
    ],
    nextPageToken: null,
  });
  (studioApi.listMembers as jest.Mock).mockResolvedValueOnce({
    scopeId: "scope-a",
    members: [
      {
        ...defaultMembers[0],
        memberId: "member-archived",
        displayName: "归档团队成员",
        publishedServiceId: "service-archived",
        teamId: "t-archived",
      },
    ],
    nextPageToken: null,
  });

  renderWithQueryClient(React.createElement(TeamsHomePage));

  expect(
    await screen.findByText(
      "当前账号还没有创建任何团队。创建后，这里会展示你的 AI 团队列表。",
    ),
  ).toBeTruthy();
  expect(
    screen.queryByRole("heading", { level: 3, name: "已归档团队" }),
  ).toBeNull();
  expect(scopeRuntimeApi.listServiceRuns).not.toHaveBeenCalled();
});
```

- [ ] **Step 3: Run both new tests and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web test --runInBand \
  src/pages/teams/home.test.tsx \
  -t "archived Teams|every Team is archived"
```

Expected: both tests fail because the archived Team is still rendered; the
first case also reports two Teams and samples `service-archived`, while the
second case does not render the empty-roster description.

### Task 2: Apply One Home-Page Visibility Boundary

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/teams/home.tsx`
- Test: `apps/aevatar-console-web/src/pages/teams/home.test.tsx`

- [ ] **Step 1: Import the lifecycle normalizer**

Extend the existing `@/shared/studio/models` import:

```tsx
import {
  formatStudioMemberLifecycleStage,
  normalizeStudioTeamLifecycleStage,
  type StudioMemberSummary,
  type StudioTeamSummary,
} from "@/shared/studio/models";
```

- [ ] **Step 2: Derive the visible Team collection once**

Immediately after the existing `studioTeams` memo, add:

```tsx
const visibleStudioTeams = React.useMemo(
  () =>
    studioTeams.filter(
      (team) =>
        normalizeStudioTeamLifecycleStage(team.lifecycleStage) !== "archived",
    ),
  [studioTeams],
);
```

Use `visibleStudioTeams` instead of `studioTeams` in
`runtimeTrackableEntryMemberServices` and `teamPreviews`, including both memo
dependency arrays. Do not filter `studioApi.listTeams` or mutate its result.

- [ ] **Step 3: Remove the obsolete archived preview branch**

Replace:

```tsx
let attention: TeamOperationalAttention =
  runtimeSignalPreview?.attention ?? "draft";
let attentionDetail = t(
  "pages.teams.home.team",
  "This team has no members yet. Next: add an entry member, then test the team.",
);
if (input.team.lifecycleStage === "archived") {
  attention = "draft";
  attentionDetail = t(
    "pages.teams.home.team.roster",
    "This team has been archived; the list keeps only its backend roster fact.",
  );
} else if (runtimeSignalPreview) {
  attentionDetail = runtimeSignalPreview.attentionDetail;
}
```

with:

```tsx
const attention: TeamOperationalAttention =
  runtimeSignalPreview?.attention ?? "draft";
const attentionDetail =
  runtimeSignalPreview?.attentionDetail ??
  t(
    "pages.teams.home.team",
    "This team has no members yet. Next: add an entry member, then test the team.",
  );
```

- [ ] **Step 4: Run the two regression tests and verify GREEN**

Run the command from Task 1, Step 3.

Expected: both tests pass.

- [ ] **Step 5: Run the complete Team home-page test suite**

Run:

```bash
pnpm --dir apps/aevatar-console-web test --runInBand \
  src/pages/teams/home.test.tsx
```

Expected: all Team home-page tests pass with zero failures.

- [ ] **Step 6: Commit the tested behavior change**

```bash
git add \
  apps/aevatar-console-web/src/pages/teams/home.tsx \
  apps/aevatar-console-web/src/pages/teams/home.test.tsx
git commit -m "Hide archived Teams from the roster"
```

### Task 3: Verify the Frontend Change

**Files:**
- Verify: `apps/aevatar-console-web/src/pages/teams/home.tsx`
- Verify: `apps/aevatar-console-web/src/pages/teams/home.test.tsx`
- Verify: `docs/superpowers/specs/2026-07-30-hide-archived-teams-design.md`

- [ ] **Step 1: Run the mandatory test-stability guard**

Run:

```bash
bash tools/ci/test_stability_guards.sh
```

Expected: the polling-wait scan and its guard meta-tests pass.

- [ ] **Step 2: Run frontend type checking**

Run:

```bash
pnpm --dir apps/aevatar-console-web tsc
```

Expected: TypeScript exits with code 0 and no diagnostics.

- [ ] **Step 3: Run the frontend production build**

Run:

```bash
pnpm --dir apps/aevatar-console-web build
```

Expected: the production bundle completes successfully.

- [ ] **Step 4: Check the final branch diff**

Run:

```bash
git diff --check origin/dev...HEAD
git status --short --branch
```

Expected: `git diff --check` exits with code 0 and the worktree is clean. The
branch contains only the approved design/plan documentation, the Team home-page
regression tests, and the Team home-page visibility change.
