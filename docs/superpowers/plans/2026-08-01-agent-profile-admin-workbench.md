# Agent Profile Admin Workbench Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the inline Ornn member editor with a usable Agent Profile workbench containing a multi-select discovery modal, collapsible Skill cards, and an honest sticky lifecycle action bar.

**Architecture:** Keep the existing embedded Backend Console and current Agent Profile/Ornn Web API contracts. Add only browser-local interaction state and small render/mutation helpers inside `admin.html`; authoritative facts still come from exact Ornn reads and actor-backed Profile read models. Verify behavior through the existing Node VM static-asset tests so no new frontend package or dependency is introduced.

**Tech Stack:** Embedded HTML/CSS/vanilla JavaScript, native `details`, native dialog semantics implemented with existing Console modal primitives, ASP.NET Core static asset host, xUnit, Node `vm` assertions.

## Global Constraints

- Preserve `mine/` scope ownership and Admin-only `system/` mutation authorization.
- Preserve existing Agent Profile endpoints, ETag/idempotency behavior, accepted-receipt polling, and explicit validate/publish/bind actions.
- Search summaries must not invent version, publisher, hash, side-effect, or validation facts; those come only from `GET /api/workflow/skills/{guid}/exact`.
- Reuse Backend Console tokens and primitives; add no UI library, frontend application, API contract, or speculative abstraction.
- Creation and editing must use the same Skill-card and Ornn discovery renderers.
- The canonical route remains `#/agent-profiles`; do not restore legacy routes.
- Preserve keyboard access, visible text status, narrow-screen usability, and reduced-motion behavior.
- Modify only the embedded Admin asset, its focused test file, and approved design/implementation documents.

---

### Task 1: Workbench structure, empty state, and collapsible Skill cards

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html:822-903`
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html:2396-2738`
- Test: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs:85-488`

**Interfaces:**
- Consumes: current `agentProfileDraftFromFields`, `agentProfileField`, `agentProfileSelect`, `agentProfileExactEvidenceHtml`, `agentProfileLocalDiagnostics`, and `AGENT_PROFILE_STATE`.
- Produces: `agentProfileSkillsSectionHtml(members, disabled)`, `agentProfileSkillCardHtml(member, index, count, disabled)`, `agentProfileSkillCardIsOpen(member, index)`, `agentProfileRemoveMember(index)`, and `AGENT_PROFILE_STATE.skillCardsOpen`.

- [ ] **Step 1: Write failing static-asset behavior tests**

Add tests that evaluate the served helper functions and require an intentional empty Skills state plus compact native cards:

```javascript
const emptyDraft = agentProfileEmptyDraft();
assert.equal(emptyDraft.runtimeProfile.members.length, 0);
const empty = agentProfileSkillsSectionHtml([], false);
assert.match(empty, /还没有添加 Skill/);
assert.match(empty, /data-ap-open-skills="add"/);

AGENT_PROFILE_STATE.skillCardsOpen = {0:false, 1:true};
const first = agentProfileSkillCardHtml(members[0], 0, 2, false);
const second = agentProfileSkillCardHtml(members[1], 1, 2, false);
assert.match(first, /<details class="ap-skill-card"[^>]*data-ap-member-card="0"/);
assert.doesNotMatch(first, /<details[^>]* open/);
assert.match(second, /<details class="ap-skill-card"[^>]* open/);
assert.match(second, /research.*1.2/);
assert.match(second, /publisher-b/);
assert.match(second, /data-ap-replace-skill="1"/);
assert.match(second, /data-ap-remove-member="1"/);
```

Also update the existing multi-member round-trip assertions to expect `ap-skill-card`, exact hidden inputs, and shared creation/editor rendering.

- [ ] **Step 2: Run focused tests and confirm the new expectations fail**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~AdminShell_AgentProfiles'
```

Expected: failures for missing workbench/card helpers and the current synthetic empty member.

- [ ] **Step 3: Implement the minimum shared workbench/card renderers**

Change `agentProfileDraftFromFields` to honor an explicitly empty `members` array and make `agentProfileEmptyDraft()` pass `members:[]`. Replace `agentProfileMemberHtml` with a shared native-card renderer used by both creation and editing. Its summary must show exact name/version, intent, side-effect, publisher, and a text evidence status. Its body keeps the existing fields/evidence/manual policy and adds Replace/Remove actions.

