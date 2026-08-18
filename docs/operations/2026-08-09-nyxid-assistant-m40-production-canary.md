---
title: "NyxID Assistant Milestone 40 Production Canary"
status: completed
owner: platform
---

# NyxID Assistant Milestone 40 Production Canary

## Purpose

This runbook is the production acceptance procedure for GitHub issue #3318 and
Milestone 40. It proves UC1a, UC1b, UC2, UC3, and UC4 against exact Aevatar
revisions deployed from `origin/feature/integrate`. `dev` is not a source,
delivery, deployment, comparison, or acceptance branch for this run.

Authenticated production evidence is accepted for UC1a, UC1b, UC2, UC3, and
UC4. Every disposable conversation, provider record, Approval instance, grant,
and OAuth connection created by the run has exact cleanup evidence, so the
runbook is completed.

## Production Evidence Ledger

| Use case | Status | Exact source | Production acceptance summary |
| --- | --- | --- | --- |
| UC1a | Accepted | `origin/feature/integrate@4c3b33ad272807430ca3ba6bc753f849f8c8fe5b` | A disconnected start produced one durable `service_connect` action, preserved it across reload, created no GitHub connection, and cleaned up the conversation. |
| UC1b | Accepted | `origin/feature/integrate@4c3b33ad272807430ca3ba6bc753f849f8c8fe5b` | A connected-ready start produced one confirmed actor-owned `service.connected` postcondition, preserved it across reload, and cleaned up the conversation and disposable OAuth service. |
| UC2 | Accepted | `origin/feature/integrate@87daa99e641533f25ea0ddc67396e1a0dc52bd59` | Steer, authoritative stop, new task, reload, and deletion were committed on one exact healthy image with no external effect. |
| UC3 | Accepted | `origin/feature/integrate@b5f32cbbeb09f150b9d32ba8684926f61d40bfc9` | Rejection, uncertainty, exact reconciliation, fresh approved retry, provider verification, reload, cancel, and cleanup were proved with no grant. |
| UC4 | Accepted | `origin/feature/integrate@4c3b33ad272807430ca3ba6bc753f849f8c8fe5b` | Both condition branches were proved; only observed value 80 wrote a row, and the exact row, conversations, grants, and service overlay were cleaned up. |

### UC1a: Disconnected Start

- Deployment anchor: source `4c3b33ad272807430ca3ba6bc753f849f8c8fe5b`;
  image `docker.io/aelfdevops/aevatar-console-backend:4c3b33ad`; digest
  `sha256:58d05dd6d2bf6b6d766159a2678f54ed806fcf2660eb5aafc729e4449c8f785e`;
  generation `2908`; revision `1489`; Ready `1/1`; restart count `0`; health
  `9/9`.
- Bounded UTC evidence: created `2026-08-10T00:19:13.2353077Z`; blocked
  terminal committed `2026-08-10T00:19:34.490761Z`; gate decided
  `2026-08-10T00:20:10.4404147Z`; cleanup control committed
  `2026-08-10T00:21:09.1576163Z`.
- Start state: the authenticated owner's personal `api-github` service was
  absent.
- Conversation `nyxid-chat-9dd9187b266cbb613855666991acc9c3`; turn
  `turn-c264ce54a3e9acdb696c2fe8693721f8`; task
  `task-835994ffb8c96bdf86ab66ed501c7cbc`.
- Plan revision `4` committed with gate `satisfied`. The single
  `service_connect` action used request
  `action-1c8040336794273bf6171eac414d2f32`, requested scope `public_repo`, and
  remained `waiting` with `externalEffect=not_started`.
- The turn/task committed the expected blocked terminal shape
  `NYXID_ACTION_REQUESTED`; exactly one terminal frame, `RUN_FINISHED`, was
  observed.
- StateVersion `28` preserved the same task, action request, gate, and waiting
  state after reload. Stop was accepted and committed as `already_terminal` at
  StateVersion `29`.
- No GitHub connection was created. Conversation deletion completed, and both
  state and transcript subsequently returned `404 not_found`.

### UC1b: Connected And Ready Start

- Deployment anchor: source `4c3b33ad272807430ca3ba6bc753f849f8c8fe5b`;
  image `docker.io/aelfdevops/aevatar-console-backend:4c3b33ad`; digest
  `sha256:58d05dd6d2bf6b6d766159a2678f54ed806fcf2660eb5aafc729e4449c8f785e`;
  generation `2908`; revision `1489`; Ready `1/1`; restart count `0`; health
  `9/9`.
