# Frontend Test Suite Risk Audit

## Scope and Method

This audit applies the risk-based rules in `docs/testing-policy.md` to the
current `dev` inventory of `src/**/*.test.{ts,tsx}`. Commit `4a7d49f3c` carries
the policy and is an ancestor of the audited branch.

The inventory contains 138 test files. Exactly 38 files exceed 300 lines at
the audited commit. The review considered test names, fixture boundaries,
repeated setup, mock and collaborator-call assertions, observable assertions,
and overlap with adjacent test files. Line count and mock count were used only
as review triggers, not as deletion criteria.

This pull request performs one bounded migration in the Studio GAgent catalog
discovery and published-revision identity domain. The old route-level case
`loads discovered GAgent types and the published service revision catalog`
asserted only that two mocked collaborators were called. It would fail after an
internal data-loading refactor even when the product behavior was unchanged,
while still passing if the returned catalog data never reached the operator.

The migration replaces that weak evidence with three observable contracts:

- The Studio route now renders the real `StudioGAgentBuildPanel` while mocking
  only the heavy script-editor boundary. A discovered `Orders Assistant` kind
  must reach the GAgent selector and the real definition fields must render.
- A focused component integration case selects `Billing Assistant` through the
  real selector, continues to Bind, and observes that the handoff retains
  `Tests.BillingGAgent` rather than the previously selected kind.
- The initial Team member rail must render the published workflow name and the
  exact service/revision identity for `billing-api · rev-billing-7`, rather
  than merely proving that a revision API was invoked.

The workflow, Script, and GAgent service-revision fixtures are consolidated in
`tests/studioServiceCatalogFixtures.ts` so future behavior-domain extraction
does not duplicate the oversized route fixture block. No production code
changes are made.

## Priority Behavior and Risk Inventory

### `src/pages/studio/index.test.tsx`

| Behavior domain | Concrete risk | Highest-value evidence | Review outcome |
| --- | --- | --- | --- |
| Route and identity canonicalization | A service, member, workflow, or legacy query identity is sent to the wrong authority or survives in a canonical URL | Component or route integration | Retain the distinct initial-load, route-change, and missing-target cases; replace the duplicate bootstrap call-only case with visible service/revision identity evidence |
| Team member inventory and entry selection | Cross-Team members leak into the rail or the wrong member becomes the Team entry | Component or route integration | Retain; assertions act through the rendered rail and entry action |
| Workflow, Script, and GAgent authoring handoffs | The selected implementation or unsaved draft is lost across Build and Bind | Component or route integration | Split later by implementation behavior when shared setup can be extracted without recreating the mock graph |
| Bind observation and published contract selection | A pending or stale binding is presented as callable, or the scope default replaces the selected member | Component or route integration | Retain; these are high-risk identity and eventual-consistency rules |
| Invoke and Observe handoffs | Runs are started or stopped for the wrong member, service, or run | Component or route integration | Retain; adapter request-shape checks are product identity contracts here |
| Auth recovery | Studio loops through login or loads protected state before session recovery settles | Component or route integration | Retain locally; real redirect and provider acceptance remain a browser smoke gap |

The file contains several independent behavior domains and oversized shared
fixtures. Its outcome is **split by behavior domain**, but only after a shared
route render kit can preserve realistic internal collaboration. Mechanical
file splitting would duplicate more than 3,000 lines of setup and increase the
maintenance risk this audit is intended to reduce.

### `src/pages/team-member-workflow-studio/index.test.tsx`

| Behavior domain | Concrete risk | Highest-value evidence | Review outcome |
| --- | --- | --- | --- |
| New member and draft materialization | A retry creates duplicate members or links the wrong workflow draft | Component or route integration | Retain; identity fixtures are deliberately distinct |
| Route draft recovery | Member, workflow, and published-service identities are substituted for one another | Component or route integration | Retain and split from editor interaction coverage |
| Graph and YAML editing | Graph, YAML, and dirty-state buffers diverge or stale async work overwrites newer edits | Component integration, with pure document rules in `shared/studio/document.test.ts` | Retain integration cases; move only deterministic transformation duplication to the helper suite |
| Guided node configuration | Typed and raw parameter editors write different runtime shapes | Component integration | Retain; it protects the editor-to-document boundary |
| Draft execution and streaming logs | Unsaved input/files are omitted or stream frames produce the wrong visible result | Component integration | Retain; real SSE transport and browser streaming remain a browser smoke gap |
| Publish and rebind observation | Pending, failed, stale, or already-published facts expose the wrong action | Component or route integration | Retain and split as a publish-observation behavior domain |