Add CSS for `.ap-panel`, `.ap-skills-head`, `.ap-skills-empty`, `.ap-skill-card`, `.ap-skill-summary`, `.ap-skill-body`, badges, and separated danger actions using existing variables. Track native `toggle` events in `skillCardsOpen`; incomplete/diagnostic cards open by default when no explicit state exists. Permit removing the final card into the local empty state, reindex proofs/open-state maps, mark dirty, and rely on existing local validation to block Save/Create.

- [ ] **Step 4: Run focused tests and confirm card behavior passes**

Run the Task 1 focused command. Expected: all Agent Profile tests pass, including existing multi-member, system-summary, creation, ETag, and polling cases.

- [ ] **Step 5: Commit the card workbench slice**

```bash
git add src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
git commit -m "Build Agent Profile skill workbench"
```

### Task 2: Ornn discovery modal and exact Skill add/replace

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html:868-903`
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html:2396-2660`
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html:2897-2931`
- Test: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs:189-488`

**Interfaces:**
- Consumes: Task 1's shared Skills renderer, working draft, card/proof state, `agentProfileApplyExactSkill`, `agentProfileUnionNames`, `agentProfileSlugFromName`, `agentProfileJson`, and `render`.
- Produces: `agentProfileOpenSkillModal(root, mode, memberIndex)`, `agentProfileCloseSkillModal()`, `agentProfileSkillModalHtml()`, `agentProfileSearchSkills(root, page)`, `agentProfileToggleSkillSelection(guid)`, `agentProfileConfirmSkillSelection(root)`, `agentProfileMemberFromExactSkill(draft, detail)`, and `AGENT_PROFILE_STATE.skillModal`.

- [ ] **Step 1: Write failing modal and selection tests**

Require honest searchable summaries, duplicate disabling, add/replace modes, selection order, exact-read authority, and partial retry:

```javascript
agentProfileOpenSkillModal(root, 'add', null);
assert.equal(AGENT_PROFILE_STATE.skillModal.mode, 'add');
assert.equal(capturedBeforeRender, true);
AGENT_PROFILE_STATE.skillModal.results = [publicSkill, privateSkill, alreadyAdded];
const modal = agentProfileSkillModalHtml();
assert.match(modal, /role="dialog"/);
assert.match(modal, /aria-modal="true"/);
assert.match(modal, /type="checkbox"/);
assert.match(modal, /已添加/);
assert.doesNotMatch(modal, /publisher-from-nowhere/);

agentProfileToggleSkillSelection('guid-b');
agentProfileToggleSkillSelection('guid-a');
await agentProfileConfirmSkillSelection(root);
assert.deepEqual(draft.runtimeProfile.members.map(x => x.skillRef.guid), ['existing','guid-b']);
assert.deepEqual(AGENT_PROFILE_STATE.skillModal.selected, ['guid-a']);
assert.match(AGENT_PROFILE_STATE.skillModal.exactErrors['guid-a'], /unavailable/);
```

Add a replacement test that fails one exact read and proves the member is unchanged, then succeeds and proves intent/routing/aliases/side-effect are preserved while exact identity changes. Add a stale-request test proving closing the modal ignores late list/exact responses.

- [ ] **Step 2: Run focused tests and confirm modal behavior fails**

Run the Task 1 focused test command. Expected: failures for missing modal state/render/actions.

- [ ] **Step 3: Implement one reusable discovery modal**

Use one `skillModal` object for add/replace mode, query, page, total, results, ordered selected GUIDs, loading/resolving state, and exact errors. Render result rows from current list facts only: name, description, category, tags, privacy, and shortened GUID. Use checkboxes for add and radios for replacement. Disable every GUID already present in the working draft.

Search `/api/workflow/skills?query=...&page=N&pageSize=20`. On confirmation, call `/api/workflow/skills/{guid}/exact` for the selected page entries with native `Promise.allSettled`. Apply successes in selected order, union declared tools, generate unique normalized intent IDs, retain only failures as selected, and keep the modal open with inline Retry errors. Close after complete success. Replacement mutates only after exact success. Use the existing monotonically increasing request token to ignore late responses.

Wire add, replace, search, paging, selection, retry, close, backdrop, and removal actions into `mountAgentProfiles`. Replace the old per-card inline query/results and remove their unused CSS/state.

- [ ] **Step 4: Run focused tests and confirm discovery behavior passes**

Run the focused command. Expected: all Agent Profile tests pass; each async Node VM test exits 0.

- [ ] **Step 5: Commit the modal slice**

```bash
git add src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
git commit -m "Add Ornn skill discovery modal"
```

