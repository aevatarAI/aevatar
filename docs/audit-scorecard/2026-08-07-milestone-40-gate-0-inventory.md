---
title: "Milestone 40 Gate 0 Baseline and Evidence Inventory"
status: superseded
owner: eanzhao
---

# Milestone 40 Gate 0 Baseline and Evidence Inventory

> **Superseded (2026-08-10).** This is the Gate-0 planning snapshot recaptured on 2026-08-08; its section-5 classifications describe the state *at capture time*, not current status. Milestone 40 has since closed 30/30: the machine-readable conformance authority required by [#3313](https://github.com/AevatarAI/aevatar/issues/3313) is checked in at [`docs/contracts/nyxid-assistant-conformance/v1/`](../contracts/nyxid-assistant-conformance/v1/sources.json) (superseding this inventory per its own exit plan and refuting the `missing` row below), and [#3318](https://github.com/AevatarAI/aevatar/issues/3318) closed with final authenticated UC1a-UC4 production evidence anchored to `origin/feature/integrate@7b3dee82e`. This document is retained as a historical record only.

Recaptured on 2026-08-08 for [Milestone 40](https://github.com/AevatarAI/aevatar/milestone/40). This inventory uses the production branch selected by [#3317](https://github.com/AevatarAI/aevatar/issues/3317). Source, deployment, canary, and issue-closure evidence all attach to exact `origin/feature/integrate` revisions.

## 1. Binding baseline

[Issue #3317](https://github.com/AevatarAI/aevatar/issues/3317) makes `origin/feature/integrate` the only Milestone 40 delivery, production, and acceptance baseline:

| Evidence | Revision | Meaning |
|---|---|---|
| Frozen Gate 0 baseline | `origin/feature/integrate@6cf0da4cc53311e27dcc29887b60b330587bcf3c` | Inventory point for classifying every attached issue |
| Principal implementation | [`53e20f9ba2bc6f883a887615a3e015ce4eac3caa`](https://github.com/AevatarAI/aevatar/commit/53e20f9ba2bc6f883a887615a3e015ce4eac3caa) | Task-plan, action registry, projection, and Studio source |
| Wave 0 source | [`6a32bc32d`](https://github.com/AevatarAI/aevatar/commit/6a32bc32d), [`080bb5c28`](https://github.com/AevatarAI/aevatar/commit/080bb5c28), [`cce255a03`](https://github.com/AevatarAI/aevatar/commit/cce255a03), [`e8c5ccc94`](https://github.com/AevatarAI/aevatar/commit/e8c5ccc94) | Completion reads, closed catalog, delegated refresh, and admin-only exposure correction already on the production branch |

The frozen SHA is not a permanent branch lock. Every subsequent feature records its own exact integration commit, waits for that short-SHA image, and verifies that deployed image before its acceptance issue closes. No second branch participates in Milestone 40 acceptance.

## 2. Pinned external evidence

These identifiers freeze the evidence inspected for this inventory. They are inputs to the future repository-owned conformance manifest in [#3313](https://github.com/AevatarAI/aevatar/issues/3313); this Markdown document is not that manifest.

| Artifact | Pinned revision or digest |
|---|---|
| NyxID repository revision | `fa157bc4160c27922f49f8f498ccac755843a15a` |
| NyxID source tree OID | `2d597bda65f5a6ff6903c888c509756ba87807e9` |
| NyxID CLI tree OID | `17ed291072a8543dc323195a46ed8db80c2c5684` |
| NyxID assistant registry source blob | `a0ad19dddf615445a1d2a16ee5ceeb961c683389` |
| NyxID assistant registry source SHA-256 | `35650ae260404dc291e53e9efdbc4615833a67821d19dd727f45f16a9ffce372` |
| NyxID CLI source SHA-256 | `51dd82101d7671859351e9989e8d554b4aa1cb898a4e04d948df28aa37de0195` |
| NyxID CLI archive SHA-256 | `8fe81a49ad3fb371ba7fd5085bdef4bcbec8756912679b8e8953674de2ad42bf` |
| Assistant registry wire revision | `nyxid-assistant-actions.v4` |
| 211-leaf source digest | `42337fe266f49f33f3c474006bce5646f913e7b775d9d4c1c78a24a7bcde10b6` |
| Support-contract gist revision | `f45febb057a7182dab2495d4c739d2bb8d7026f5` |
| Support-contract English content revision | `5da762f39782457dcdcc9e169ecb248dbf0a7818` |
| Support-contract Chinese content revision | `8495f00403db45b5f3fe5e733147da13968b8a8f` |
| Aevatar registry loader blob at captured integration revision | `2bcc18ea20abbc3947f365c61289e6c022befd3c` |
| Aevatar registry loader SHA-256 | `ed83f22bb95d01e4409a7e051748194a9595c93c276129d4ed38442ee4b0b53e` |

The CLI source digest and CLI archive digest identify different evidence artifacts and are intentionally recorded separately. Neither digest is inferred from the other.

The support-contract gist remains mutable design input. [Issue #3315](https://github.com/AevatarAI/aevatar/issues/3315) and [ADR-0049](../adr/0049-nyxid-assistant-plan-progress-and-operation-authorization.md) own the binding repository interpretation, including read-only plan progress, disclosed pre-plan reads, operation-owned authorization boundaries, and typed revision-cause semantics. The checked-in conformance manifest, not a mutable gist, becomes the implementation authority.

## 3. Production deployment evidence

The evidence below was captured from the production deployment of an exact `origin/feature/integrate` image. It satisfies the listed Wave 0 issue-level checks; #3318 still owns the final UC1a-UC4 release suite on the final milestone image.

| Evidence | Result |
|---|---|
| Deployed image | `docker.io/aelfdevops/aevatar-console-backend:e8c5ccc9` |
| Image digest | `sha256:b067de2e3f7d6071d719d1d682a7cf5b1366bcb0fe1eb231294ec9a99242fb90` |
| Workload | `aevatar-console-backend-7dfbc697cd-zn2jd`, Ready, restart count 0 |
| #3298 positive Class-R | Actor `nyxid-chat-5aa0c4ba50d82b5489d810721dc54eab`; account, status, sessions, API-key list, node list, pool list, developer-app list, and OAuth-binding list succeeded with `externalEffect=not_applied` |
| #3298 permission boundaries | Service-account list returned upstream 403 for `role=user`; node-credential list returned the NyxID writable-ACL hiding response 404 for an organization-visible node. Both committed typed `nyxid_request_failed` with `externalEffect=not_applied`; neither is contract drift |
| #3298 admin-only exclusion | Actor `nyxid-chat-222620d2d44aac0b2d906e1ae69a8c34`; final catalog count 23 excluded `nyxid_service_accounts`, SSE/state had no tool call or tool step, and pod logs had no `/api/v1/admin/service-accounts` request |
| #3299 adversarial catalog | Actor `nyxid-chat-2ba71529f0b2896bbf2b8da48b64b61d`; `nyxid_service_update`, `nyxid_service_delete`, `web_search`, and an unknown tool produced no tool call, state step, audit operation, or proxy dispatch |
| #3300 bearer path | Actor `nyxid-chat-05b4cefb7e1b52ca6b51863ca353ac05`; normal bearer turn completed at state version 14 without refresh or tool execution |

All listed canary conversations were deleted and their state endpoints returned `not_found`. The #3299 audit query had an ingestion watermark beyond its canary window but `windowCompleteness=unknown`; the final #3298 exclusion query was still behind the ingestion watermark. Audit emptiness is therefore supporting evidence only, not a claim of complete negative coverage. The delegated-token refresh success and failure paths remain unverified in production because the sanctioned CLI ingress supplies a bearer and cannot create a near-expiry delegated turn.

## 4. Classification rules

- `missing`: the issue's required contract or behavior is absent, or only a non-accepting fragment exists.
- `present-needs-tests`: relevant source exists, but issue-level deterministic or browser/integration acceptance is incomplete.
- `present-needs-production-evidence`: source-level behavior exists, but authenticated exact-image evidence is absent or stale.
- `completed`: the owning issue's source, contract, and required issue-level evidence are complete.
- `obsolete/superseded`: not part of the current M40 completion contract. This does not mean the product need is invalid.

Uncommitted working-tree changes are not deliverable evidence and do not improve a classification.

## 5. Issue inventory

The GitHub milestone API returned 30 attached issues even though its aggregate `open_issues` field reported 31. The table therefore inventories the 30 returned issues and separately records [#3312](https://github.com/AevatarAI/aevatar/issues/3312), the explicitly unmilestoned Wave 1 cross-repository epic. It does not imply that #3312 is attached to Milestone 40.

| Gate | Issue | Classification | Evidence, gap, and concrete disposition |
|---|---|---|---|
| 0 | [#3317 baseline](https://github.com/AevatarAI/aevatar/issues/3317) | `completed` | The only baseline is `origin/feature/integrate@6cf0da4cc`; stewardship is `@eanz17` with target 2026-08-08. The complete issue classification and supersession decisions are recorded here. |
| 0 | [#3315 contract correction](https://github.com/AevatarAI/aevatar/issues/3315) | `completed` | [ADR-0049](../adr/0049-nyxid-assistant-plan-progress-and-operation-authorization.md) pins read-only plan progress, Tier B, the pre-plan Class-R exception, exact operation dispatch, and typed revision provenance. #3302/#3304/#3324/#3321 implement and verify the accepted contract. |
| 1 | [#3296 one chat trunk](https://github.com/AevatarAI/aevatar/issues/3296) | `missing` | The integration baseline still routes `/api/chat` by request shape between Workflow and Assistant runtimes. Introduce one HTTP-free canonical application facade; keep form/no-type only as a frozen compatibility adapter until its callers migrate. |
| 1 | [#3297 R/A/P/L/X ADR](https://github.com/AevatarAI/aevatar/issues/3297) | `completed` | [ADR-0048](../adr/0048-nyxid-assistant-operation-class-boundary.md) is accepted on the integration baseline. Its exact Class-P consumer is #3320; the ADR alone does not ship execution. |
| 1 | [#3320 admitted execution](https://github.com/AevatarAI/aevatar/issues/3320) | `missing` | Shared MCP/admission machinery exists, but the M40 request-local Class-P chat exposure and its complete acceptance are not established. Implement only exact admitted operations; keep raw proxy hidden. |
| 1 | [#3298 Class-R reads](https://github.com/AevatarAI/aevatar/issues/3298) | `completed` | `6a32bc32d` and `e8c5ccc94` complete the ordinary-user reads and admin-only ceiling. Deterministic tests and exact-image canaries passed, including honest typed permission failures. |
| 1 | [#3299 allowlist exposure](https://github.com/AevatarAI/aevatar/issues/3299) | `completed` | `080bb5c28` plus `e8c5ccc94` implement the closed catalog and role ceiling. The exact-image adversarial canary passed; final UC proof is consolidated under #3318. |
| 1 | [#3300 credential lifecycle](https://github.com/AevatarAI/aevatar/issues/3300) | `present-needs-production-evidence` | `cce255a03` implements bearer/delegation decision and typed refresh failure with deterministic tests. The bearer production path passed; sanctioned ingress cannot exercise delegated refresh without a browser/session bridge. |
| 1 | [#3311 approval contract](https://github.com/AevatarAI/aevatar/issues/3311) | `completed` | ADR-0048 selects Tier B/no-NyxID-change for M40. Generic `tool_approval` is never treated as exact-service authorization; #3324 owns implementation. |
| 2 | [#3301 TaskPlan vocabulary](https://github.com/AevatarAI/aevatar/issues/3301) | `present-needs-tests` | Task-plan proto/decoder work from `53e20f9ba` is on the integration baseline. Review one decoder path, rename `postcondition_kind` to `check` while preserving protobuf tag 2, and add three-path convergence fixtures. |
| 2 | [#3302 derived execution control](https://github.com/AevatarAI/aevatar/issues/3302) | `superseded` | The former plan-level execution control was removed. Current acceptance is direct typed operation dispatch plus browser-owned OAuth and exact tool-owned approval boundaries from ADR-0049. |
| 2 | [#3304 stable identity](https://github.com/AevatarAI/aevatar/issues/3304) | `present-needs-tests` | Integration preserves task and plan state through action work, but complete continuation/reorder/duplicate tests are not evidenced. Validate stable `taskId` and monotonic `planRevision` across every continuation. |
| 2 | [#3305 generalized verify](https://github.com/AevatarAI/aevatar/issues/3305) | `missing` | Connect-specific postcondition handling exists on integration; generalized typed effect verification does not meet issue scope. Implement from committed effect evidence, never assistant prose. |
| 2 | [#3307 composite ask](https://github.com/AevatarAI/aevatar/issues/3307) | `present-needs-tests` | Composite input and restored typed `ask_user` changes are on the integration baseline; prove free-text-only, mixed-choice, duplicate/stale, and reload cases against the latest contract rather than the pre-sync `MinOptions=2` assumption. |
| 2 | [#3324 Tier-B observation/resume](https://github.com/AevatarAI/aevatar/issues/3324) | `missing` | Under Tier B, show running/waiting then threshold-derived stalled; create an approval fact only after NyxID returns 7000/7001 with `approval_request_id`. No pre-effect synthetic card. |
| 2 | [#3314 Studio UC1a/UC1b](https://github.com/AevatarAI/aevatar/issues/3314) | `present-needs-tests` | Studio cards, decoder, and rehydration work exist in [`53e20f9ba`](https://github.com/AevatarAI/aevatar/commit/53e20f9ba2bc6f883a887615a3e015ce4eac3caa), but browser acceptance and Tier-B copy/state behavior are incomplete. |
| 2 | [#3131 pending attention projection](https://github.com/AevatarAI/aevatar/issues/3131) | `present-needs-tests` | Actor-state projection work is present in [`53e20f9ba`](https://github.com/AevatarAI/aevatar/commit/53e20f9ba2bc6f883a887615a3e015ce4eac3caa). Prove the actor-scoped current-state path exposes pending input/approval attention without query-time priming. |
| 2 | [#3152 readiness identity](https://github.com/AevatarAI/aevatar/issues/3152) | `present-needs-tests` | [`5e3f1a22b`](https://github.com/AevatarAI/aevatar/commit/5e3f1a22b) projects authoritative readiness identity. Verify intentionally distinct service/readiness IDs with issue-level projection tests. |
| 2 | [#3154 authoritative resume](https://github.com/AevatarAI/aevatar/issues/3154) | `present-needs-tests` | [`73f0412e9`](https://github.com/AevatarAI/aevatar/commit/73f0412e9) contains needs-you continuation work. Prove duplicate/stale action decisions cannot resume a different task generation. |
| 2 | [#3177 connect blocker](https://github.com/AevatarAI/aevatar/issues/3177) | `obsolete/superseded` | Credential-kind and delegated-failure fixes are implemented. The remaining workflow-chat card/terminal scope is superseded by the converged trunk in #3296 and Studio acceptance in #3314/#3318. |
| 2 | [#3167 terminal/action frames](https://github.com/AevatarAI/aevatar/issues/3167) | `obsolete/superseded` | The legacy workflow-chat path is not repaired as a second product trunk. Action, terminal, and reload acceptance belongs to #3296/#3314/#3318. |
| 3 | [#3303 presentation substeps](https://github.com/AevatarAI/aevatar/issues/3303) | `missing` | Vocabulary fragments exist, but production substep derivation and lifecycle behavior do not satisfy the issue. Keep substeps presentation-only and actor-derived. |
| 3 | [#3306 progress/stall](https://github.com/AevatarAI/aevatar/issues/3306) | `missing` | Progress transport exists, but cadence and honest stall thresholds lack issue-level acceptance. Stall must derive from observed silence, not be authored as an execution result. |
| 3 | [#3308 preference/honest-can't](https://github.com/AevatarAI/aevatar/issues/3308) | `missing` | No generalized cannot-check and preference-order contract is evidenced. Implement typed unavailable outcomes before planner fallback. |
| 3 | [#3310 reconcile-first retry](https://github.com/AevatarAI/aevatar/issues/3310) | `missing` | Generic retry fragments do not establish reconcile-first behavior for effect-capable steps. Retry must re-enter a new generation and never reuse an approval as broader authority. |
| 3 | [#3316 Studio controls](https://github.com/AevatarAI/aevatar/issues/3316) | `present-needs-tests` | UI controls exist on integration, but reconcile/stall behavior and live reload evidence are incomplete. Test controls against committed actor/read-model state. |
| 3 | [#3321 steering/re-plan](https://github.com/AevatarAI/aevatar/issues/3321) | `missing` | Steering fragments exist; failure-driven re-plan and generation fencing are not complete. Resume only through actor-owned event continuations. |
| 4 | [#3309 Class-L/Class-X](https://github.com/AevatarAI/aevatar/issues/3309) | `missing` | Exact local command handoff and honest decline are not implemented as matrix-driven typed outcomes. They must never fabricate remote execution. |
| 4 | [#3313 conformance SSOT](https://github.com/AevatarAI/aevatar/issues/3313) | `missing` | No checked-in machine-readable 211-intent authority, digest drift gate, or full adversarial fixture corpus exists. Generate it from the accepted contract revisions, not from this inventory. |
| 4 | [#3318 production canaries](https://github.com/AevatarAI/aevatar/issues/3318) | `present-needs-production-evidence` | This is the release-proof owner. No authenticated UC1a-UC4 evidence pinned to the final exact deployed Aevatar and NyxID revisions is recorded yet. |
| Excluded | [#3312 Wave 1](https://github.com/AevatarAI/aevatar/issues/3312) | `obsolete/superseded` (for M40 only) | The cross-repository Wave 1 epic is not attached to M40. Its need remains valid, but M40 must route `service.reauthorize`, `key.create`, and `key.rotate` to Class-X honest not-yet-executable outcomes. |

## 6. Gate 0 exit blockers

Gate 0 is closed. The remaining governance work belongs to later gates:

1. keep Gate stewardship assigned to `@eanz17` and record any target-date change explicitly;
2. replace this planning inventory with the machine-readable conformance authority required by #3313; and
3. keep deterministic source/test evidence separate from authenticated production evidence owned by #3318.

## 7. Change and evidence protocol

Every owning change names its exact integration commit, removes duplicate or superseded behavior, and runs the issue's focused tests plus repository guards. For `present-needs-production-evidence`, do not close from source inspection alone: wait for the exact short-SHA image and record authenticated canary plus committed-state/read-model evidence in the release artifact.