The outcome is **split by behavior domain**. The most valuable boundaries are
route identity, editor synchronization, draft execution, and publish
observation. Pointer resizing and native leave-prompt behavior cannot be fully
proven by jsdom and remain browser gaps.

### `src/pages/teams/detail.test.tsx`

| Behavior domain | Concrete risk | Highest-value evidence | Review outcome |
| --- | --- | --- | --- |
| Team shell and navigation | Scope or Team context is lost across tabs and breadcrumbs | Component or route integration | Retain |
| Roster, entry member, and deletion | Accepted commands are shown as completed before the read model confirms them | Component or route integration | Retain; these cases protect destructive and eventual-consistency behavior |
| Team Test | A test invokes the selected row rather than the authoritative entry member, or runs while entry state is stale | Component or route integration | Retain and split as an independent Team Test domain |
| Member workflow handoffs | Route hints override authoritative member, workflow, or service identities | Component or route integration | Retain |
| Member automation handoffs | Query hints become owner identity or cross-Team members are queried | Component or route integration | Retain |
| Rename, archive, and projection recovery | An accepted update is reverted by a stale refresh or a transient 404 becomes data loss | Component or route integration | Retain |

The outcome is **split by behavior domain**. The responsive row-action case is
useful as a layout regression signal, but actual clipping and Team Test
streaming across deployed services remain browser smoke gaps.

### `src/shared/studio/api.test.ts`

| Behavior domain | Concrete risk | Highest-value evidence | Review outcome |
| --- | --- | --- | --- |
| Authenticated host requests and errors | Bearer auth, problem details, or compact HTML errors are adapted incorrectly | Module or adapter integration | Retain |
| Workflow draft and published fallback | Scope is omitted or a missing draft hides a committed workflow | Module or adapter integration | Retain and split by workflow persistence behavior |
| Binding contracts | Member and scope endpoints or stable workflow identities are confused | Module or adapter integration | Retain |
| Team and member commands | Typed identities, explicit nulls, or accepted command responses are lost during adaptation | Module or adapter integration | Retain and split by authority resource |
| Workflow board mapping | Nullable totals and current-state snapshot fields are decoded incorrectly | Module or adapter integration | Retain |

The outcome is **split by adapter behavior domain**. Fetch is the external
boundary; internal decoders and request builders remain real. The suite should
not be converted into route tests, and its request-shape assertions are
observable transport contracts rather than incidental call forwarding.

### `src/pages/studio/components/StudioBuildPanels.test.tsx`

| Behavior domain | Concrete risk | Highest-value evidence | Review outcome |
| --- | --- | --- | --- |
| Script authoring and save observation | Unsaved packages are lost or pending/rejected saves enable Bind | Component integration | Retain and split as Script authoring |
| Workflow prompt and node editing | User instructions are written to the wrong runtime parameter or omitted from save/run | Component integration | Retain; pure mapping overlap belongs in document-helper tests |
| Diagnostics and mutation locks | Invalid JSON or in-flight mutations allow Apply, Save, or Run | Component integration | Retain |
| Draft-run output | Metadata or intermediate frames replace the final product output | Component integration | Retain |
| GAgent discovery and handoff | A discovered kind never reaches Build, or selecting a different kind still hands the stale kind to Bind | Route plus component integration | Cover discovery through the real route-owned panel and selection through the real selector |
| GAgent draft-run recovery | Provider failure escapes Build without a retry path | Component integration | Retain |

The outcome is **split by behavior domain**. The legacy-surface deletion guard
is a temporary architecture assertion and should be removed with that surface,
not expanded. Real provider routing remains a browser or deployed smoke gap.

