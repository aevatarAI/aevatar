# Admin Observatory UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `/admin#/observatory` default to the caller's own scope, provide honest URL-backed filtering and exact-scope deep links, and give run details a collapsible large canvas plus an explicit session-persistent immersive mode.

**Architecture:** Keep the existing zero-build embedded `admin.html` as the single UI and data path. Add small hash-state, request-URL, and detail-endpoint helpers around the existing read-only observatory endpoints; keep presentation-only state in memory or `sessionStorage`, and keep scope, filters, selected run, and tab in the hash query.

**Tech Stack:** Embedded HTML/CSS, ES5-compatible browser JavaScript, ASP.NET Core static asset serving, .NET 10, xUnit, FluentAssertions, Playwright visual verification.

## Global Constraints

- `/admin` remains `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`; no React/Umi page, npm dependency, build step, or second observatory data path is added.
- Every caller, including a platform admin, defaults to `scope=mine`; only explicit `scope=all` maps to the backend `__all__` sentinel.
- Canonical observatory hash keys are exactly `scope`, `status`, `origin`, `definition`, `schedule`, `from`, `to`, `run`, and `tab`.
- `status` is one of `running`, `completed`, `failed`, or `stopped`; `origin`, `definition`, and `schedule` are CSV values; `from` and `to` are valid ISO-8601 timestamps before they enter a request.
- Fleet deep links preserve the run's exact scope; schedule deep links set a real `schedule` filter; neither source silently widens observation to all scopes.
- Selected runs outside the active list remain pinned and selected with the label `不在当前筛选结果中`.
- Detail polling preserves cached content, current tab, and scroll position; monotonic list request IDs continue to reject stale responses.
- Immersive mode is explicit, stored in `sessionStorage`, excluded from the URL, and exited with `Escape` after any active overlay gets first refusal.
- Static asset behavior changes are test-first, canonical behavior is updated in `docs/canon/backend-console.md`, and `.superpowers/` visual companion artifacts are never committed.

---

### Task 1: Canonical URL State And List Requests

**Files:**
- Modify: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`

**Interfaces:**
- Consumes: existing `parseHash()`, `buildHash(parts, q)`, and `adminJson(path, opts)` helpers.
- Produces: `obsSyncStateFromUrl()`, `obsCanonicalQuery(overrides)`, `obsNavigate(overrides)`, `obsListRequestUrl()`, and `obsValidTimestamp(value)`.
- Produces: `OBS_STATE` fields `scope`, `status`, `origin`, `definition`, `schedule`, `from`, `to`, `selectedId`, and `tab`.

- [ ] **Step 1: Add failing static asset contract tests for own-scope defaults and every server filter**

Add focused assertions to the `/admin` asset tests:

```csharp
[Fact]
public async Task AdminShell_ObservatoryRouteState_ShouldDefaultToMineAndBuildSupportedFilters()
{
    await using var app = await CreateAppAsync();
    var html = await app.GetTestClient().GetStringAsync("/admin");

    html.Should().Contain("scope:'mine',status:'',origin:'',definition:'',schedule:'',from:'',to:''");
    html.Should().Contain("if(OBS_STATE.scope==='all') p.set('scope','__all__')");
    html.Should().Contain("['status','origin','definition','schedule','from','to'].forEach");
    html.Should().Contain("p.set('take','100')");
    html.Should().Contain("if(value&&!obsValidTimestamp(value)) return");
    html.Should().NotContain("scope:'__all__'");
    html.Should().NotContain("statusFilter:[]");
}