- Bounded UTC evidence: the actor turn started at
  `2026-08-10T01:18:55.9856642Z`, committed its terminal at
  `2026-08-10T01:19:16.8611936Z`, and all conversation and OAuth cleanup was
  verified by `2026-08-10T01:21:13Z`.
- Start state: disposable personal OAuth UserService
  `1727d7f8-0cbb-4b0a-9148-fe367e446d3d` was `active/connected`, used slug
  `api-github`, and exposed granted scopes `read:user`, `repo`, and
  `user:email`. Catalog refresh was observed and ready at visible StateVersion
  `123`, ahead of required StateVersion `122`.
- Conversation `nyxid-chat-6cefdf1da9ad4efa8f7e7561ad0ac9ef`; turn
  `turn-55c05cceddccc41c1a0a2d18a08accaf`; task
  `task-a5e8efae4152cc8482b10eb5d7eb0893`.
- Plan revision `4` committed gate `satisfied`. The single admitted
  `service_connect` postcondition used action request
  `action-postcondition-95f66701cbad996960d0f4e96b869a14` and exact
  provider resource identity
  `1727d7f8-0cbb-4b0a-9148-fe367e446d3d`.
- The actor-owned `service.connected` postcondition reached
  `done/externalEffect=confirmed`; the task and turn succeeded at StateVersion
  `30`. SSE contained exactly one terminal, `RUN_FINISHED`.
- Reload preserved the same task, turn, gate, postcondition step, action
  request, provider identity, and single terminal. Transcript before and after
  reload contained the same one complete user message and one complete
  assistant message.
- Conversation deletion completed, and state and transcript returned
  `404 not_found`. The disposable OAuth UserService and its upstream grant were
  deleted; exact service read-back returned `404`, the personal `api-github`
  inventory was empty, both pre-existing PAT services remained
  `active/connected`, pending approvals were `0`, and grants were `0`.

### UC2: Steer, Stop, And New Task

- Deployment anchor: source `87daa99e641533f25ea0ddc67396e1a0dc52bd59`;
  image `docker.io/aelfdevops/aevatar-console-backend:87daa99e`; digest
  `sha256:a14b85c9441fa17fd251c6d116c670bdf2420d09dc0183a8d1fa76f0ffb0b12a`;
  generation `2852`; revision `1461`; Ready `1/1`; restart count `0`; health
  `9/9`.
- Conversation `nyxid-chat-c40e91a694c70606d44fed169c697314`; initial turn
  `turn-f3745b72b8abe1b27691c522fd178c8d`; initial task
  `task-7ae33a60d0dcec561c650988a567a952`.
- Steering was accepted at StateVersion `27`. The same task advanced to plan
  revision `5`, preserving completed web evidence as `done`; all built-in
  `web_search` effects remained `not_applied`.
- Steering created authoritative continuation turn
  `turn-c7aa565b0e77180b63fe920d4f9d8cd0`. A stop against the superseded turn was
  honestly rejected with `NYXID_CHAT_CONTROL_IDENTITY_MISMATCH`; the stop
  against the authoritative turn was accepted and committed at StateVersion
  `50` with `NYXID_CHAT_STOP_ACCEPTED`.
- A subsequent request created distinct task
  `task-e97244afe7b88b6b15f26f6d7f3d1fc5` and turn
  `turn-4abd5440b1a73dbcfda82fe62bcc9a3c`; the task succeeded and its step was
  `done/externalEffect=not_applied`.
- Reload at StateVersion `56` retained both stopped turns for the prior task
  while the new task remained authoritative and succeeded. Cleanup was
  accepted, actor commits `57` through `59` completed deletion, and subsequent
  state and transcript reads returned `404 not_found`.

### UC3: Reconcile Before Retry

- Deployment anchor: source `b5f32cbbeb09f150b9d32ba8684926f61d40bfc9`;
  image `docker.io/aelfdevops/aevatar-console-backend:b5f32cbb`; digest
  `sha256:278e29c2e098f85dde8e8baacbd3a100fca7eb4029acea723d3b8b4873e82b59`;
  generation `2906`; revision `1488`; Ready `1/1`; restart count `0`; health
  `9/9`.
- Bounded UTC evidence: started `2026-08-09T23:18:46Z`; finished
  `2026-08-09T23:21:28Z`.
- Conversation `nyxid-chat-239efbf34b0742bff1f67970fa9a2abf`; turn
  `turn-71637a93e0807be320da4ba276cab454`; task
  `task-f52c79c133304ac35335c431933e96eb`.
