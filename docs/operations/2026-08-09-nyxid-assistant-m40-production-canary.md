---
title: "NyxID Assistant Milestone 40 Production Canary"
status: active
owner: platform
---

# NyxID Assistant Milestone 40 Production Canary

## Purpose

This runbook is the production acceptance procedure for GitHub issue #3318 and
Milestone 40. It proves UC1a, UC1b, UC2, UC3, and UC4 against the exact Aevatar
revision deployed from `origin/feature/integrate`. `dev` is not a source,
delivery, deployment, comparison, or acceptance branch for this run.

UC2 is already accepted on
`origin/feature/integrate@87daa99e641533f25ea0ddc67396e1a0dc52bd59`.
Every remaining UC must record its own exact deployed full SHA. A branch name,
image short tag, HTTP 2xx, assistant sentence, or visible card is not completion
evidence.

## Ownership And Retention

| Responsibility | Owner | Required action |
| --- | --- | --- |
| Canary operator | `@eanz17` using `share-ops@aelf.io` | Run setup, Studio interaction, typed state checks, and cleanup. |
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
NyxID account:                share-ops@aelf.io
Lark UserService ID:          f681818b-f625-4aef-82f7-8bfd92e426b8
Lark endpoint ID:             1d87990f-958c-44c2-9514-2474d24f3b1a
Lark service slug:            api-lark-bot

Approval definition:          Email access request
Approval code:                9C330885-C70A-4A5D-913A-CBA9A142FFD4
Approval textarea widget:     widget17163600360780001
Approval link widget:         widget17163600454870001
Approval submitter user ID:   ee689459

Bitable app token:            TxGrbUmPQa8Lkus2z9UlEuffgUc
Bitable table:                Asset Attestations
Bitable table ID:             tblLH7cSI4IWX7kF
Canary key field:              Attestation Key
Canary control field:          Control
Canary owner field:            Owner
Canary status field:           Status
Candidate score field:         Sequence
Canary timestamp field:        Reviewed At
```

The Bitable row uses a unique `Attestation Key` beginning with `m40-canary-`.
`Control` is `candidate-screening`, `Owner` is `share-ops@aelf.io`, `Status` is
`accepted`, `Sequence` carries the candidate score, and `Reviewed At` carries
the run timestamp. Do not write to any other table or Approval definition.

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

The operator must snapshot the existing organization policy before changing it.
The permanent safe state for this canary service is the rule set below. A switch
to `per_request` revokes active grants and cancels stale grant-mode requests in
NyxID; verify both effects instead of assuming them from the update receipt.

```bash
set +x
set -euo pipefail
umask 077

export M40_NYXID_PROFILE="share-ops"
export M40_ORG="ChronoAI"
export M40_LARK_USER_SERVICE_ID="f681818b-f625-4aef-82f7-8bfd92e426b8"
export M40_LARK_SLUG="api-lark-bot"
export M40_BASE_APP_TOKEN="TxGrbUmPQa8Lkus2z9UlEuffgUc"
export M40_BASE_TABLE_ID="tblLH7cSI4IWX7kF"
export M40_APPROVAL_CODE="9C330885-C70A-4A5D-913A-CBA9A142FFD4"
export M40_APPROVAL_USER_ID="ee689459"

M40_TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-m40-canary.XXXXXX")"
chmod 700 "$M40_TMP_DIR"

nyxid whoami --profile "$M40_NYXID_PROFILE"
git fetch origin feature/integrate
M40_AEVATAR_SHA="$(git rev-parse origin/feature/integrate)"
test "$(git rev-parse HEAD)" = "$M40_AEVATAR_SHA"
M40_CANARY_SPEC_URL="https://raw.githubusercontent.com/aevatarAI/aevatar/${M40_AEVATAR_SHA}/docs/operations/2026-08-09-m40-lark-canary-openapi.json"

nyxid service show "$M40_LARK_USER_SERVICE_ID" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/lark-service-before.json"
M40_PREVIOUS_SPEC_URL="$(jq -r '.openapi_spec_url // ""' "$M40_TMP_DIR/lark-service-before.json")"

nyxid service update "$M40_LARK_USER_SERVICE_ID" \
  --openapi-spec-url "$M40_CANARY_SPEC_URL" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/lark-service-spec-update.json"
nyxid service show "$M40_LARK_USER_SERVICE_ID" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/lark-service-mounted.json"
test "$(jq -r '.openapi_spec_url // ""' "$M40_TMP_DIR/lark-service-mounted.json")" = \
  "$M40_CANARY_SPEC_URL"

nyxid approval service-configs \
  --org "$M40_ORG" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/approval-config-before.json"
nyxid approval grants \
  --org "$M40_ORG" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/grants-before.json"

nyxid approval set-config "$M40_LARK_USER_SERVICE_ID" \
  --org "$M40_ORG" \
  --profile "$M40_NYXID_PROFILE" \
  --require-approval true \
  --approval-mode per_request \
  --default-effect auto_allow \
  --rule 'effect=require_approval;methods=POST;path=/open-apis/approval/v4/instances;mode=per_request' \
  --rule 'effect=require_approval;methods=POST;path=/open-apis/approval/v4/instances/cancel;mode=per_request' \
  --rule 'effect=require_approval;methods=POST;path=/open-apis/bitable/v1/apps/TxGrbUmPQa8Lkus2z9UlEuffgUc/tables/tblLH7cSI4IWX7kF/records;mode=per_request' \
  --rule 'effect=require_approval;methods=DELETE;path=/open-apis/bitable/v1/apps/TxGrbUmPQa8Lkus2z9UlEuffgUc/tables/tblLH7cSI4IWX7kF/records/*;mode=per_request' \
  --output json > "$M40_TMP_DIR/approval-config-set.json"