[Fact]
public async Task AdminShell_ObservatoryRouteState_ShouldUseCanonicalHashKeys()
{
    await using var app = await CreateAppAsync();
    var html = await app.GetTestClient().GetStringAsync("/admin");

    html.Should().Contain(
        "var OBS_QUERY_KEYS=['scope','status','origin','definition','schedule','from','to','run','tab']");
    html.Should().Contain("OBS_STATUS_VALUES.indexOf(q.status)>=0");
    html.Should().Contain("OBS_TAB_VALUES.indexOf(q.tab)>=0");
    html.Should().Contain("selectedId:q.run||null");
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter FullyQualifiedName~BackendConsoleStaticAssetEndpointTests
```

Expected: the two new tests fail because the asset still defaults to `__all__`, uses `statusFilter`, and does not construct filter query parameters.

- [ ] **Step 3: Implement canonical state parsing and request construction**

Replace the observatory state initializer and list URL assembly with helpers shaped as follows:

```javascript
var OBS_QUERY_KEYS=['scope','status','origin','definition','schedule','from','to','run','tab'];
var OBS_STATUS_VALUES=['running','completed','failed','stopped'];
var OBS_TAB_VALUES=['timeline','steps','diagnostics','logs','artifacts','graph'];
var OBS_STATE={selectedId:null,tab:'timeline',filterOpen:false,localSearch:'',railCollapsed:false,
  immersive:false,scope:'mine',status:'',origin:'',definition:'',schedule:'',from:'',to:''};

function obsValidTimestamp(value){
  return !value||!isNaN(new Date(value).getTime());
}
function obsListRequestUrl(){
  var p=new URLSearchParams();
  if(OBS_STATE.scope==='all') p.set('scope','__all__');
  else if(OBS_STATE.scope&&OBS_STATE.scope!=='mine') p.set('scope',OBS_STATE.scope);
  ['status','origin','definition','schedule','from','to'].forEach(function(key){
    var value=OBS_STATE[key];
    if((key==='from'||key==='to')&&value&&!obsValidTimestamp(value)) return;
    if(value) p.set(key,value);
  });
  p.set('take','100');
  return '/api/workflow/observatory/runs?'+p.toString();
}
```

`obsSyncStateFromUrl()` accepts only known status/tab values, treats omitted scope as `mine`, and allows exact/all scope only for admins. `obsCanonicalQuery()` emits only the nine approved keys and omits default/empty values. Filter controls update the hash through `obsNavigate()` so refresh, back, forward, polling, and shared links use the same state.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the Task 1 test command again.

Expected: all `BackendConsoleStaticAssetEndpointTests` pass with zero failures.

- [ ] **Step 5: Commit URL state and list filter semantics**

```bash
git add test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs \
  src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html
git commit -m "Fix observatory scope and filter state"
```

---

### Task 2: Exact Deep Links, Endpoint Intent, And Pinned Runs

**Files:**
- Modify: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`

**Interfaces:**
- Consumes: Task 1 `OBS_STATE`, `obsCanonicalQuery(overrides)`, and `obsNavigate(overrides)`.
- Produces: `obsDetailRequestBase(runId)`, `obsGraphRequestUrl(runId)`, `OBS_DIRECT_RUNS`, `obsPinnedRun()`, and exact-scope deep-link data attributes.

- [ ] **Step 1: Add failing tests for endpoint selection and navigation identity**

Add contract tests containing these assertions:

```csharp
html.Should().Contain("if(OBS_STATE.scope==='all'||OBS_DIRECT_RUNS[runId])");
html.Should().Contain("if(OBS_STATE.scope&&OBS_STATE.scope!=='mine') p.set('scope',OBS_STATE.scope)");
html.Should().Contain("return base+(p.toString()?'?'+p.toString():'')");
html.Should().Contain("obsNavigate({run:rid,scope:runScope||'mine'})");
html.Should().NotContain("OBS_STATE.scope='__all__'");
html.Should().Contain("obsNavigate({schedule:act.getAttribute('data-id')})");
html.Should().Contain("不在当前筛选结果中");
html.Should().Contain("data-obs-pinned=\"true\"");
```

Retain the existing cached-detail polling assertions.

- [ ] **Step 2: Run the focused tests and verify RED**

Run the Task 1 test command.

Expected: endpoint and deep-link assertions fail because admin identity still selects every admin detail endpoint, Fleet forces `__all__`, and no pinned row exists.

- [ ] **Step 3: Implement intent-based endpoint selection and pinned selection**

Use normal endpoints for `mine` and exact scope, and admin endpoints only for all-scope or unknown-owner manual lookup:

```javascript
var OBS_DIRECT_RUNS={};
function obsDetailRequestBase(runId){
  if(OBS_STATE.scope==='all'||OBS_DIRECT_RUNS[runId])
    return '/api/workflow/observatory/admin/runs/'+encodeURIComponent(runId);
  var base='/api/workflow/observatory/runs/'+encodeURIComponent(runId),p=new URLSearchParams();
  if(OBS_STATE.scope&&OBS_STATE.scope!=='mine') p.set('scope',OBS_STATE.scope);
  return base+(p.toString()?'?'+p.toString():'');
}
```

Build graph URLs before the query suffix. Do not insert a synthetic deep-linked run into `OBS_RUNS`; render it separately above server results when selected and absent, mark it `data-obs-pinned="true"`, and keep loading its detail. Mark only manually entered full run IDs in `OBS_DIRECT_RUNS`.

Add `data-scope` to Fleet run rows and navigate with exact `run + scope`. Update schedule actions to call `obsNavigate({schedule:...})`, preserving the last intentional observatory scope or falling back to `mine`.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the Task 1 test command again.

Expected: all tests pass, including the existing cached-detail polling contract.

- [ ] **Step 5: Commit deep-link and detail intent behavior**

```bash
git add test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs \
  src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html
git commit -m "Preserve observatory deep link scope"
```

---

### Task 3: Large Detail Canvas And Immersive Observation

**Files:**
- Modify: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`

**Interfaces:**
- Consumes: Task 1 URL state and Task 2 selected/pinned run behavior.
- Produces: `obsHeader()`, `obsFilterBar()`, `obsRunRail()`, `obsSetImmersive(enabled)`, `OBS_SESSION_IMMERSIVE`, and `OBS_SESSION_RAIL`.

- [ ] **Step 1: Add failing layout and immersive-mode contracts**

Add assertions for explicit controls, session persistence, and accessibility:

```csharp
html.Should().Contain("class=\"obs-scope-switch\" role=\"group\" aria-label=\"观测 scope\"");
html.Should().Contain("aria-pressed=\"");
html.Should().Contain("data-act=\"obsImmersive\"");
html.Should().Contain("sessionStorage.setItem(OBS_SESSION_IMMERSIVE,enabled?'1':'0')");
html.Should().Contain("if(OBS_STATE.immersive){ obsSetImmersive(false); render(); }");
html.Should().Contain("data-act=\"obsRailToggle\"");
html.Should().Contain("class=\"obs-admin-tools\"");
html.Should().Contain("body.obs-immersive .rail");
html.Should().Contain("body.obs-immersive .app-header");
html.Should().NotContain("class=\"obs-adminbar\"");
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run the Task 1 test command.

Expected: assertions fail because the current page has the red admin bar, fixed 320 px list, no rail collapse, and no immersive state.

- [ ] **Step 3: Implement the approved operational layout**

Replace the red admin bar and collapsed filter form with:

```text
compact observatory context header
  -> mine/all scope segmented control
  -> explicit scope warning and return-to-mine action
  -> refresh, rail toggle, admin tools, immersive action
dense filter toolbar
  -> local search, single status select, advanced filter menu, active chips
split work area
  -> 360 px bounded run rail with stable rows and honest visible/loaded count
  -> flexible run detail canvas using all remaining width
```

Use a native `<details class="obs-admin-tools">` menu for email/scope and full-run-ID lookup. Keep row dimensions stable and show name, shortened ID, updated time, definition, status, and origin. Make tabs horizontally scrollable at narrow widths and use a list-above-detail layout below 768 px.

Implement session-only viewing preferences:

```javascript
function obsSetImmersive(enabled){
  OBS_STATE.immersive=!!enabled;
  try{ sessionStorage.setItem(OBS_SESSION_IMMERSIVE,enabled?'1':'0'); }catch(e){}
  document.body.classList.toggle('obs-immersive',OBS_STATE.immersive);
}
```

Immersive rendering retains a compact bar with scope, selected run, refresh/live state, and exit. Global Escape handling closes an open drawer, popover, or admin tools menu first; otherwise it exits immersive mode without changing the hash.

- [ ] **Step 4: Run focused tests, JS syntax validation, and the static asset guard**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter FullyQualifiedName~BackendConsoleStaticAssetEndpointTests
bash tools/ci/backend_console_static_asset_guard.sh
node -e "const fs=require('fs'),h=fs.readFileSync('src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html','utf8'),s=[...h.matchAll(/<script(?: [^>]*)?>([\\s\\S]*?)<\\/script>/g)].map(x=>x[1]).join('\\n').replace('__BACKEND_CONSOLE_CONFIG__','{}'); new Function(s); console.log('admin script syntax: ok')"
```

Expected: focused tests pass, the guard prints `backend_console_static_asset_guard: ok`, and Node prints `admin script syntax: ok`.

- [ ] **Step 5: Commit the observatory workspace redesign**

```bash
git add test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs \
  src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html
git commit -m "Redesign admin run observatory"
```

---

### Task 4: Canonical Documentation, Visual QA, And Delivery

**Files:**
- Modify: `docs/canon/backend-console.md`
- Verify only: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`

**Interfaces:**
- Consumes: completed Tasks 1-3 behavior.
- Produces: canonical scope/filter/deep-link/endpoint documentation and verified commits on `origin/feature/integrate`.

- [ ] **Step 1: Update canonical backend-console semantics**

Extend the Workflow Observatory endpoint boundary with these exact rules:

```markdown
The embedded `/admin#/observatory` UI defaults every caller, including an administrator, to the caller's own scope. `scope=all` is an explicit administrator viewing mode that maps to the backend-only `__all__` sentinel; exact scope IDs remain exact and are never inferred from role.

Its shareable hash state uses only `scope`, `status`, `origin`, `definition`, `schedule`, `from`, `to`, `run`, and `tab`. Fleet links carry exact `scope + run`; schedule links carry `schedule`; a selected run outside the current list stays pinned instead of changing the filters.

Own-scope detail and graph reads use normal endpoints without `scope`; exact-scope reads use normal endpoints with that scope; all-scope selections and unknown-owner manual run lookup use the administrator endpoints. Administrator identity by itself does not select the administrator endpoint.
```

- [ ] **Step 2: Run repository verification**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
bash tools/ci/backend_console_static_asset_guard.sh
bash tools/ci/test_stability_guards.sh
bash tools/docs/lint.sh
bash tools/ci/architecture_guards.sh
```

Expected: every command exits zero. If the broad architecture guard delegates to additional guards, retain its complete output as the verification record.

- [ ] **Step 3: Run visual and interaction verification**

Start the Mainnet Host on a free non-5000/non-5050 port with its normal local configuration, open `/admin#/observatory`, and use the in-app browser/Playwright at:

```text
1440x900
1920x1080
1024x768
390x844
```

At each relevant width verify normal and immersive modes, expanded advanced filters, a long run ID, a long log line, no-results/error presentation, keyboard focus, scope visibility, no text overlap, and Escape exit. Inspect browser console errors and confirm list/detail polling does not change the selected tab or scroll position.

- [ ] **Step 4: Commit documentation and any visual corrections**

```bash
git add docs/canon/backend-console.md \
  src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html \
  test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
git commit -m "Document observatory scope semantics"
```

If visual QA required no code correction, commit only `docs/canon/backend-console.md` with the same message.

- [ ] **Step 5: Reconcile the remote branch and push without force**

```bash
git fetch origin feature/integrate
git merge --no-edit origin/feature/integrate
git status --short --branch
git push origin HEAD:feature/integrate
```

Expected: the merge is clean or conflicts are resolved without discarding remote work; `.superpowers/` remains untracked; push reports the new `feature/integrate` tip and does not use `--force`.