- Generation `1` produced NyxID approval request
  `e1748b89-8c90-4cd1-8bab-82cf89098412`, which was rejected. The operation
  became `uncertain/externalEffect=may_have_changed`; exact reconciliation then
  proved `not_applied`.
- Generation `2` produced fresh NyxID approval request
  `d22a723a-f66a-4ef3-8231-abf2c5db3366`, which was approved. Aevatar
  correctly retained no generation-2 `approvalRequestId` or
  `approvalObservation`; effect and postcondition were confirmed against
  provider instance `4EAE494E-FB7D-4294-903D-518E76B5950F`.
- Final StateVersion `31` retained plan revision `4`, both generations, the
  reconciliation result, the absent generation-2 Aevatar approval fact, and
  exactly one terminal; the task succeeded.
- Cleanup used fresh approval request
  `c5065560-6c6f-47f3-af2e-77faed821b1a`. Provider read-back reported
  `CANCELED`; the conversation was deleted; grants created were `0`.

### UC4: Conditional Bitable Write

- Deployment anchor: source `4c3b33ad272807430ca3ba6bc753f849f8c8fe5b`;
  image `docker.io/aelfdevops/aevatar-console-backend:4c3b33ad`; digest
  `sha256:58d05dd6d2bf6b6d766159a2678f54ed806fcf2660eb5aafc729e4449c8f785e`;
  generation `2908`; revision `1489`; Ready `1/1`; restart count `0`; health
  `9/9`.
- Bounded UTC evidence: started `2026-08-09T23:57:26Z`; finished
  `2026-08-10T00:12:49Z`.
- Observed value `72`: conversation `nyxid-chat-9370f82e717b838564fcb28226dbb046`;
  turn `turn-f84c89859c65d5acbeca67a176eb7e2c`; task
  `task-6d77eaf37a14049f69e98573edcf121b`; StateVersion `19`; plan revision `4`.
  Threshold `75` compared with observed value `72`, so the condition committed false. The
  guarded write and postcondition were skipped, no approval was created, and no
  effect was applied. The conversation was deleted.
- Observed value `80`: conversation `nyxid-chat-25d486381c0bb02db188e214e424fcdd`;
  turn `turn-6524bd6133728a1ab4cf82927100c275`; task
  `task-8e8d96ffd4aa5f90fd6aa2d45da22b68`; StateVersion `28`; plan revision `5`;
  gate `satisfied`. Threshold `75` compared with observed value `80`, so the condition
  committed true.
- The create used NyxID approval request
  `33ecb863-8118-405f-9158-626e0e30eb1f`; provider record
  `recvrQAoCO2YfK` was returned; exact read-back confirmed the effect and
  postcondition.
- Cleanup used approval request `c71c4f50-4273-43db-b3b2-42ed497f9bc5` and
  deleted the exact provider record. The supervised cleanup resumed from the
  saved provider ID and did not replay create. Both conversations were deleted,
  grants created were `0`, and the prior service overlay was restored.
- On the successful Tier-B synchronous operation, Aevatar approval fields
  remained absent and reload preserved that absence.

Under Tier B, the exact approval request identity for a successful synchronous
NyxID operation remains NyxID decision evidence. Aevatar `approvalRequestId`
and `approvalObservation` remain absent; reload must preserve that absence and
must never fabricate or inherit another generation's approval identity.

## Ownership And Retention

| Responsibility | Owner | Required action |
| --- | --- | --- |
| Canary operator | `@eanz17`; NyxID subject `5d0d7b72-acff-49af-bb1b-9f30bbb7c102` | Run setup, Studio interaction, typed state checks, and cleanup. |
| Identity reset | `@eanz17` | Establish the asserted connected or disconnected start state before each UC. |
| Lark write cleanup | `@eanz17` | Cancel the exact Approval instance and delete the exact Bitable row created by the run. |
| NyxID approval cleanup | `@eanz17` | Keep the service in `per_request`, revoke any unexpected grant, and resolve or expire every request from the run. |
| Evidence retention | `@eanz17` | Retain redacted state/version metadata for 30 days; remove raw temporary files immediately after issue evidence is recorded. |

Canary-created Lark records have no retention window: clean them up in the same
operator session. If cleanup cannot be proved, stop the milestone closure and
record the exact resource identity as an open incident. Never hide an orphan by
deleting only the Aevatar conversation.

## Fixed Identities And Write Targets