### Task 3: Sticky lifecycle state, accessibility, and responsive polish

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html:822-903`
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html:2660-2931`
- Test: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs:85-1180`

**Interfaces:**
- Consumes: workbench/card/modal helpers from Tasks 1-2 and existing lifecycle state (`dirty`, `busy`, `pending`, `notice`, `diagnostics`, `publishedRevision`, `available`).
- Produces: `agentProfileLifecycleState(detail)`, `agentProfileActionBarHtml(detail, writable)`, `agentProfileRestoreModalFocus(root)`, and `agentProfileTrapModalFocus(root, event)`.

- [ ] **Step 1: Write failing lifecycle/accessibility tests**

Add table-driven VM assertions for honest state priority:

```javascript
assert.equal(agentProfileLifecycleState(detailWithRevision).label, '已发布 r4');
AGENT_PROFILE_STATE.dirty = true;
assert.equal(agentProfileLifecycleState(detailWithRevision).label, '未保存修改');
AGENT_PROFILE_STATE.busy = true;
assert.equal(agentProfileLifecycleState(detailWithRevision).label, '正在保存');
AGENT_PROFILE_STATE.busy = false; AGENT_PROFILE_STATE.pending = {kind:'draft'};
assert.equal(agentProfileLifecycleState(detailWithRevision).label, '已接受，等待提交/投影');
```

Require `.ap-action-bar` to contain Save/Validate/Publish only for writable details, require the read-only system summary to remain free of the form, and assert the asset includes labelled dialog, `aria-live`, Escape, focus return, focus trap, native details toggle handling, sticky CSS, near-full-height mobile modal CSS, and the existing reduced-motion rule.

- [ ] **Step 2: Run focused tests and confirm lifecycle/accessibility behavior fails**

Run the focused command. Expected: missing lifecycle/action/focus helpers and CSS assertions fail.

- [ ] **Step 3: Implement lifecycle hierarchy and keyboard behavior**

Move Save/Validate/Publish out of the header into one sticky `.ap-action-bar`. Give busy/pending/dirty/validation/published states an explicit priority and text+badge treatment. Keep default binding and system rollout explicit and visually secondary. Use the same action-bar primitive for create Cancel/Create actions.

On modal open, remember whether focus came from add or replacement. On close, render then restore focus to the corresponding current button. Auto-focus the search only on first open. Handle Enter search, Escape close when not resolving, backdrop close, and Tab/Shift+Tab wrapping within the dialog. Add responsive rules so the modal is a near-full-height sheet below 768px, card summaries and action bars wrap, and the sticky bar remains visible.

- [ ] **Step 4: Run focused and full project tests**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~AdminShell_AgentProfiles'
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
bash tools/ci/test_stability_guards.sh
bash tools/docs/lint.sh
```

Expected: all commands exit 0.

- [ ] **Step 5: Commit the lifecycle/polish slice**

```bash
git add src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
git commit -m "Polish Agent Profile lifecycle UX"
```

### Task 4: Final verification and direct integration push

**Files:**
- Verify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`
- Verify: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`
- Verify: `docs/superpowers/specs/2026-08-01-agent-profile-admin-ux-design.md`
- Verify: `docs/superpowers/plans/2026-08-01-agent-profile-admin-workbench.md`

**Interfaces:**
- Consumes: completed workbench commits and current `origin/feature/integrate`.
- Produces: a verified fast-forward update of `origin/feature/integrate`.

- [ ] **Step 1: Fetch and integrate the latest target safely**

```bash
git fetch origin feature/integrate dev --prune
git rebase origin/feature/integrate
```

Resolve only overlaps in the four in-scope files. Never discard unrelated remote changes.

- [ ] **Step 2: Run repository-required verification on the rebased tree**

```bash
git diff --check origin/feature/integrate...HEAD
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
```

Expected: all commands exit 0. If a full-suite failure is unrelated, reproduce it against `origin/feature/integrate` before reporting; do not silently waive it.

- [ ] **Step 3: Inspect the final diff and worktree state**

```bash
git status --short --branch
git log --oneline origin/feature/integrate..HEAD
git diff --stat origin/feature/integrate...HEAD
```

Expected: clean worktree; only the approved spec/plan, Admin asset, and focused test are changed.

- [ ] **Step 4: Push the verified HEAD directly to the requested target**

```bash
git push origin HEAD:feature/integrate
```

Expected: a fast-forward update succeeds. Read back `git ls-remote origin refs/heads/feature/integrate` and require it to equal local `HEAD` before claiming completion.