## Review Outcomes for Every File Above 300 Lines

| File | Lines | Recorded outcome and rationale |
| --- | ---: | --- |
| `src/pages/studio/index.test.tsx` | 8,617 | **Split by behavior domain.** Replace duplicate bootstrap call-only coverage with observable catalog/revision behavior now; later isolate route identity, inventory, authoring, Bind, Invoke, and Observe around a shared render kit. |
| `src/pages/team-member-workflow-studio/index.test.tsx` | 7,559 | **Split by behavior domain.** Route identity, editor synchronization, draft execution, and publish observation have independent fixtures and risks. |
| `src/pages/teams/detail.test.tsx` | 3,219 | **Split by behavior domain.** Roster mutation, Team Test, handoffs, and Team administration are distinct route behaviors. |
| `src/shared/studio/api.test.ts` | 2,997 | **Split by behavior domain.** Keep module/adapter integration and separate workflow, binding, Team, member, and board contracts. |
| `src/pages/chat/index.test.tsx` | 1,991 | **Split by behavior domain.** Conversation recovery, streaming, history, and Studio handoff risks should not share one route fixture indefinitely. |
| `src/pages/studio/components/StudioMemberInvokePanel.test.tsx` | 1,867 | **Split by behavior domain.** Separate chat streaming, structured invoke, attachments, run history, and failure recovery. |
| `src/pages/studio/components/StudioBuildPanels.test.tsx` | 1,796 | **Split by behavior domain.** Separate Script authoring, Workflow editing, validation, and draft-run output. |
| `src/pages/settings/index.test.tsx` | 1,246 | **Reduce duplicated setup, then split.** Exact-service observation races are valuable but share large catalog and async fixtures. |
| `src/pages/studio/components/bind/StudioMemberBindPanel.test.tsx` | 1,227 | **Retain with rationale.** Thirteen cases cover distinct contract states; extract fixture builders rather than split mechanically. |
| `src/pages/scopes/invoke.test.tsx` | 1,084 | **Reduce repeated fixtures.** Eight route-integration cases cover distinct chat, typed invoke, reset, and runtime handoff behavior. |
| `src/pages/MissionWall/index.test.tsx` | 1,067 | **Retain with rationale.** Snapshot freshness, focus stability, and authoritative workflow-board rendering are distinct fullscreen behaviors. |
| `src/pages/teams/home.test.tsx` | 1,052 | **Split by behavior domain.** Team inventory, entry-member runtime sampling, archive filtering, and projection recovery are independent. |
| `src/pages/runs/index.test.tsx` | 992 | **Split by behavior domain.** Launch setup, run restoration, streaming retry, and human interaction are independent route risks. |
| `src/locales/hardcodedCopyAudit.test.ts` | 917 | **Retain with rationale.** Length comes from deterministic source fixtures for one repository-wide i18n invariant, with no mock graph. |
| `src/shared/api/scheduledDispatchApi.test.ts` | 888 | **Split by behavior domain.** Listing, mutation encoding, retry, validation, and action routes are separate adapter contracts. |
| `src/pages/settings/nyxIdRelayLlm.test.ts` | 866 | **Reduce duplicated permutations.** Preserve distinct exact-identity, stale observation, race, timeout, and fail-closed risks. |
| `src/pages/teams/tabs/TeamAutomationsTab.test.tsx` | 840 | **Split by behavior domain.** Admission review, create/update, credential recovery, and revocation observation are independent workflows. |
| `src/shared/api/runtimeRunsApi.test.ts` | 771 | **Split by endpoint behavior.** Keep request-shape coverage because member, Team, service, draft, and typed invoke routes are public contracts. |
| `src/pages/chat/chatApi.test.ts` | 692 | **Retain with rationale.** SSE parsing, continuation recovery, cancellation, and structured frame extraction are distinct adapter risks. |
| `src/shared/api/scopeRuntimeApi.test.ts` | 689 | **Retain with rationale.** Eleven concise adapter cases cover distinct service, binding, endpoint, run, and retirement contracts. |
| `src/shared/api/teamAutomationApi.test.ts` | 681 | **Split by behavior domain.** Authorization decoding, fail-closed admission, owner-scoped schedules, and revocation are independent. |
| `src/pages/Deployments/index.test.tsx` | 662 | **Split by behavior domain.** Inventory states, handoff routing, rollout control, and evidence transitions are independent route risks. |
| `src/pages/gagents/index.test.tsx` | 626 | **Reduce repeated setup.** Six high-value route cases protect discovery, draft run, binding replacement, activation, and retirement. |
| `src/pages/studio/components/StudioFilesPage.test.tsx` | 612 | **Split by behavior domain.** Catalog browsing, chat-history deletion, and Chrono file editing have independent boundaries. |
| `src/shared/studio/document.test.ts` | 594 | **Retain with rationale.** Pure document transformations are correctly placed at the unit layer and protect distinct graph invariants. |
| `src/shared/auth/client.test.ts` | 547 | **Retain with rationale.** OAuth initiation, callback, service-access review, refresh, and stale-storage races are distinct adapter behaviors. |
| `src/pages/runtime-published-runs/index.test.tsx` | 503 | **Retain with rationale.** Seven cases cover loading, canonical routing, schedule filtering, materialization, and back navigation. |
| `src/pages/team-member-invoke/index.test.tsx` | 430 | **Retain with rationale.** Seven route cases enforce distinct member/workflow/service identity and binding eligibility rules. |
| `src/shared/studio/nodeConfigFields.structured.test.ts` | 411 | **Retain with rationale.** Pure structured-editor mappings and validation belong at the unit layer and use no mock graph. |
| `src/pages/Deployments/releaseEvidence.test.ts` | 409 | **Retain with rationale.** Pure evidence state transitions are correctly placed and cover distinct rollout/deactivate/rollback states. |
| `src/shared/api/runtimeGAgentApi.test.ts` | 388 | **Retain with rationale.** Nine adapter cases cover discovery, saved actors, draft runs, binding, activation, and retirement contracts. |
| `src/adminObservatoryGraph.test.ts` | 380 | **Retain with rationale.** Two dense pure graph cases share one topology fixture and protect edge preservation and interaction layout. |
| `src/pages/governance/components/GovernanceInspectorDrawer.test.tsx` | 367 | **Reduce repeated mock setup.** Six component cases protect distinct create, update, retired-state, and exposure behavior. |
| `src/shared/navigation/teamRoutes.test.ts` | 356 | **Retain with rationale.** Pure route builders/parsers encode high-risk identity separation across distinct canonical routes. |
| `src/shared/graphs/GraphCanvas.test.tsx` | 322 | **Retain with rationale.** Seven component cases cover deletion ownership, labels, viewport, positions, and connection handles. |
| `src/pages/studio/components/StudioExecutionPage.test.tsx` | 321 | **Retain with rationale.** Six component cases cover runtime context, approval, signals, script downgrade, and empty state. |
| `src/pages/runs/components/RunsLaunchRail.test.tsx` | 309 | **Reduce oversized fixtures.** Four observable layout and disclosure cases do not need the current mock volume. |
| `src/pages/chat/chatHistoryApi.test.ts` | 308 | **Retain with rationale.** Eight adapter cases cover pagination, recovery identity, decoding, empty data, malformed data, deletion, and errors. |

## Residual Coverage Gaps

- OAuth redirect origin, provider callback acceptance, and cross-origin session
  recovery require browser smoke coverage.
- Native browser history, leave prompts, pointer resizing, responsive clipping,
  and focus behavior are only approximated by jsdom.
- Real SSE framing, cancellation, multipart upload, and deployed Team/member
  routing require a focused browser or deployed integration check.
- Backend projection timing and binding materialization are represented with
  deterministic controlled responses; the suite does not prove production
  timing or infrastructure wiring.

These gaps are recorded rather than replaced with additional mock-heavy unit
tests. No browser, end-to-end, integration, smoke, or full frontend suite is
authorized or run by this audit.