```text
NyxID profile:                share-ops
NyxID canonical subject:      5d0d7b72-acff-49af-bb1b-9f30bbb7c102
NyxID canonical email:        eancuznaivy@gmail.com
Lark personal UserService ID: 41b9a19b-3aa8-4be8-a424-b3821b0951e4
Lark endpoint ID:             d6c2ee39-f2b1-460c-ae1f-5ce93037935b
Lark policy service ID:       698404b0-8919-4848-9f4a-c61af64461c4
Lark service slug:            api-lark-bot

Approval definition:          Email access request
Approval code:                9C330885-C70A-4A5D-913A-CBA9A142FFD4
Approval textarea widget:     widget17163600360780001
Approval input widget:        widget17163600454870001
Approval submitter user ID:   ee689459

Bitable app token:            TxGrbUmPQa8Lkus2z9UlEuffgUc
Bitable table:                Asset Attestations
Bitable table ID:             tblLH7cSI4IWX7kF
Canary key field:              Attestation Key
Canary control field:          Control
Canary owner field:            Owner
Canary status field:           Status
Observed value field:          Sequence
Canary timestamp field:        Reviewed At
```

The Bitable row uses a unique `Attestation Key` beginning with `m40-canary-`.
`Control` is `conditional-write`, `Owner` is `eancuznaivy@gmail.com`, `Status` is
`accepted`, `Sequence` carries the observed value, and `Reviewed At` carries
the run timestamp. Do not write to any other table or Approval definition.

The organization-owned UserService
`f681818b-f625-4aef-82f7-8bfd92e426b8` is historical context only. It is not a
target for this run and must not be updated, used for approval policy changes,
or treated as cleanup ownership. Authentication and canary authorization use
the stable NyxID subject above; email text is descriptive and is not an
authorization key.

## Fixed Approval Policy

The selected policy is NyxID Tier B with strict separation:

- mode: `per_request`;
- approval timeout: 30 seconds;
- default grant TTL: 30 days, recorded only as a platform default because
  `per_request` must mint no grant;
- grant scope: none; the pre-run and post-run active grant set for this service
  must be empty;
- reads are approval-free;
- the exact Approval create/cancel and Bitable create/delete paths require a
  fresh per-request decision;
- no Aevatar approval card may be fabricated before NyxID returns a typed
  request identity or terminal outcome.

The operator must snapshot the existing personal UserService policy before
changing it. The permanent safe state for this personal canary service is the
rule set below. A switch to `per_request` revokes active grants and cancels stale
grant-mode requests in NyxID; verify both effects instead of assuming them from
the update receipt.

