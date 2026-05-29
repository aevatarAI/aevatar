---
title: "Frontend Architecture Audit"
date: 2026-05-28
status: complete
---

# Frontend Architecture Audit — 2026-05-28

## Scope

- Scanned only `apps/aevatar-console-web/src/`
- Excluded `*.test.*`, `__tests__/`, `.umi-production/`, and `node_modules/`
- Verified each finding by reading the actual file content
- Cross-referenced against `CLAUDE.md`, `docs/canon/frontend-design.md`, and `docs/canon/cqrs-projection.md`

## Summary

- CRITICAL: 1
- HIGH: 7
- MEDIUM: 6
- LOW: 3

## Issues Found

### [CRITICAL] frontend-audit-001 — Non-streaming invoke receipts treated as completed run state

- **Category:** CQRS UI State Model
- **Files:**
  - `apps/aevatar-console-web/src/pages/scopes/invoke.tsx` line 977
  - `apps/aevatar-console-web/src/pages/studio/components/StudioMemberInvokePanel.tsx` line 122
- **Rule:** `docs/canon/frontend-design.md` §4.1 / `CLAUDE.md` ACK honesty / `docs/canon/cqrs-projection.md` §4.1
- **Description:** `invokeEndpoint(...)` POST receipt is immediately presented as `status: 'success'` / `'Succeeded'`. Per CQRS rules, an HTTP 200 or accepted receipt can only enter `Accepted` or `Running`, not `Completed`/`Succeeded`. The state model in these files uses `'idle' | 'running' | 'success' | 'error'`, collapsing Accepted, Running, Streaming, Observed, and Completed into a single `success` state.
- **Evidence:** `invoke.tsx:977` sets `status: 'success'` directly from POST receipt. `StudioMemberInvokePanel.tsx:122` maps `'success'` to `'Succeeded'` via `getRunStatusLabel()`.
- **Fix direction:** After invokeEndpoint POST success, set accepted/running state. Only show succeeded when terminal observation or readmodel proves completion. Expand state union to include Accepted, Streaming, Paused, Observed, StillProcessing per design baseline §4.1.

### [HIGH] frontend-audit-002 — Systemic simplified state model across 10+ files

- **Category:** CQRS UI State Model
- **Files:**
  - `apps/aevatar-console-web/src/pages/chat/chatTypes.ts` line 107
  - `apps/aevatar-console-web/src/pages/gagents/index.tsx` line 79
  - `apps/aevatar-console-web/src/pages/scopes/invoke.tsx` line 98
  - `apps/aevatar-console-web/src/pages/studio/components/StudioBuildPanels.tsx` line 472
  - `apps/aevatar-console-web/src/pages/studio/components/StudioMemberInvokePanel.currentRun.ts` lines 23, 62
  - `apps/aevatar-console-web/src/pages/studio/components/bind/StudioMemberBindPanel.tsx` line 97
  - `apps/aevatar-console-web/src/pages/chat/chatAdvancedConsole.tsx` line 228
  - `apps/aevatar-console-web/src/pages/teams/components/TeamTestPanel.tsx` line 42
  - `apps/aevatar-console-web/src/pages/runs/components/RunsStatusStrip.tsx` line 21
  - `apps/aevatar-console-web/src/shared/studio/observeSession.ts` line 22
- **Rule:** `docs/canon/frontend-design.md` §4.1
- **Description:** At least 10 files across the codebase use `"idle" | "running" | "success" | "error"` (or close variants) as their status model. This collapses the required CQRS states (Accepted, Running, Streaming, Paused, Observed, Completed, StillProcessing, Failed) into just 4 states. The `success` state conflates command receipt, observation completion, and readmodel materialization.
- **Evidence:** `chatTypes.ts:107` -- `status: "idle" | "running" | "success" | "error"`. `gagents/index.tsx:79` -- `status: 'idle' | 'running' | 'success' | 'error'`. `StudioMemberInvokePanel.currentRun.ts:62` -- `status: 'running' | 'success' | 'error' | 'cancelled'`.
- **Fix direction:** Create a shared CQRS UI state enum in `shared/models/` with all required states from frontend-design.md §4.1. Migrate all files to use the shared enum.

