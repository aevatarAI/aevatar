---
title: "Milestone 40 Gate 0 Baseline and Evidence Inventory"
status: draft
owner: eanzhao
---

# Milestone 40 Gate 0 Baseline and Evidence Inventory

Recaptured at 2026-08-08T02:54:07+08:00 for [Milestone 40](https://github.com/AevatarAI/aevatar/milestone/40). This is an evidence inventory, not a Gate 0 exit record. It does not state that Calvin accepted the support-contract corrections, that Gate 0 passed, or that an integration-lineage deployment satisfies the `dev` release baseline.

## 1. Binding baseline conflict

[Issue #3317](https://github.com/AevatarAI/aevatar/issues/3317) makes `dev` the only deliverable baseline because CI, release, and production canaries measure what `dev` ships. At capture time:

| Evidence | Revision | Meaning |
|---|---|---|
| Deliverable baseline | `origin/dev@0ed05a923e14b22dfa49e4182a78501eae3f0d52` | Binding acceptance baseline from #3317 after PRs #3337 and #3338 |
| Integration source | `origin/feature/integrate@e8c5ccc94ac2a0d4c317723c8574e42f4139ad3e` | Frozen portable implementation source, not an alternative acceptance baseline |
| Merge base | `fae59d63788e4670d4252591df788a9ac3cf1705` | The branches have diverged: two `dev`-only commits and 19 integration-only commits |
| Shared principal implementation | [`53e20f9ba2bc6f883a887615a3e015ce4eac3caa`](https://github.com/AevatarAI/aevatar/commit/53e20f9ba2bc6f883a887615a3e015ce4eac3caa) | Task-plan, action registry, projection, and Studio source now present on both lineages |
| Integration-only Wave 0 source | [`6a32bc32d`](https://github.com/AevatarAI/aevatar/commit/6a32bc32d), [`080bb5c28`](https://github.com/AevatarAI/aevatar/commit/080bb5c28), [`cce255a03`](https://github.com/AevatarAI/aevatar/commit/cce255a03), [`e8c5ccc94`](https://github.com/AevatarAI/aevatar/commit/e8c5ccc94) | Completion reads, closed catalog, delegation refresh, and admin-only exposure correction to review and port individually |

The implementation request says to work from and eventually push to `origin/feature/integrate`. That request conflicts with the binding #3317 acceptance rule. Work may continue on the integration lineage, but Milestone 40 cannot be accepted from that lineage alone. Formal acceptance requires one of:

1. reviewed port/merge of the accepted implementation into `dev`, followed by re-running this inventory against the resulting `dev` SHA; or
2. an explicit revision of #3317 by its owner.

The baseline decision itself is no longer ambiguous: `dev` is authoritative. Gate 0 remains open because the frozen integration-only commits have not been individually reviewed and ported, the post-port `dev` SHA does not exist, and the required owner/DRI decisions are absent. A wholesale baseline switch or an unreviewed merge of integration is not the port plan.

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

The support-contract gist remains mutable design input. [Issue #3315](https://github.com/AevatarAI/aevatar/issues/3315) still requires Calvin to accept or explicitly rebut the 11 body corrections plus the typed revision-cause follow-up against the selected deliverable baseline. No such owner decision is asserted here.

## 3. Integration deployment evidence

The evidence below proves behavior on an integration-lineage deployment only. It does not satisfy #3317 or #3318 until the selected changes land on `dev` and the release canaries run against that exact image.

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
- `present-on-lineage (port plan)`: useful implementation exists only on the integration lineage and must be reviewed and ported to `dev`.
- `present-needs-tests`: relevant source exists, but issue-level deterministic or browser/integration acceptance is incomplete.
- `present-needs-release-evidence`: source-level behavior exists, but authenticated deployed evidence is absent or stale.
- `obsolete/superseded`: not part of the current M40 completion contract. This does not mean the product need is invalid.

Uncommitted working-tree changes are not deliverable evidence and do not improve a classification.

## 5. Issue inventory

The GitHub milestone API returned 30 attached issues even though its aggregate `open_issues` field reported 31. The table therefore inventories the 30 returned issues and separately records [#3312](https://github.com/AevatarAI/aevatar/issues/3312), the explicitly unmilestoned Wave 1 cross-repository epic. It does not imply that #3312 is attached to Milestone 40.

| Gate | Issue | Classification | Evidence, gap, and concrete disposition |
|---|---|---|---|
| 0 | [#3317 baseline](https://github.com/AevatarAI/aevatar/issues/3317) | `missing` | `dev@0ed05a923` is binding. Review and port the four frozen Wave 0 commits, name the resulting `dev` SHA, re-run this classification, and fill Gate 1 DRI, entry owner, exit owner, and target date without inventing them. |
| 0 | [#3315 contract correction](https://github.com/AevatarAI/aevatar/issues/3315) | `missing` | Eleven body corrections and the revision-cause follow-up are pinned, but the support-contract owner has not accepted or rebutted them. Preserve the gist revision and feed accepted changes to #3313. |
| 1 | [#3296 one chat trunk](https://github.com/AevatarAI/aevatar/issues/3296) | `missing` | Both frozen lineages still route `/api/chat` by request shape between Workflow and Assistant runtimes. Introduce one HTTP-free canonical application facade; keep form/no-type only as a frozen compatibility adapter until its callers migrate. |
| 1 | [#3297 R/A/P/L/X ADR](https://github.com/AevatarAI/aevatar/issues/3297) | `present-needs-tests` | [ADR-0048](../adr/0048-nyxid-assistant-operation-class-boundary.md) is now on `dev`, but remains proposed pending #3315 and #3317. Its exact Class-P consumer is #3320; do not infer that the ADR alone ships execution. |
| 1 | [#3320 admitted execution](https://github.com/AevatarAI/aevatar/issues/3320) | `missing` | Shared MCP/admission machinery exists, but the M40 request-local Class-P chat exposure and its complete acceptance are not established. Implement only exact admitted operations; keep raw proxy hidden. |
| 1 | [#3298 Class-R reads](https://github.com/AevatarAI/aevatar/issues/3298) | `present-on-lineage (port plan)` | `dev` contains the first read slice; integration adds completion and the admin-only correction in `6a32bc32d` and `e8c5ccc94`. The integration canaries above passed the ordinary-user set and proved permission failures honest. Review/port the selected hunks, then repeat against the `dev` release image. |
| 1 | [#3299 allowlist exposure](https://github.com/AevatarAI/aevatar/issues/3299) | `present-on-lineage (port plan)` | `080bb5c28` implements the closed catalog and the integration adversarial canary passed. Port that commit plus the `e8c5ccc94` ceiling correction; re-run the negative corpus on the `dev` image. |
| 1 | [#3300 credential lifecycle](https://github.com/AevatarAI/aevatar/issues/3300) | `present-on-lineage (port plan)` | `cce255a03` implements bearer/delegation decision and typed refresh failure with deterministic tests. The bearer production path passed, but sanctioned ingress cannot exercise delegated refresh. Port the commit, then obtain browser/session-bridge success and failure evidence without copying credentials. |
| 1 | [#3311 approval contract](https://github.com/AevatarAI/aevatar/issues/3311) | `present-needs-tests` | ADR-0048 on `dev` selects Tier B/no-NyxID-change for M40. The cross-repository contract and #3315 owner correction remain open; generic `tool_approval` cannot be treated as exact-service authorization. |
| 2 | [#3301 TaskPlan vocabulary](https://github.com/AevatarAI/aevatar/issues/3301) | `present-needs-tests` | Task-plan proto/decoder work from `53e20f9ba` is now on `dev`. Review one decoder path, rename `postcondition_kind` to `check` while preserving protobuf tag 2, and add three-path convergence fixtures. |
| 2 | [#3302 derived gate](https://github.com/AevatarAI/aevatar/issues/3302) | `present-needs-tests` | Gate vocabulary and task lifecycle fragments from `53e20f9ba` are now on `dev`; derived behavior and propose-then-run acceptance remain blocked on #3315's plan-gate decision. |
| 2 | [#3304 stable identity](https://github.com/AevatarAI/aevatar/issues/3304) | `present-needs-tests` | Integration preserves task and plan state through action work, but complete continuation/reorder/duplicate tests are not evidenced. Validate stable `taskId` and monotonic `planRevision` across every continuation. |
| 2 | [#3305 generalized verify](https://github.com/AevatarAI/aevatar/issues/3305) | `missing` | Connect-specific postcondition handling exists on integration; generalized typed effect verification does not meet issue scope. Implement from committed effect evidence, never assistant prose. |
| 2 | [#3307 composite ask](https://github.com/AevatarAI/aevatar/issues/3307) | `present-needs-tests` | Composite input and restored typed `ask_user` changes are now on `dev`; prove free-text-only, mixed-choice, duplicate/stale, and reload cases against the latest contract rather than the pre-sync `MinOptions=2` assumption. |
| 2 | [#3324 Tier-B observation/resume](https://github.com/AevatarAI/aevatar/issues/3324) | `missing` | Under Tier B, show running/waiting then threshold-derived stalled; create an approval fact only after NyxID returns 7000/7001 with `approval_request_id`. No pre-effect synthetic card. |
| 2 | [#3314 Studio UC1a/UC1b](https://github.com/AevatarAI/aevatar/issues/3314) | `present-needs-tests` | Studio cards, decoder, and rehydration work exist in [`53e20f9ba`](https://github.com/AevatarAI/aevatar/commit/53e20f9ba2bc6f883a887615a3e015ce4eac3caa), but browser acceptance and Tier-B copy/state behavior are incomplete. |
| 2 | [#3131 pending attention projection](https://github.com/AevatarAI/aevatar/issues/3131) | `present-needs-tests` | Actor-state projection work is present on integration in [`53e20f9ba`](https://github.com/AevatarAI/aevatar/commit/53e20f9ba2bc6f883a887615a3e015ce4eac3caa). Port the actor-scoped current-state path and prove pending input/approval attention without query-time priming. |
| 2 | [#3152 readiness identity](https://github.com/AevatarAI/aevatar/issues/3152) | `present-needs-tests` | [`5e3f1a22b`](https://github.com/AevatarAI/aevatar/commit/5e3f1a22b) projects authoritative readiness identity. Port with intentionally distinct service/readiness IDs and run the issue-level projection tests. |
| 2 | [#3154 authoritative resume](https://github.com/AevatarAI/aevatar/issues/3154) | `present-needs-tests` | [`73f0412e9`](https://github.com/AevatarAI/aevatar/commit/73f0412e9) contains needs-you continuation work. Port and prove duplicate/stale action decisions cannot resume a different task generation. |
| 2 | [#3177 connect blocker](https://github.com/AevatarAI/aevatar/issues/3177) | `present-needs-release-evidence` | Integration contains readiness/connect fixes, including [`5e3f1a22b`](https://github.com/AevatarAI/aevatar/commit/5e3f1a22b). Source presence is not proof that deployed workflow-chat emits the blocker; retain for Gate 2 canary evidence. |
| 2 | [#3167 terminal/action frames](https://github.com/AevatarAI/aevatar/issues/3167) | `present-needs-release-evidence` | Frame and Studio changes exist in [`53e20f9ba`](https://github.com/AevatarAI/aevatar/commit/53e20f9ba2bc6f883a887615a3e015ce4eac3caa). Current authenticated production proof for both action and terminal frames is missing. |
| 3 | [#3303 presentation substeps](https://github.com/AevatarAI/aevatar/issues/3303) | `missing` | Vocabulary fragments exist, but production substep derivation and lifecycle behavior do not satisfy the issue. Keep substeps presentation-only and actor-derived. |
| 3 | [#3306 progress/stall](https://github.com/AevatarAI/aevatar/issues/3306) | `missing` | Progress transport exists, but cadence and honest stall thresholds lack issue-level acceptance. Stall must derive from observed silence, not be authored as an execution result. |
| 3 | [#3308 preference/honest-can't](https://github.com/AevatarAI/aevatar/issues/3308) | `missing` | No generalized cannot-check and preference-order contract is evidenced. Implement typed unavailable outcomes before planner fallback. |
| 3 | [#3310 reconcile-first retry](https://github.com/AevatarAI/aevatar/issues/3310) | `missing` | Generic retry fragments do not establish reconcile-first behavior for effect-capable steps. Retry must re-enter a new generation and never reuse an approval as broader authority. |
| 3 | [#3316 Studio controls](https://github.com/AevatarAI/aevatar/issues/3316) | `present-needs-tests` | UI controls exist on integration, but reconcile/stall behavior and live reload evidence are incomplete. Test controls against committed actor/read-model state. |
| 3 | [#3321 steering/re-plan](https://github.com/AevatarAI/aevatar/issues/3321) | `missing` | Steering fragments exist; failure-driven re-plan and generation fencing are not complete. Resume only through actor-owned event continuations. |
| 4 | [#3309 Class-L/Class-X](https://github.com/AevatarAI/aevatar/issues/3309) | `missing` | Exact local command handoff and honest decline are not implemented as matrix-driven typed outcomes. They must never fabricate remote execution. |
| 4 | [#3313 conformance SSOT](https://github.com/AevatarAI/aevatar/issues/3313) | `missing` | No checked-in machine-readable 211-intent authority, digest drift gate, or full adversarial fixture corpus exists. Generate it from the accepted contract revisions, not from this inventory. |
| 4 | [#3318 production canaries](https://github.com/AevatarAI/aevatar/issues/3318) | `present-needs-release-evidence` | This is the release-proof owner. No authenticated UC1a-UC4 evidence pinned to exact deployed Aevatar and NyxID revisions is recorded yet. |
| Excluded | [#3312 Wave 1](https://github.com/AevatarAI/aevatar/issues/3312) | `obsolete/superseded` (for M40 only) | The cross-repository Wave 1 epic is not attached to M40. Its need remains valid, but M40 must route `service.reauthorize`, `key.create`, and `key.rotate` to Class-X honest not-yet-executable outcomes. |

## 6. Gate 0 exit blockers

Gate 0 is not ready to close. The minimum unresolved items are:

1. review the frozen integration-only Wave 0 commits issue by issue and produce normal PR ports to `dev`;
2. recapture the resulting post-port `dev` SHA and re-run the complete issue/dependency classification;
3. obtain the #3315 support-contract owner's accept/rebut decision without attributing a decision that was not made;
4. fill Gate 1 DRI, entry owner, exit owner, and target date without inventing them;
5. replace this planning inventory with the machine-readable conformance authority required by #3313 when the source contract is accepted; and
6. keep deterministic source/test evidence separate from authenticated `dev` production evidence owned by #3318.

## 7. Port and evidence protocol

For every `present-on-lineage (port plan)` row, the owning PR must name the selected source commit and files, remove duplicate or superseded behavior, and run the issue's focused tests plus repository guards. After the port lands, recapture the `dev` SHA and reclassify the row. For `present-needs-release-evidence`, do not close the issue from source inspection; record the exact deployed Aevatar/NyxID revisions and committed-state/read-model evidence in the release canary artifact.