```bash
set +x
set -euo pipefail
umask 077

export M40_NYXID_PROFILE="share-ops"
export M40_NYXID_SUBJECT="5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
export M40_NYXID_EMAIL="eancuznaivy@gmail.com"
export M40_LARK_USER_SERVICE_ID="41b9a19b-3aa8-4be8-a424-b3821b0951e4"
export M40_LARK_ENDPOINT_ID="d6c2ee39-f2b1-460c-ae1f-5ce93037935b"
export M40_LARK_POLICY_SERVICE_ID="698404b0-8919-4848-9f4a-c61af64461c4"
export M40_LARK_SLUG="api-lark-bot"
export M40_BASE_APP_TOKEN="TxGrbUmPQa8Lkus2z9UlEuffgUc"
export M40_BASE_TABLE_ID="tblLH7cSI4IWX7kF"
export M40_APPROVAL_CODE="9C330885-C70A-4A5D-913A-CBA9A142FFD4"
export M40_APPROVAL_USER_ID="ee689459"

M40_TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-m40-canary.XXXXXX")"
chmod 700 "$M40_TMP_DIR"

nyxid whoami \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/whoami.json"
test "$(jq -r '.id // ""' "$M40_TMP_DIR/whoami.json")" = "$M40_NYXID_SUBJECT"
test "$(jq -r '.email // ""' "$M40_TMP_DIR/whoami.json")" = "$M40_NYXID_EMAIL"
git fetch origin feature/integrate
M40_AEVATAR_SHA="$(git rev-parse origin/feature/integrate)"
test "$(git rev-parse HEAD)" = "$M40_AEVATAR_SHA"
M40_CANARY_SPEC_URL="https://raw.githubusercontent.com/aevatarAI/aevatar/${M40_AEVATAR_SHA}/docs/operations/2026-08-09-m40-lark-canary-openapi.json"

nyxid service show "$M40_LARK_USER_SERVICE_ID" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/lark-service-before.json"
jq -e \
  --arg user_service_id "$M40_LARK_USER_SERVICE_ID" \
  --arg endpoint_id "$M40_LARK_ENDPOINT_ID" \
  --arg service_id "$M40_LARK_POLICY_SERVICE_ID" \
  --arg slug "$M40_LARK_SLUG" \
  '.id == $user_service_id and
   .endpoint_id == $endpoint_id and
   .catalog_service_id == $service_id and
   .slug == $slug and
   .credential_source.type == "personal" and
   .status == "active" and
   .connected == true' \
  "$M40_TMP_DIR/lark-service-before.json" > /dev/null
M40_PREVIOUS_SPEC_URL="$(jq -r '.openapi_spec_url // ""' "$M40_TMP_DIR/lark-service-before.json")"

nyxid service update "$M40_LARK_USER_SERVICE_ID" \
  --openapi-spec-url "$M40_CANARY_SPEC_URL" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/lark-service-spec-update.json"
nyxid service show "$M40_LARK_USER_SERVICE_ID" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/lark-service-mounted.json"
jq -e \
  --arg user_service_id "$M40_LARK_USER_SERVICE_ID" \
  --arg endpoint_id "$M40_LARK_ENDPOINT_ID" \
  --arg service_id "$M40_LARK_POLICY_SERVICE_ID" \
  --arg spec_url "$M40_CANARY_SPEC_URL" \
  '.id == $user_service_id and
   .endpoint_id == $endpoint_id and
   .catalog_service_id == $service_id and
   .credential_source.type == "personal" and
   .openapi_spec_url == $spec_url' \
  "$M40_TMP_DIR/lark-service-mounted.json" > /dev/null

nyxid approval service-configs \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/approval-config-before.json"
nyxid approval grants \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/grants-before.json"
jq -e \
  --arg service_id "$M40_LARK_POLICY_SERVICE_ID" \
  '[.grants[] | select(.service_id == $service_id)] | length == 0' \
  "$M40_TMP_DIR/grants-before.json" > /dev/null

nyxid approval set-config "$M40_LARK_USER_SERVICE_ID" \
  --profile "$M40_NYXID_PROFILE" \
  --require-approval true \
  --approval-mode per_request \
  --default-effect auto_allow \
  --rule 'effect=require_approval;methods=POST;path=/open-apis/approval/v4/instances;mode=per_request' \
  --rule 'effect=require_approval;methods=POST;path=/open-apis/approval/v4/instances/cancel;mode=per_request' \
  --rule 'effect=require_approval;methods=POST;path=/open-apis/bitable/v1/apps/TxGrbUmPQa8Lkus2z9UlEuffgUc/tables/tblLH7cSI4IWX7kF/records;mode=per_request' \
  --rule 'effect=require_approval;methods=DELETE;path=/open-apis/bitable/v1/apps/TxGrbUmPQa8Lkus2z9UlEuffgUc/tables/tblLH7cSI4IWX7kF/records/*;mode=per_request' \
  --output json > "$M40_TMP_DIR/approval-config-set.json"
jq -e \
  --arg service_id "$M40_LARK_POLICY_SERVICE_ID" \
  '.service_id == $service_id and
   .approval_required == true and
   .approval_mode == "per_request" and
   .default_effect == "auto_allow"' \
  "$M40_TMP_DIR/approval-config-set.json" > /dev/null

nyxid approval service-configs \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/approval-config-after.json"
nyxid approval grants \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/grants-after.json"
jq -e \
  --arg service_id "$M40_LARK_POLICY_SERVICE_ID" \
  '[.configs[] |
    select(.service_id == $service_id and
           .approval_required == true and
           .approval_mode == "per_request" and
           .default_effect == "auto_allow" and
           .rules == [
             {effect: "require_approval", methods: ["POST"], mode: "per_request",
              resource_pattern: "/open-apis/approval/v4/instances", verbs: []},
             {effect: "require_approval", methods: ["POST"], mode: "per_request",
              resource_pattern: "/open-apis/approval/v4/instances/cancel", verbs: []},
             {effect: "require_approval", methods: ["POST"], mode: "per_request",
              resource_pattern: "/open-apis/bitable/v1/apps/TxGrbUmPQa8Lkus2z9UlEuffgUc/tables/tblLH7cSI4IWX7kF/records", verbs: []},
             {effect: "require_approval", methods: ["DELETE"], mode: "per_request",
              resource_pattern: "/open-apis/bitable/v1/apps/TxGrbUmPQa8Lkus2z9UlEuffgUc/tables/tblLH7cSI4IWX7kF/records/*", verbs: []}
           ])] | length == 1' \
  "$M40_TMP_DIR/approval-config-after.json" > /dev/null
jq -e \
  --arg service_id "$M40_LARK_POLICY_SERVICE_ID" \
  '[.grants[] | select(.service_id == $service_id)] | length == 0' \
  "$M40_TMP_DIR/grants-after.json" > /dev/null
jq -S \
  --arg service_id "$M40_LARK_POLICY_SERVICE_ID" \
  '[.configs[] | select(.service_id == $service_id)]' \
  "$M40_TMP_DIR/approval-config-after.json" \
  > "$M40_TMP_DIR/approval-config-expected.json"
```