### [HIGH] frontend-audit-003 — Incomplete CQRS state model in execution trace

- **Category:** CQRS UI State Model
- **Files:**
  - `apps/aevatar-console-web/src/shared/studio/execution.ts` line 22
- **Rule:** `docs/canon/frontend-design.md` §4.1
- **Description:** `StepExecutionState.status` typed as `'idle' | 'active' | 'waiting' | 'completed' | 'failed'`. Missing Accepted, Streaming, Paused, Observed, StillProcessing states.
- **Evidence:** Line 22: `status: 'idle' | 'active' | 'waiting' | 'completed' | 'failed';`
- **Fix direction:** Expand status union to include Accepted, Streaming, Paused, Observed, StillProcessing per design baseline §4.1.

### [HIGH] frontend-audit-004 — Glassmorphism / backdrop-filter usage in 6 files

- **Category:** Design Compliance
- **Files:**
  - `apps/aevatar-console-web/src/pages/chat/index.tsx` line 2018
  - `apps/aevatar-console-web/src/pages/MissionControl/index.tsx` line 252
  - `apps/aevatar-console-web/src/pages/MissionControl/TopologyCanvas.tsx` line 517
  - `apps/aevatar-console-web/src/pages/runs/index.tsx` line 149
  - `apps/aevatar-console-web/src/pages/runs/components/RunsStatusStrip.tsx` line 29
  - `apps/aevatar-console-web/src/pages/runs/runWorkbenchConfig.tsx` line 374
- **Rule:** `docs/canon/frontend-design.md` §5.3
- **Description:** Six files in pages/ use `backdropFilter: "blur()"` for glassmorphism effects. Design baseline explicitly lists glassmorphism as a forbidden pattern: "为了'看起来现代'而堆砌玻璃态、发光和悬浮阴影".
- **Evidence:** `chat/index.tsx:2018` -- `backdropFilter: "blur(10px)"`. `MissionControl/index.tsx:252` -- `backdropFilter: 'blur(12px)'`. `runs/index.tsx:149` -- `backdropFilter: "blur(10px)"`.
- **Fix direction:** Remove backdrop-filter blur effects. Use solid or semi-transparent backgrounds without blur.

### [HIGH] frontend-audit-005 — Purple gradient pattern (forbidden "紫白渐变默认主题")

- **Category:** Design Compliance
- **Files:**
  - `apps/aevatar-console-web/src/shared/agui/runtimeConversationPresentation.tsx` lines 454, 740
  - `apps/aevatar-console-web/src/pages/chat/chatPresentation.tsx` line 1216
  - `apps/aevatar-console-web/src/pages/studio/components/StudioFilesDetailPane.tsx` line 1404
  - `apps/aevatar-console-web/src/pages/workflows/WorkflowYamlViewer.tsx` line 198
