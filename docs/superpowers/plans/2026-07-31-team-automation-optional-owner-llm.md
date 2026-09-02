# Team Automation Optional Owner LLM Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decode and review valid Team automation authorization plans that omit the owner LLM selection, while keeping confirmation controls reachable in short viewports.

**Architecture:** Keep protobuf authorization semantics at the `teamAutomationApi` boundary: decode both grant requirements, retain an optional owner LLM selection, and reject inconsistent exact-grant plans. Render the resulting review without inferred identities and use the owning Modal's footer plus a bounded body for confirmation actions.

**Tech Stack:** React 19, TypeScript, Ant Design 6, Jest, Testing Library, pnpm.

---

## File Map

- Modify `apps/aevatar-console-web/src/shared/api/teamAutomationApi.ts`: add typed policy requirements, decode nullable owner LLM selection, and validate plan invariants.
- Modify `apps/aevatar-console-web/src/shared/api/teamAutomationApi.test.ts`: add the exact no-service v2 response and fail-closed invariant tests.
- Modify `apps/aevatar-console-web/src/pages/teams/components/TeamAutomationAuthorizationReview.tsx`: render selected and not-required authorization variants without owning Modal actions.
- Modify `apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.tsx`: move review actions into the Modal footer and bound the review body height.
- Modify `apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.test.tsx`: exercise no-service preflight through confirmed creation and assert footer placement.
- Modify `apps/aevatar-console-web/src/locales/en-US.ts` and `apps/aevatar-console-web/src/locales/zh-CN.ts`: add user-facing no-service and no-owner-LLM copy.

### Task 1: Lock The Nullable Authorization Contract

- [ ] Add a `noServiceAuthorizationResult()` fixture containing `schemaVersion: "scheduled-invocation-authorization/v2"`, numeric grant requirement `2`, `nyxIdServiceGrants: []`, and `ownerLlmSelection: null`.
- [ ] Assert `permissionReview()` returns `status: "ready"`, both requirements as `"not_required"`, empty grants, and `ownerLLMSelection: null`.
- [ ] Assert a null owner LLM selection also remains valid when a non-LLM workflow capability has an exact required service grant.
- [ ] Add rejected cases for an empty grant list marked `required`, a non-empty grant list marked `not_required`, and a credential-level node requirement that disagrees with its service grants.
- [ ] Run `pnpm --dir apps/aevatar-console-web exec jest --runInBand --verbose src/shared/api/teamAutomationApi.test.ts` and verify the no-service case fails at `ownerLlmSelection must be an object` before production edits.

### Task 2: Decode The Plan Without Inventing Identity

- [ ] Introduce one grant requirement normalizer returning `"required" | "not_required"`; use it for policy and service grant fields.
- [ ] Change the review contract to:

```ts
readonly ownerLLMSelection: {
  readonly model: string;
  readonly nyxIdUserServiceId: string;
  readonly routeKind: "gateway" | "nyx_id_user_service";
  readonly routeValue: string;
  readonly serviceSlugSnapshot: string;
} | null;
```

- [ ] Decode `null` or an absent protobuf message as `null`; continue exact route/grant validation when a selection exists.
- [ ] Decode `serviceGrantRequirement` and `nodeGrantRequirement` into `credentialPlan`, then reject inconsistent required/not-required grant sets.
- [ ] Re-run the focused API test and verify green.

### Task 3: Render And Confirm The No-Service Review

- [ ] Add a page test whose mocked review has `ownerLLMSelection: null`, no grants, and both policy requirements `not_required`.
- [ ] Assert the dialog says `No external NyxID service or owner LLM model grant is required`, does not display a fabricated gateway/model, and does not call create before confirmation.
- [ ] Assert `Authorize and continue` belongs to `.ant-modal-footer`, then click it and verify one create request with the preflight digest and policy version.
- [ ] Run `pnpm --dir apps/aevatar-console-web exec jest --runInBand --verbose src/pages/teams/tabs/TeamAutomationsTab.test.tsx` and verify the new behavior fails before UI edits.
- [ ] Render the nullable selection branch, include exact service display-name tags where present, add English and Chinese copy, move actions to the Modal footer, and give only the review body `maxHeight: "min(70vh, 640px)"` with vertical scrolling.
- [ ] Re-run the focused page test and verify green.

### Task 4: Focused Verification And Delivery

- [ ] Run the frontend change-scope analyzer with `--base origin/dev`.
- [ ] Run Jest `--findRelatedTests` for changed production files and explicitly run both changed test files.
- [ ] Run Biome only for analyzer `staticCheckFiles`; do not run a local full typecheck or production build.
- [ ] Run `bash tools/ci/test_stability_guards.sh` because frontend tests changed.
- [ ] Run `git diff --check`, review the complete `origin/dev...HEAD` diff, stage only this task's files, and commit with an imperative message.
- [ ] Push `fix/2026-07-31_nullable-owner-llm-plan` and create a PR to `dev` linked to issue #3002. Record focused commands and delegate the full frontend suite/build to GitHub CI.