Stop unless the exact personal UserService maps to the expected endpoint and
catalog policy service, that exact UserService was the `set-config` selector,
the receipt and config read-back identify the expected catalog policy service,
mode is `per_request`, the four exact rules are present, the default is
`auto_allow`, and the active grant set for that catalog policy service is empty.
`service-configs` may surface a sibling `user_service_id` for a catalog-owned
policy, so that joined field is not used as policy ownership evidence.

## Deployment Gate

1. Fetch `origin/feature/integrate` and record its full SHA.
2. Wait for the mainnet Deployment image short tag to equal the first eight
   characters of that SHA, with observed generation equal to generation,
   Ready `1/1`, and zero container restarts.
3. Resolve the running image digest and record it with the full source SHA.
4. Through `nyxid proxy request aevatar /api/status`, require `overall=ok` and
   every typed probe healthy.
5. Confirm the exact Lark UserService read-back retains the exact-SHA
   `M40_CANARY_SPEC_URL`; a branch URL or mutable URL fails the gate.
6. Start each canary in a new conversation so Aevatar performs a fresh NyxID
   MCP catalog read. Require the committed tool descriptors to contain the
   reviewed Approval and Bitable operations. A generic proxy fallback, stale
   catalog, or endpoint-name/path inference is a failed gate.

Kubernetes use is read-only: `get`, `describe`, and `logs`. Do not `exec`,
`apply`, delete, restart, or mutate production workloads for this canary.

## Independent UC Setup

### UC1a: disconnected start

1. Assert the authenticated owner has no personal `api-github` UserService.
   Delete only a disposable personal connection owned by the exact canonical
   subject above; never delete an organization-owned or another user's service.
2. Assert a fresh service inventory still reports no personal GitHub binding.
3. Start a new Studio conversation requesting GitHub connection and a read-only
   repository inspection.
4. Read the committed state and record the exact task and plan revision. Require
   the typed browser action to publish directly without a local plan decision.
5. Require one `service.connect` action with a real `actionRequestId`, waiting
   step, exact requested scopes, and exactly one terminal frame.
6. Reload before continuing. The same task, step, action identity, and waiting
   state must rehydrate without duplication.
7. Stop and delete the disposable Aevatar conversation. Re-read state and
   transcript until both are `404 not_found`.

UC1a does not depend on a prior UC and does not connect GitHub.

### UC1b: connected and ready start

1. Independently connect the authenticated owner's disposable personal GitHub
   service through the NyxID OAuth flow. Record only UserService ID, endpoint ID, slug,
   granted scope names, and readiness; never record tokens or OAuth codes.
2. Assert the exact personal UserService is active and the required read scope
   is present before opening Studio.
3. Start a new Studio conversation requesting GitHub connection verification.
4. Read the committed state and record the exact task and plan revision. Require
   the typed postcondition operation to dispatch directly without a local plan
   decision.
5. Require the actor-owned postcondition `service.connected`, a verified
   resource identity, succeeded task, and exactly one terminal frame.
6. Reload and require the same terminal task and action/postcondition identity
   exactly once.
7. Delete the disposable conversation. Delete the disposable personal GitHub
   connection only after every later UC that needs it has finished, then assert
   it is absent.

UC1b does not rely on UC1a having created or continued an action.

## UC3: Reconcile Before Retry

Use only the fixed Approval definition. Generate a unique non-secret canary key
and include it in both fixed form fields. The first generation must enter its
direct typed Tool dispatch and a real NyxID per-request decision, then encounter
the reviewed one-shot fault after the effect dispatch waterline. The fault must be scoped to
the exact conversation, turn, task, step, operation, generation, UserService,
and operation digest. A process-wide timeout, workload restart, network outage,
or ambiguous request mutation is forbidden.

Acceptance sequence:

1. Generation 1 creates a real NyxID per-request approval. Record its exact
   request ID, reject that request on a NyxID surface before its timeout, and
   read `GET /api/v1/approvals/requests/{request_id}/status` with the same
   requester authority. Require the typed status to be exactly `rejected`;
   `expired`, `pending`, a missing request, or an unreadable status fails UC3.