nyxid approval service-configs \
  --org "$M40_ORG" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/approval-config-after.json"
nyxid approval grants \
  --org "$M40_ORG" \
  --profile "$M40_NYXID_PROFILE" \
  --output json > "$M40_TMP_DIR/grants-after.json"
```

Stop unless the read-back identifies the same UserService, mode is
`per_request`, the four exact rules are present, the default is `auto_allow`,
and the active grant set for the service is empty.

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

1. Assert the team account has no personal `api-github` UserService. Delete only
   a disposable personal connection owned by `share-ops`; never delete an
   organization-owned or another user's service.
2. Assert a fresh service inventory still reports no personal GitHub binding.
3. Start a new Studio conversation requesting GitHub connection and a read-only
   repository inspection.
4. Require one `service.connect` action with a real `actionRequestId`, waiting
   step, exact requested scopes, and exactly one terminal frame.
5. Reload before continuing. The same task, step, action identity, gate, and
   waiting state must rehydrate without duplication.
6. Stop and delete the disposable Aevatar conversation. Re-read state and
   transcript until both are `404 not_found`.

UC1a does not depend on a prior UC and does not connect GitHub.

### UC1b: connected and ready start

1. Independently connect the team account's disposable personal GitHub service
   through the NyxID OAuth flow. Record only UserService ID, endpoint ID, slug,
   granted scope names, and readiness; never record tokens or OAuth codes.
2. Assert the exact personal UserService is active and the required read scope
   is present before opening Studio.
3. Start a new Studio conversation requesting GitHub connection verification.
4. Require the actor-owned postcondition `service.connected`, a verified
   resource identity, succeeded task, and exactly one terminal frame.
5. Reload and require the same terminal task and action/postcondition identity
   exactly once.
6. Delete the disposable conversation. Delete the disposable personal GitHub
   connection only after every later UC that needs it has finished, then assert
   it is absent.

UC1b does not rely on UC1a having created or continued an action.

## UC3: Reconcile Before Retry

Use only the fixed Approval definition. Generate a unique non-secret canary key
and include it in both fixed form fields. The first generation must pass the
plan gate and a real NyxID per-request decision, then encounter the reviewed
one-shot fault after the effect dispatch waterline. The fault must be scoped to
the exact conversation, turn, task, step, operation, generation, UserService,
and operation digest. A process-wide timeout, workload restart, network outage,
or ambiguous request mutation is forbidden.

Acceptance sequence:

1. Generation 1 becomes `uncertain` with `externalEffect=may_have_changed`.
   Retry is unavailable.
2. The actor adds a read-only reconciliation step in the same task and a higher
   plan revision.
3. Exact typed reconciliation calls
   `GET /open-apis/approval/v4/instances/{instance_id}` with the caller UUID
   supplied to create. Only Lark provider code `1390003` proves `not_applied`;
   a timeout, malformed response, or any other miss is `unavailable`.
4. The actor exposes `retry`; retry passes the plan gate again and enters
   generation 2.
5. Generation 2 produces a fresh NyxID approval request. Approve it on a NyxID
   surface. No Aevatar pre-return approval card is permitted.
6. The create operation returns a provider-generated instance code; an exact
   GET verifies the instance. The task succeeds with `externalEffect=confirmed`.
7. Reload reproduces both generations, reconciliation, captured approval facts,
   verified instance, and one terminal.
8. Cancel the exact instance and require an exact GET to report the provider's
   canceled/`RECALL` status.

Do not run UC3 until the narrow one-shot fault mechanism is present on the exact
deployed image and reviewed as disabled by default. Fixture-only failure
injection does not satisfy this production step.

## UC4: Conditional Bitable Write

Run two independent conversations with unique canary keys:

1. Score 72 with threshold 75. The condition is committed false; write and
   read-back steps are `skipped/not_applied`; no NyxID approval request and no
   Bitable row may exist.
2. Score 80 with threshold 75. The condition is committed true; the exact
   Bitable create operation receives a fresh per-request NyxID decision, returns
   a provider-generated record ID, and an exact read/search verifies the unique
   `Attestation Key`, score, owner, status, control, and timestamp.
3. Reload preserves the threshold override, branch choice, approval facts,
   provider-generated record identity, verification, and exactly one terminal.
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

Do not attach credentials, bearer headers, raw tool arguments or results,
approval reasons, OAuth/device codes, form values, user content, browser
storage, cookies, or Kubernetes secret material. Correlate production logs by
known IDs and timestamps. LLM prose is never effect or cleanup evidence.

## Restore The Service Overlay

After every created Approval and Bitable record has passed exact cleanup,
restore the snapshotted per-instance spec. An empty prior value is restored by
explicitly clearing the override. Re-read the UserService and start no further
canary turn until the prior value is visible.

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
test "$(jq -r '.openapi_spec_url // ""' "$M40_TMP_DIR/lark-service-restored.json")" = \
  "$M40_PREVIOUS_SPEC_URL"
```

## Completion Gate

Milestone 40 may close only when all five UC rows have deterministic fixture
evidence and authenticated production evidence, every canary write has an exact
cleanup result, all grants/bindings are accounted for, the repository gates pass,
and all code and evidence are pushed to `origin/feature/integrate`. Close #3318
first, then close Milestone 40. Do not close either from a local branch or an
undeployed SHA.
