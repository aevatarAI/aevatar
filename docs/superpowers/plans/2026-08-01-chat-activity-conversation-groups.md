# Chat Activity Conversation Groups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Organize `/admin#/chat-activity` by Conversation, with only the most recent Conversation expanded by default.

**Architecture:** Keep the existing API and record contract unchanged. Group the already-loaded records by `provenance.chat.conversationId` in `admin.html`, sort groups and rows by activity time, and render each group with native `details`/`summary` elements.

**Tech Stack:** Embedded HTML/CSS/JavaScript, Node VM behavior tests, xUnit.

## Global Constraints

- Do not add a backend endpoint, store, dependency, or client-side persistence.
- Preserve filters, cursor pagination, safe fields, row inspector, and keyboard access.
- The newest Conversation is open by default; all others are closed by default.
- Loading more records merges matching Conversation groups.

---

### Task 1: Group Chat Activity by Conversation

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`
- Test: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`

**Interfaces:**
- Consumes: existing `CHAT_ACTIVITY_DATA` records and `provenance.chat.conversationId`.
- Produces: `chatActivityConversationGroups()` and grouped output from `chatActivityTable()`.

- [ ] **Step 1: Write the failing behavior test**

Add records from two differently shaped Conversation IDs in non-chronological order. Assert that the rendered output contains two native disclosure groups, the group with the latest activity comes first and is the only group initially open, each header includes the Conversation ID and loaded count, and every activity row remains rendered.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~BackendConsoleStaticAssetEndpointTests.AdminShell_ChatActivity'
```

Expected: FAIL because the flat table has no Conversation disclosure groups.

- [ ] **Step 3: Implement the minimum grouped renderer**

Add one grouping helper, reuse existing time/ID/result helpers, render native `details`/`summary`, and add only the CSS needed to distinguish group headers and expanded content. Remove the redundant Conversation column from the nested activity table.

- [ ] **Step 4: Verify GREEN and required guards**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~BackendConsoleStaticAssetEndpointTests.AdminShell_ChatActivity|FullyQualifiedName~BackendConsoleStaticAssetEndpointTests.AdminShell_AuditTrail'
bash tools/ci/test_stability_guards.sh
```

Expected: all commands exit 0.

- [ ] **Step 5: Commit and push the current remote tip**

```bash
git add docs/superpowers/specs/2026-07-31-chat-activity-audit-design.md \
  docs/superpowers/plans/2026-08-01-chat-activity-conversation-groups.md \
  src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html \
  test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs
git commit -m "Group chat activity by conversation"
git fetch origin feature/integrate
git rebase origin/feature/integrate
git push origin HEAD:feature/integrate
```