2. Aevatar captures the same request ID with receipt status `denied`, decision
   mode `per_request` or `unknown`, and terminal outcome `rejected`. Generation
   1 then becomes `uncertain` with `externalEffect=may_have_changed`; retry is
   unavailable. The production policy read-back above, not a missing field in
   the NyxID 7001 body, proves the effective decision mode is `per_request`.
3. The actor adds a read-only reconciliation step in the same task and a higher
   plan revision.
4. Exact typed reconciliation calls
   `GET /open-apis/approval/v4/instances/{instance_id}` with the caller UUID
   supplied to create. Only Lark provider code `1390003` proves `not_applied`;
   a timeout, malformed response, or any other miss is `unavailable`.
5. The actor exposes `retry`; retry creates and directly dispatches generation 2
   with the exact `not_applied` source-operation proof.
6. Generation 2 produces a fresh NyxID approval request. Record its exact ID
   from the NyxID decision surface, prove it differs from generation 1, and
   approve it there. No Aevatar pre-return approval card is permitted. Under
   Tier B, an approved synchronous proxy call returns only the downstream
   success response, so the Aevatar step must leave `approvalRequestId` and
   `approvalObservation` absent instead of inheriting generation 1 or guessing
   the NyxID-owned generation-2 identity.
7. The create operation returns a provider-generated instance code; an exact
   GET verifies the instance. The task succeeds with `externalEffect=confirmed`.
8. Reload reproduces both generations, the generation-1 returned approval fact,
   the absent generation-2 Aevatar approval fact, reconciliation, the verified
   instance, and one terminal. The exact generation-2 request ID remains part
   of the NyxID decision evidence and cleanup manifest.
9. Cancel the exact instance and require an exact GET to report the provider's
   canceled/`RECALL` status.

Do not run UC3 until the narrow one-shot fault mechanism is present on the exact
deployed image and reviewed as disabled by default. Fixture-only failure
injection does not satisfy this production step.

Before the source LLM operation completes, read the exact conversation `/state`
document. Select its single active running LLM step and record the conversation
actor ID, active turn ID, task ID, source step ID, source operation ID, source
`operationGeneration=1`, and top-level `stateVersion`. Separately use the exact
disposable UserService ID established by the UC setup. Do not derive, edit, or
reuse any of these values. Using only the authenticated canonical subject above,
POST those exact source-operation values plus the exact UserService ID, a fresh
`armId`, `clientRequestId`, and an expiry no more than 15 minutes ahead to:

```text
/api/scopes/{scopeId}/nyxid-chat/conversations/{conversationActorId}:arm-effect-fault-canary
```

The request does not carry `catalogDigest` or a target Tool operation identity,
because neither exists at arm time. The conversation actor validates that the
source LLM operation is still active and commits the owner-bound arm intent.
When that exact LLM result materializes one generation-1 effect Tool operation,
the actor reads its committed admission, validates the UserService and digest,
then atomically seals the target operation key and digest into the private
one-shot directive before direct dispatch.

The accepted receipt is only dispatch evidence. Poll `/state` and require the
same `armId`, exact `sourceOperation`, and increasing actor `stateVersion` while
the typed canary status progresses `armed -> forwarded -> consumed`.
`targetOperation` must be absent while `armed`, then match the directly
dispatched effect Tool when `forwarded`. Do not expose or record owner subject,
UserService ID, catalog digest, or client request ID from the canary snapshot.
`consumed` must appear before accepting the generation-1 uncertain result. Any
other owner, default/non-Mainnet composition, disabled configuration, identity mismatch,
duplicate matching step, stale version, generation other than 1, or expired arm
must fail closed; a non-allowlisted caller must receive `404` before scope
admission or actor dispatch. Stop if any status, identity, or version transition
cannot be proved from the typed read model.

## UC4: Conditional Bitable Write

Run two independent conversations with unique canary keys:

1. Observed value 72 with threshold 75. The condition is committed false; write and
   read-back steps are `skipped/not_applied`; no NyxID approval request and no
   Bitable row may exist.
2. Observed value 80 with threshold 75. The condition is committed true; the exact
   Bitable create operation receives a fresh per-request NyxID decision, returns
   a provider-generated record ID, and an exact read/search verifies the unique
   `Attestation Key`, observed value, owner, status, control, and timestamp.
