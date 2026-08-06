# Admin Canonical Chat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the `/chat` editing workbench to the complete canonical NyxID Assistant contract used by `nyxid-chat`.

**Architecture:** Keep the existing route and AG-UI presentation. Replace legacy Workflow Chat serialization and scoped history with typed `/api/chat` and `/api/chat/conversations/**`, add a strict actor-state reducer/decoder, and render controls from actor-owned facts. Browser service connection uses the existing NyxID OAuth session directly at the NyxID boundary.

**Tech Stack:** React 19, TypeScript strict, Umi Max, Ant Design, TanStack Query, Jest, Testing Library, existing AG-UI SDK.

## Global Constraints

- Modify only `apps/aevatar-console-web/`.
- Preserve the existing three-column workbench and existing AG-UI message presentation.
- Never send `scopeId`, `sessionId`, or `workflow` in canonical Assistant commands.
- Keep `conversationId`, `turnId`, task/control IDs, action IDs, and `clientRequestId` distinct.
- Controls read only actor-owned pending facts, `availableActions`, and `stateVersion`.
- Browser actions accept only schema v4 `service.connect`; unknown or secret-bearing input fails closed.
- A completed service action reports only a real top-level NyxID UserService `id`, never `api_key_id`, slug, or catalog ID.
- No new dependency, BFF, backend route, polling loop, or duplicate message renderer.
- Follow the frontend incremental testing policy; do not run the complete frontend suite.
- Push without force to `origin/feature/integrate` only after fresh verification and remote readback.

---

### Task 1: Canonical command and conversation adapters

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/chat/chatApi.ts`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatApi.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatHistoryApi.ts`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatHistoryApi.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatTypes.ts`

**Interfaces:**
- Produces `sendChatCommand(command, signal): Promise<Response>` for typed streaming and accepted commands.
- Produces canonical list, transcript, state, and delete methods rooted at `/api/chat/conversations`.

- [ ] Write failing adapter tests asserting the first text body is exactly `{type, prompt, clientRequestId}`, continuation adds only `conversationId`, the idempotency header matches, and no legacy keys exist.
- [ ] Run those named tests and require failures caused by the old body and paths.
- [ ] Implement the minimum typed command union and canonical paths.
- [ ] Run the two adapter test files and require all cases to pass.

### Task 2: Actor state, action schema, and NyxID service boundary

**Files:**
- Create: `apps/aevatar-console-web/src/pages/chat/chatActorState.ts`
- Create: `apps/aevatar-console-web/src/pages/chat/chatActorState.test.ts`
- Create: `apps/aevatar-console-web/src/pages/chat/nyxIdServiceApi.ts`
- Create: `apps/aevatar-console-web/src/pages/chat/nyxIdServiceApi.test.ts`
- Modify: `apps/aevatar-console-web/jest.config.ts` only if the DOM-free files are assigned to the node project.

**Interfaces:**
- Produces strict current-state decode/reduce and `validateActionRequest`.
- Produces `listNyxIdConnectors`, `createNyxIdCatalogKey`, and exact matching helpers.

- [ ] Write failing tests for current/not-modified/reload-required/not-found, monotonic versions/sequences, actor-authored availability, schema-v4 identity validation, unsafe custom URLs, secret rejection, and exact cached-action restoration.
- [ ] Write failing tests proving top-level NyxID `id` is selected while `api_key_id` is ignored and ambiguous inventory never completes an action.
- [ ] Run the named tests and require missing-module failures.
- [ ] Implement the minimum strict decoder/reducer and direct NyxID adapter using existing auth/runtime config.
- [ ] Run the two new test files and require all cases to pass.

### Task 3: Actor controls and browser action UI

**Files:**
- Create: `apps/aevatar-console-web/src/pages/chat/ChatActorControls.tsx`
- Create: `apps/aevatar-console-web/src/pages/chat/ChatActorControls.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/chat/chatPresentation.tsx` to compose the actor controls beneath the existing assistant message presentation without replacing its renderer.
- Modify: `apps/aevatar-console-web/src/locales/projectMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/projectMessages.zh-CN.ts`

**Interfaces:**
- Consumes the Task 2 projection and callbacks.
- Emits exact input, approval, stop, steer, retry, skip, refresh, open/connect, and action-report intents.

- [ ] Write a failing rendered test proving controls appear only from pending facts/available actions, input option IDs are submitted instead of labels, and action completion remains pending without actor proof.
- [ ] Run that file and require failure because the component is absent.
- [ ] Implement the accessible presentational component with localized copy.
- [ ] Run the component test and locale catalog test.

### Task 4: Route integration and legacy deletion

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/chat/index.tsx`
- Modify: `apps/aevatar-console-web/src/pages/chat/index.test.tsx`
- Delete obsolete legacy helpers from `apps/aevatar-console-web/src/pages/chat/chatApi.ts`, `chatHistoryApi.ts`, and route state.

**Interfaces:**
- Consumes Tasks 1-3.
- Produces the complete canonical workbench journey.

- [ ] Add failing route integration cases for typed first turn, authoritative RUN_STARTED identity, transcript/state reopen, input/approval/control dispatch, browser action continuation, and refresh/delete.
- [ ] Run only those named cases and require failures at old request or absent UI behavior.
- [ ] Wire the adapters and controls; delete `sessionId`, nested Workflow conversation intent, create-recovery polling, scoped history, and confirmation-keyword inference.
- [ ] Run the route, component, actor-state, and adapter files together.

### Task 5: Verification and production push

**Files:**
- Review all changed files under `apps/aevatar-console-web/`.

**Interfaces:**
- Produces a verified commit and exact remote readback.

- [ ] Run focused Jest files only, then `pnpm --dir apps/aevatar-console-web tsc`, affected-file Biome lint, and `pnpm --dir apps/aevatar-console-web build`.
- [ ] Run `bash tools/ci/test_stability_guards.sh`, `bash tools/docs/lint.sh`, and `git diff --check`.
- [ ] Review the complete diff for identity, secret, error, loading, retry, and stale-state behavior.
- [ ] Commit with an imperative single-purpose message.
- [ ] Fetch `origin/feature/integrate`; if it moved, rebase without force and rerun the focused tests, type check, build, guards, and diff check on the integrated tree.
- [ ] Push `HEAD:feature/integrate` without force, then require `git ls-remote origin refs/heads/feature/integrate` to equal local `HEAD`.