- **Rule:** `docs/canon/frontend-design.md` §5.3
- **Description:** Four files use purple-to-indigo gradients (#8b5cf6 to #4f46e5) or hardcoded purple colors (#8b5cf6, #7c3aed). This is the "紫白渐变默认主题" explicitly forbidden by the design baseline.
- **Evidence:** `runtimeConversationPresentation.tsx:454` -- `background: "linear-gradient(135deg, #8b5cf6 0%, #4f46e5 100%)"`. `chatPresentation.tsx:1216` -- same gradient. `StudioFilesDetailPane.tsx:1404` -- `color: '#8b5cf6'`.
- **Fix direction:** Replace purple gradients with theme token-based colors. Use `token.colorPrimary` or CSS variables.

### [HIGH] frontend-audit-006 — Hardcoded gradient defaults across 17 page files

- **Category:** Design Compliance
- **Files:**
  - `apps/aevatar-console-web/src/pages/actors/index.tsx` lines 1749, 1918
  - `apps/aevatar-console-web/src/pages/chat/chatPresentation.tsx` line 625
  - `apps/aevatar-console-web/src/pages/Deployments/index.tsx` line 537
  - `apps/aevatar-console-web/src/pages/governance/components/GovernanceQueryCard.tsx` line 123
  - `apps/aevatar-console-web/src/pages/governance/components/GovernanceResultPanels.tsx` line 56
  - `apps/aevatar-console-web/src/pages/governance/components/GovernanceWorkbench.tsx` line 1902
  - `apps/aevatar-console-web/src/pages/login/index.tsx` line 24
  - `apps/aevatar-console-web/src/pages/MissionControl/index.tsx` line 92
  - `apps/aevatar-console-web/src/pages/MissionControl/TopologyCanvas.tsx` line 443
  - `apps/aevatar-console-web/src/pages/runs/index.tsx` lines 151, 170, 193, 208, 219, 299, 375, 399
  - `apps/aevatar-console-web/src/pages/runs/runWorkbenchConfig.tsx` lines 362, 525
  - `apps/aevatar-console-web/src/pages/services/components/ServiceQueryCard.tsx` line 42
  - `apps/aevatar-console-web/src/pages/services/index.tsx` lines 114, 298
  - `apps/aevatar-console-web/src/pages/settings/shared.tsx` line 26
  - `apps/aevatar-console-web/src/pages/studio/components/StudioShell.tsx` lines 80, 613
  - `apps/aevatar-console-web/src/pages/teams/tabs/TeamMembersTab.tsx` line 164
  - `apps/aevatar-console-web/src/pages/workflows/WorkflowYamlViewer.tsx` line 32
- **Rule:** `docs/canon/frontend-design.md` §5.3
- **Description:** 17 page files contain hardcoded `linear-gradient` or `radial-gradient` values using raw rgba/hex colors. Design baseline warns against "千篇一律的 SaaS 卡片墙" and requires token-based styling. The `runs/index.tsx` file alone has 8 separate gradient definitions.
- **Evidence:** `runs/index.tsx:151` -- `"linear-gradient(180deg, rgba(248, 250, 252, 0.9) 0%, rgba(255, 255, 255, 0.78) 100%)"`. `StudioShell.tsx:80` -- `'linear-gradient(180deg, rgba(255, 253, 249, 0.98) 0%, rgba(249, 245, 237, 0.98) 100%)'`.
- **Fix direction:** Extract gradient definitions to CSS variables or theme tokens. Use `token.colorBgLayout`/`token.colorBgContainer` for background transitions.

### [HIGH] frontend-audit-007 — Massive monolithic component files

- **Category:** Component Quality
- **Files:**
  - `apps/aevatar-console-web/src/pages/studio/index.tsx` — 10,172 lines (StudioPage function)
  - `apps/aevatar-console-web/src/pages/gagents/index.tsx` — 2,787 lines (GAgentsPage: 2,387 lines)
  - `apps/aevatar-console-web/src/pages/chat/index.tsx` — 2,328 lines (ChatPage: 2,005 lines)
  - `apps/aevatar-console-web/src/pages/runs/index.tsx` — 2,414 lines (RunsPage: 1,909 lines)
  - `apps/aevatar-console-web/src/pages/actors/index.tsx` — 1,974 lines (TopologyExplorerPage: 1,386 lines)
  - `apps/aevatar-console-web/src/pages/teams/detail.tsx` — 1,561 lines (TeamDetailPage: 1,194 lines)
  - `apps/aevatar-console-web/src/pages/services/index.tsx` — 1,061 lines (ServicesPage: 611 lines)
- **Rule:** Component quality / `docs/canon/frontend-design.md` §9
- **Description:** Seven page components exceed 1,000 lines. `studio/index.tsx` at 10,172 lines with 135 useMemo/useCallback hooks is an extreme monolith. `gagents/index.tsx` has a single exported function spanning 2,387 lines.
- **Evidence:** `wc -l` returns 10172 for studio/index.tsx, 2787 for gagents/index.tsx, 2328 for chat/index.tsx, 2414 for runs/index.tsx.
- **Fix direction:** Extract sub-components, custom hooks, and business logic into focused modules. Target <500 lines per component file.

### [MEDIUM] frontend-audit-008 — Dead code: unused exported components

- **Category:** Dead Code
- **Files:**
  - `apps/aevatar-console-web/src/shared/ui/ConsoleMetricCard.tsx`
  - `apps/aevatar-console-web/src/shared/ui/AevatarAppFlowGuide.tsx`
  - `apps/aevatar-console-web/src/pages/workflows/WorkflowYamlViewer.tsx`
- **Rule:** Dead code cleanup
- **Description:** Three components are exported but never imported in any non-test source file. `ConsoleMetricCard` and `AevatarAppFlowGuide` are in shared/ui/ but have zero consumers. `WorkflowYamlViewer` is in pages/workflows/ but is not wired into any route or parent component.
- **Evidence:** `grep -rn "import.*ConsoleMetricCard"` returns zero results outside the definition file. Same for `AevatarAppFlowGuide` and `WorkflowYamlViewer`.
- **Fix direction:** Delete these unused components or wire them into consumers.

### [MEDIUM] frontend-audit-009 — Hardcoded color values bypassing design tokens (952+ instances)

- **Category:** Design Compliance
- **Files:**
  - `apps/aevatar-console-web/src/pages/` — 802 instances of raw rgba/hex colors
  - `apps/aevatar-console-web/src/shared/` — 150 instances of raw rgba/hex colors
- **Rule:** `docs/canon/frontend-design.md` §6.1 / `CLAUDE.md` 前端改动优先抽取 design tokens
- **Description:** Over 950 instances of hardcoded color values (rgba(), #hex) in pages/ and shared/ directories, bypassing CSS variables and theme tokens. Design baseline §6.1 requires colors to be "CSS variables / theme tokens / 可复用样式原语".
- **Evidence:** `grep -rn "rgba\([0-9]\|#[0-9a-fA-F]" apps/aevatar-console-web/src/pages/` returns 802 matches.
- **Fix direction:** Extract colors to CSS variables or Ant Design theme tokens. Use `token.color*` references.

### [MEDIUM] frontend-audit-010 — Hardcoded spacing values (975+ instances)

- **Category:** Design Compliance
- **Files:**
  - `apps/aevatar-console-web/src/pages/` — 975 instances of hardcoded padding/margin/gap values
- **Rule:** `docs/canon/frontend-design.md` §6.1 / `CLAUDE.md` 前端改动优先抽取 design tokens
- **Description:** Over 975 instances of hardcoded numeric spacing values (`padding: 12`, `margin: 16`, `gap: 8`) not derived from tokens or CSS variables.
- **Evidence:** `grep -rn "padding:\s*[0-9]\|margin:\s*[0-9]\|gap:\s*[0-9]" apps/aevatar-console-web/src/pages/` (excluding token/var references) returns 975 matches.
- **Fix direction:** Define spacing tokens in CSS variables and reference them consistently.

### [MEDIUM] frontend-audit-011 — Missing key props on .map() JSX elements (63 instances)

- **Category:** Component Quality
- **Files:**
  - 63 instances across `apps/aevatar-console-web/src/pages/` and `apps/aevatar-console-web/src/pages/studio/`
- **Rule:** React rendering best practices
- **Description:** 63 `.map()` calls produce JSX elements without `key` props. This causes React reconciliation warnings and potential rendering bugs.
- **Evidence:** Automated scan found 63 `.map()` calls where the returned JSX lacks a `key=` prop within the next 300 characters.
- **Fix direction:** Add stable `key` props to all mapped JSX elements.

### [MEDIUM] frontend-audit-012 — Extensive inline style objects created in render (1,356+ instances)

- **Category:** Component Quality
- **Files:**
  - `apps/aevatar-console-web/src/pages/` — 1,356 inline `style={{}}` instances
  - `apps/aevatar-console-web/src/shared/` — 108 inline `style={{}}` instances
- **Rule:** Component quality
- **Description:** Over 1,400 inline style objects created during render. These create new object references on every render cycle, defeating React.memo and causing unnecessary re-renders.
- **Evidence:** `grep -rn "style={{" apps/aevatar-console-web/src/pages --include="*.tsx" | wc -l` returns 1356.
- **Fix direction:** Extract inline styles to CSS modules, styled components, or memoized constants outside the component.

### [MEDIUM] frontend-audit-013 — Inconsistent state model across presentation files

- **Category:** CQRS UI State Model
- **Files:**
  - `apps/aevatar-console-web/src/shared/playground/stepSummary.ts` line 124
  - `apps/aevatar-console-web/src/pages/runs/runEventPresentation.ts` line 65
  - `apps/aevatar-console-web/src/pages/MissionControl/presentation.tsx` line 94
  - `apps/aevatar-console-web/src/pages/MissionControl/models.ts` line 106
- **Rule:** `docs/canon/frontend-design.md` §4.1
- **Description:** Different files define different status vocabularies for the same domain concepts. `stepSummary.ts` uses `"Running" | "Completed" | "Failed"`, `MissionControl/models.ts` uses `'queued' | 'running' | 'completed' | 'failed'`, and `chatTypes.ts` uses `"idle" | "running" | "success" | "error"`. No shared state model enum exists.
- **Fix direction:** Create a shared CQRS state enum in `shared/models/` and use consistently across all presentation files.

### [LOW] frontend-audit-014 — Font fallback includes forbidden fonts

- **Category:** Design Compliance
- **Files:**
  - `apps/aevatar-console-web/src/global.less` line 49
- **Rule:** `docs/canon/frontend-design.md` §6.2
- **Description:** Font-family declaration includes Roboto and Arial in fallback stack after AlibabaSans. While AlibabaSans is correctly primary, the forbidden fonts appear in the fallback chain.
- **Evidence:** `global.less:49` -- `AlibabaSans, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, 'Noto Sans', ...`
- **Fix direction:** Consider removing Roboto and Arial from the fallback stack, or document why they are acceptable as system fallbacks.

### [LOW] frontend-audit-015 — outline:none removes keyboard focus visibility

- **Category:** Design Compliance
- **Files:**
  - `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx` line 69
- **Rule:** `docs/canon/frontend-design.md` §7 / keyboard accessibility
- **Description:** `outline: none !important` removes keyboard focus visibility on graph canvas nodes. Design baseline requires "keyboard focus 必须可见".
- **Evidence:** `GraphCanvas.tsx:69` -- `outline: none !important;` in CSS string for `.react-flow__node` selection class.
- **Fix direction:** Replace `outline: none` with a visible custom focus style that maintains accessibility.

### [LOW] frontend-audit-016 — Missing test coverage for major page components

- **Category:** Test Coverage
- **Files:**
  - `apps/aevatar-console-web/src/pages/MissionControl/` — 6 source files, 0 tests
  - `apps/aevatar-console-web/src/pages/governance/policies.tsx`, `audit.tsx`, `endpoints.tsx` — 0 tests
  - `apps/aevatar-console-web/src/pages/studio/explorer/` — 5 source files, 0 tests
  - 40+ page .tsx files without corresponding .test.tsx files
- **Rule:** Test coverage
- **Description:** 78 page .tsx files exist but only 58 page test files. Major components like MissionControl (6 files), governance sub-pages, and studio explorer (5 files) have zero test coverage. Only 9 module .tsx files have 6 test files.
- **Fix direction:** Prioritize test coverage for MissionControl, governance, and studio explorer components.

## Processing Results

Full audit completed. All findings verified against actual source files.