3. Reload preserves the threshold override, branch choice, provider-generated
   record identity, verification, and exactly one terminal. Under Tier B, the
   successful synchronous operation's exact approval ID remains NyxID decision
   evidence; actor `approvalRequestId` and `approvalObservation` remain absent
   and must never be fabricated or inherited from another generation.
4. Delete the exact created record by provider-generated ID. Search the full
   result set for the unique key, following `data.page_token` while
   `data.has_more=true` and stopping only at `data.has_more=false`. A first-page
   miss, page cap, missing token, or malformed pagination is `unavailable`, not
   cleanup evidence.
5. Delete both disposable Aevatar conversations and require state and transcript
   `404 not_found` for each.

## Evidence Contract

For each UC record only:

- exact full Aevatar source SHA, image tag and digest, deployment generation,
  revision, readiness, and restart count;
- pinned NyxID source/registry revision and mounted per-instance spec URL;
- bounded UTC start/end timestamps;
- conversation, turn, task, step, operation, action, and approval request IDs;
- committed `StateVersion`, task/step statuses, external-effect evidence,
  plan-revision and operation-generation numbers;
- terminal frame count/type and redacted cleanup read-back status.

For a successful synchronous Tier-B operation, the exact approval request ID
belongs to NyxID decision evidence. Aevatar actor evidence must record the
absence of `approvalRequestId` and `approvalObservation`; it must not infer the
ID from list order, timing, another generation, or another system's approval.

Do not attach credentials, bearer headers, raw tool arguments or results,
approval reasons, OAuth/device codes, form values, user content, browser
storage, cookies, or Kubernetes secret material. Correlate production logs by
known IDs and timestamps. LLM prose is never effect or cleanup evidence.

## Restore The Service Overlay

After every created Approval and Bitable record has passed exact cleanup,
restore the snapshotted personal UserService spec. An empty prior value is
restored by explicitly clearing the override. Re-read that exact UserService
and start no further canary turn until its prior value is visible. Do not mutate
the historical organization-owned UserService during setup or restore.
Only the OpenAPI overlay is restored: the personal service approval policy must
remain at the exact `per_request` configuration established above, with no
active grants. The final sorted policy comparison and grant query below prove
that post-cleanup state.

```bash
if [ -n "$M40_PREVIOUS_SPEC_URL" ]; then
  nyxid service update "$M40_LARK_USER_SERVICE_ID" \
    --openapi-spec-url "$M40_PREVIOUS_SPEC_URL" \
    --profile "$M40_NYXID_PROFILE" \
    --output json > "$M40_TMP_DIR/lark-service-spec-restore.json"
else
  nyxid service update "$M40_LARK_USER_SERVICE_ID" \
    --openapi-spec-url "" \
    --profile "$M40_NYXID_PROFILE" \
    --output json > "$M40_TMP_DIR/lark-service-spec-restore.json"
fi

nyxid service show "$M40_LARK_USER_SERVICE_ID" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/lark-service-restored.json"
jq -e \
  --arg user_service_id "$M40_LARK_USER_SERVICE_ID" \
  --arg endpoint_id "$M40_LARK_ENDPOINT_ID" \
  --arg spec_url "$M40_PREVIOUS_SPEC_URL" \
  '.id == $user_service_id and
   .endpoint_id == $endpoint_id and
   .credential_source.type == "personal" and
   (.openapi_spec_url // "") == $spec_url' \
  "$M40_TMP_DIR/lark-service-restored.json" > /dev/null

nyxid approval service-configs \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/approval-config-final.json"
nyxid approval grants \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/grants-final.json"
jq -S \
  --arg service_id "$M40_LARK_POLICY_SERVICE_ID" \
  '[.configs[] | select(.service_id == $service_id)]' \
  "$M40_TMP_DIR/approval-config-final.json" \
  > "$M40_TMP_DIR/approval-config-final-target.json"
cmp \
  "$M40_TMP_DIR/approval-config-expected.json" \
  "$M40_TMP_DIR/approval-config-final-target.json"
jq -e \
  --arg service_id "$M40_LARK_POLICY_SERVICE_ID" \
  '[.grants[] | select(.service_id == $service_id)] | length == 0' \
  "$M40_TMP_DIR/grants-final.json" > /dev/null
```

## Completion Gate

Milestone 40 may close only when all five UC rows have deterministic fixture
evidence and authenticated production evidence, every canary write has an exact
cleanup result, all grants/bindings are accounted for, the repository gates pass,
and all code and evidence are pushed to `origin/feature/integrate`. Close #3318
first, then close Milestone 40. Do not close either from a local branch or an
undeployed SHA.
