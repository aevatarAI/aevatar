# Agent Profile Owner Switch Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `我的 Profile` / `system/` switches immediate while authoritative reads refresh safely in the background.

**Architecture:** Keep a completed browser-local query snapshot per owner and restore it synchronously on switch. Replace the serial request waterfall with concurrent list/binding reads, allow detail to follow the list independently, and gate every state write by owner plus request generation.

**Tech Stack:** Embedded HTML/JavaScript, ASP.NET Core static asset hosting, xUnit, FluentAssertions, Node `vm` behavior harness.

## Global Constraints

- Preserve actor-backed read-model authority, ETag, idempotency, receipt polling, authorization, and binding semantics.
- Change only `admin.html`, its focused Capabilities test, and these approved work documents.
- Add no dependency, API, server cache, persistence, compatibility route, or speculative abstraction.
- Cached data is stale-while-revalidate display state only; stale requests must not overwrite the active owner.

---

### Task 1: Owner snapshot and concurrent refresh behavior

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`
- Test: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`

**Interfaces:**
- Consumes: existing `AGENT_PROFILE_STATE`, `agentProfileJson`, `loadAgentProfileDetail`, `render`, and owner endpoints.
- Produces: browser-local owner snapshot helpers plus request-generation guarded `loadAgentProfiles`.

- [ ] **Step 1: Write the failing behavior test**

Add one Node `vm` test that extracts the real Agent Profile loader/snapshot functions. Drive deferred list, personal-binding, system-binding, and detail promises and assert literal observable behavior: list and bindings start before any promise resolves; the detail starts after list resolution without waiting for bindings; switching owner restores the saved snapshot synchronously; resolving the previous owner's deferred calls afterward cannot overwrite the active target owner.

- [ ] **Step 2: Run the focused test and verify RED**

Run: `dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~BackendConsoleStaticAssetEndpointTests.AdminShell_AgentProfiles_ShouldSwitchOwnersFromCacheAndIgnoreStaleRefreshes'`

Expected: FAIL because owner snapshots and generation-guarded concurrent loading do not exist.

- [ ] **Step 3: Implement the minimum loader change**

Add a two-key `mine/system` snapshot object and a monotonically increasing request generation. Save/restore only completed list/detail/ETag/binding query state. In the owner handler, restore and render before refreshing. In `loadAgentProfiles`, launch list and binding reads together, reveal the list before detail, and check the request generation plus owner before each state write. Keep mutation polling on the same loader.

- [ ] **Step 4: Run the focused test and full static asset class**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~BackendConsoleStaticAssetEndpointTests'
bash tools/ci/test_stability_guards.sh
```

Expected: 0 failures; test stability guard passes.

- [ ] **Step 5: Verify repository gates**

Run:

```bash
bash tools/docs/lint.sh
bash tools/ci/architecture_guards.sh
dotnet build aevatar.slnx --nologo --no-restore
dotnet test aevatar.slnx --nologo --no-build --no-restore
```

Expected: every command exits 0. Existing dependency warnings may remain unchanged.

- [ ] **Step 6: Review, commit, and push**

Review the diff against this design, then commit with `Improve Agent Profile owner switching` and push `fix/2026-08-01_agent-profile-owner-switch` to `origin`.
