---
title: "Scheduled Agent Key Runtime Integrity Rollout"
status: active
owner: platform
---

# Scheduled Agent Key Runtime Integrity Rollout

This is the release gate for the scheduled Agent Key integrity change. The
detailed production procedure is
[`2026-07-23-scheduled-agent-key-production-canary.md`](./2026-07-23-scheduled-agent-key-production-canary.md).
Do not start that canary until every gate in this document is satisfied.

## Integrity Contract

The release preserves one authoritative chain:

```text
committed typed UserConfig selection
  -> digest-covered authorization plan
  -> constrained NyxID Agent Key + Vault reference
  -> actor-owned authorization fact + persisted ChatRequestEvent.LlmControl
  -> runtime caller/payload/fact cross-check
  -> workflow inbox
```

The release must fail closed. It must not query UserConfig when a schedule
fires, fill route or model from Host defaults, infer identity from a legacy
slug or model prefix, accept missing caller binding, project caller authority,
or accept a v1 permission digest as v2-compatible.

## Gate 1: Drain The Old Binary

Run the drain against the old production binaries immediately before the
deployment. Use only the canonical scoped Team/member automation APIs. A
release-time inventory is complete only after every Team page, every Team
member page, and every member automation cursor has been exhausted.

The drain must establish all of the following:

1. No automation is `provisioning_pending` or `replacement_pending`.
2. Every active `scheduled_invocation_agent_key` automation is correlated to
   an approved non-projected caller-authority audit containing its verified
   NyxID binding.
3. Any active Agent Key automation without that approved evidence is paused
   through its canonical `/{scheduleId}/pause` action before deployment.
4. The paused detail is observed with `enabled == false`; an accepted pause
   receipt alone is insufficient.

Caller authority is intentionally absent from projections and public APIs.
Do not infer a binding from member, workflow, published-service, schedule, or
run identity. Do not add an admin endpoint for this drain. If the old-binary
inventory cannot be proven complete, approved non-projected authority evidence
is unavailable, or a deficient schedule cannot be paused, stop the release.

After rollout, run a new preflight and `reauthorize` each schedule paused by the
drain. Keep it paused until the projected schedule is `active` on a newer
authoritative `stateVersion` and the v2 runtime evidence has been accepted.
Never resume a schedule from its old v1 operation or digest.

Record only scope, Team, member, schedule, operation, authorization status,
credential source, enabled state, authoritative state version, and the
pause/reauthorize disposition. The approved authority audit remains outside
the public read model.

## Gate 2: One Atomic Release

Plan, authorization fact, schedule actor state, committed-state projector, and
Studio API are one release unit. Rolling out a subset is forbidden.

The release owner must provide these values from one immutable release
manifest. They are deliberately not hardcoded in this dated document:

The manifest must name `authorization-plan`, `authorization-fact`,
`schedule-actor-state`, `scheduled-current-state-projector`, and
`studio-automation-api`, bind every component to the same final source SHA,
and bind the deployed immutable image digest set. A verbal statement that the
components were built together is not a manifest.

```bash
set +x
set -euo pipefail

: "${FINAL_PUSHED_RELEASE_SHA:?set the final pushed/release source SHA}"
: "${DEPLOYED_SOURCE_SHA:?set the deployed source SHA from the same manifest}"
: "${RELEASE_MANIFEST_URI:?set the immutable release manifest locator}"
: "${RELEASE_MANIFEST_DIGEST:?set the immutable manifest digest}"
: "${RELEASE_IMAGE_SET_JSON:?set the manifest image digest set as a JSON array}"

[[ "$FINAL_PUSHED_RELEASE_SHA" =~ ^[0-9a-f]{40}$ ]]
[[ "$DEPLOYED_SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]]
[[ "$RELEASE_MANIFEST_DIGEST" =~ ^sha256:[0-9a-f]{64}$ ]]
jq -e '
  type == "array"
  and length > 0
  and all(type == "string" and test("^sha256:[0-9a-f]{64}$"))
  and (unique | length) == length
' <<<"$RELEASE_IMAGE_SET_JSON" >/dev/null

git fetch --quiet origin
git cat-file -e "$FINAL_PUSHED_RELEASE_SHA^{commit}"
git cat-file -e "$DEPLOYED_SOURCE_SHA^{commit}"
git merge-base --is-ancestor "$FINAL_PUSHED_RELEASE_SHA" "$DEPLOYED_SOURCE_SHA"
```

Prove the running workload's complete image digest set equals the manifest
image set. A mutable tag, healthy HTTP response, or source SHA from another
release record is not evidence. If workload visibility is unavailable or the
sets differ during rollout, stop and roll back the complete release unit.

## Gate 3: Post-Deploy Contract And Owner Selection

Before creating a canary:

1. Confirm the live OpenAPI exposes the typed UserConfig selection and all five
   automation route/model fields.
2. Confirm repository tool `tools/schedules/query_member_automation_audit.sh`
   can retrieve category `Aevatar.Studio.MemberAutomation`, EventIds `6201`
   (`StudioMemberAutomationCreateAccepted`) and `6202`
   (`StudioMemberAutomationRevocationCompleted`) without dumping raw logs.
3. Confirm the deployed public automation contract exposes both
   `nyxIdRevocationStatus` and `vaultRevocationStatus`. Their implemented wire
   values are `NotRequired`, `Pending`, `Completed`, and `Failed`; this canary
   creates a credential, so the repository audit query must prove
   both tracks are `Completed` before detail `404` is accepted.
4. Capture the original typed UserConfig selection without secrets, then
   explicitly save UserService ID
   `4061b904-62de-4cee-9125-5e3ec8365afd` with model `gpt-5.5` through
   `PUT /api/user-config/llm`.
5. Observe a committed `GET /api/user-config/llm` with kind
   `nyx_id_user_service`, route
   `/api/v1/proxy/s/chrono-llm-public`, slug `chrono-llm-public`, the exact
   UserService ID, and model `gpt-5.5`.
6. Record the `leave_selected` disposition and explicit approval to retain the
   exact owner-wide selection after the canary.

The public schema must exclude `callerAuthority`, `verifiedBindingId`,
`secretReference`, `apiKeyId`, `fullKey`, and `ciphertext`. A similarly named
replacement or generic bag does not satisfy this exclusion.

The `202 Accepted` UserConfig receipt is dispatch evidence only. The canary
must not start until the typed GET observes the committed selection.

## Gate 4: Production Canary

Execute the linked detailed canary without skipping its gates. Acceptance
requires:

- the CLI is exactly `/Users/eanzhao/.local/bin/nyxid`;
- both allow-all flags are `false`;
- Team, member, draft workflow, published service, and schedule IDs are five
  distinct identities;
- the structured create event correlates exactly one verified binding to the
  scope, Team, member, schedule, and create operation using only its six
  approved fields;
- active and post-run automation views expose the exact five route/model
  fields and no caller, binding, or credential material;
- `simple_qa` observes its completion marker, an authoritative state-version
  advance, and the exact captured NyxID key's `last_used_at` transition;
- every failure records a stable code, status, authoritative version, IDs, and
  UTC timestamps without secrets;
- deletion proves both projected revocation tracks terminal, the exact key ID
  and name inactive or absent, schedule detail `404`, temporary-resource
  cleanup, and a final automation list with zero items and `totalCount == 0`.

Remove the local canary state directory after extracting the allowlisted
evidence. Any unmet proof is a failed canary, not a reason to infer or repair
runtime facts.

Every `nyxid api-key list --output json` use must pipe directly into `jq` and
write only the single-key allowlisted projection needed by the next assertion.
Never persist or attach a complete owner key inventory.
