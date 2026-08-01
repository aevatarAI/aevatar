# Finance Workflow Definitions Acceptance Design

## Goal

Migrate the Aevatar workflow artifacts under
`~/workflows/2026-07-30-for-aevatar-team` to the deployed typed external-capability
contract, then verify every retained artifact at the strongest safe production boundary.
Read-only and preview paths must finish successfully. Lark message delivery and approval
creation must stop after admission and binding until a separate explicit mutation approval.

This design is an acceptance-layer extension of
`docs/superpowers/specs/2026-07-30-finance-workflow-acceptance-design.md`. Production
acceptance exposed one narrow platform adapter defect in addition to the local artifact
migration: explicit admission fetched NyxID's execution inventory from `/api/v1/keys` but
parsed it as the `/api/v1/user-services` response contract. This document therefore owns
that adapter correction, local artifact disposition, and the production acceptance procedure.

## Facts recovered from the current artifacts

The package contains six Aevatar artifacts and two unrelated n8n exports:

| Artifact | Current meaning | Current defect | Disposition |
|---|---|---|---|
| `attach-probe.provision-body.json` | One-step image/PDF ingress and extraction probe | The definition is valid; earlier image runs failed before caller LLM context was propagated | Retain and rerun |
| `invoice_full_chain.v2-fileinput.workflow.yaml` | Early file-input invoice preview | It duplicates v5 and embeds NyxID URL, bearer handling, and `fetch()` inside `code_execute` | Delete |
| `invoice_file_chain.v5.workflow.json` | Canonical image-input invoice preview and optional approval flow | NyxID route identity is supplied through legacy runtime arguments | Retain and migrate |
| `invoice_file_chain.v6-drain.workflow.json` | Lark message/file drain, extraction, preview, and optional approval flow | It references retired UserService identities and legacy runtime route arguments | Retain only if exact current authority can be proven; otherwise preserve a typed blocker |
| `budget_monitor_weekly.nosend.yaml` | Read-only budget calculation | It directly names legacy per-user NyxID tools | Retain, migrate, bind, and run |
| `budget_monitor_weekly.workflow.yaml` | Budget calculation followed by Lark message delivery | It directly names legacy per-user NyxID tools | Retain and migrate; bind only |
| Root-level n8n JSON exports | n8n source workflows | They are not Aevatar workflow definitions | Exclude from Aevatar acceptance |

The deployed platform rejects both stale forms deliberately:

- direct `nyxid_*__*` tool names fail with
  `NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED`;
- runtime `slug/service_id/method/path/response_mode` route bags cannot author or
  widen an actor-owned proof.

Authenticated production inventory currently returns multiple active Lark UserService
candidates. A candidate is usable only when its exact identity, owner scope, credential
authority, and intended tenant match the private artifact. Slug or display-name equality is
not authority. Exact UserService IDs and all tenant resource IDs remain outside the repository
and are injected only into the local acceptance copies.

## Semantic decision

Known Lark HTTP contracts use `tool: nyxid_proxy` with a step-owned
`capability.nyxid_request`. We do not depend on MCP endpoint discovery for this migration.
Each call site declares one exact UserService, static method, normalized path template,
runtime slots, body mode, and response mode. The binder previews and explicitly confirms
the canonical digest and risk before the definition actor commits the grant.

This is the smallest correct migration because the request shapes are already known and
the deployed platform already implements `AuthoredRequest(request_contract_digest)`. It
requires no compatibility shim or second runtime path; the only platform change is to parse
the existing `/keys` response using its own strict typed contract.

The migration must not:

- infer a UserService from slug, display name, route position, or an old ID;
- put credentials, route identities, methods, paths, response modes, or digests in runtime
  arguments;
- call NyxID or Lark from `code_execute`;
- preserve a duplicate artifact solely for historical compatibility;
- treat successful admission or an accepted dispatch receipt as a successful workflow run.

## NyxID execution-inventory authority

NyxID `service list` calls `GET /api/v1/keys`. Both `/keys` and `/user-services` begin
from the same caller-visible UserService and credential-source list, but they expose different
contracts:

- `/user-services` is the UserService route-configuration projection. It proves identity,
  active configuration, and caller credential-source access, but not current execution
  readiness.
- `/keys` is the combined discovery and execution view. It additionally exposes the exact
  credential status and enriched node status used to decide whether the proxy route can run.

Explicit authored-request admission therefore continues to call `/api/v1/keys`. The adapter
parses only the published `keys` envelope; it does not accept `services`, `items`, or `data`
as compatibility aliases. Duplicate IDs, unknown credential or node states, and a node route
without `node_status` fail closed as a stale source.

Readiness mirrors NyxID proxy routing:

- a direct route requires `is_active=true`, caller access, and credential `status=active`;
- a node route requires `is_active=true`, caller access, and `node_status=online`;
- an online node route may legitimately carry a non-active server credential such as
  `pending_auth`, because the node supplies the execution credential;
- `offline`, `draining`, or `unknown` nodes are unavailable, while `inaccessible` is an
  access-denied result.

Credential and node states contribute to the source digest so a readiness transition
invalidates an older admission proof. `/user-services` remains available to callers that
actually consume route configuration; its parser is not repurposed for execution admission.

## Canonical call-site mapping

Every retained external call uses this shape:

```yaml
- id: read_records
  type: tool_call
  capability:
    nyxid_request:
      user_service_id: usvc-finance-lark
      method: GET
      path_template: /open-apis/bitable/v1/apps/{app_token}/tables/{table_id}/records
      query_parameters: [page_size, field_names, filter]
      header_parameters: []
      body_mode: none
      response_mode: text
  parameters:
    tool: nyxid_proxy
    arguments: >-
      {"path_params":{"app_token":"app-alpha","table_id":"table-alpha"},
       "query":{"page_size":"500","field_names":"[...]"}}
```

The repository never stores `usvc-finance-lark`, `app-alpha`, or `table-alpha` as
production values. They are sanitized examples. The local migrated copies use the exact
current values already present in the private package or returned by authenticated NyxID
inventory.

### P2 budget monitor

All six Bitable reads share one authored request contract. Their call-site IDs and runtime
path/query values remain distinct. The contract is `GET`, `body_mode=none`,
`response_mode=text`, with a trusted `read_only` binder attestation.

The no-send artifact ends after calculation and card construction. Its acceptance requires:

- all Bitable call sites admitted with matching grants;
- the first read returns a non-empty Lark data envelope;
- every calculation step completes;
- the terminal read model reports `lastSuccess=true` and a non-empty final output.

The send artifact adds one `POST` message call with `body_mode=json` and
`response_mode=text`. It is write-risk, approval-required, and interactive-only. The
acceptance run must not execute this call. Success for this artifact means preview, explicit
grant confirmation, and bind complete with no Lark message created.

### P1 v5 image-input preview

The image path remains the canonical invoice preview:

1. multipart ingress produces typed `input_file_refs`;
2. `document_extract` produces non-empty text;
3. the LLM and deterministic transformations build the invoice preview;
4. read-only Lark lookup/dedup calls use authored request proofs;
5. `submit=false` selects the preview branch and never dispatches approval creation.

Read-only calls use trusted `read_only` grants. Approval creation remains a separate
write-risk authored request. The definition may bind with that call site granted, but the
production acceptance input must deterministically select the preview branch.

### P1 v6 drain

The drain definition is valid only if authenticated inventory proves one active exact
UserService that can serve both message listing and file download for the intended Lark
tenant. A same-name or same-slug service is not evidence.

If exact authority is proven:

- message listing is an authored `GET .../im/v1/messages` text request;
- file download is an authored static `GET` path template with declared message/file
  path slots and fixed `response_mode=file_artifact`;
- the resulting managed file reference feeds `document_extract`;
- downstream preview follows the v5 no-submit boundary.

If exact authority cannot be proven, the artifact is not rebound to a guessed service.
Acceptance records a typed readiness blocker with no member or serving revision presented
as runnable. This is an honest terminal outcome for the artifact migration, not a passing
workflow run.

### P1 v2 deletion

The v2 YAML is deleted from the private package after v5 passes the same image-input preview
acceptance. No redirect or compatibility copy is created. The package README points to v5
as the canonical image-input workflow and explains that v2 was removed because it bypassed
typed capability admission.

## Preview, grant, provision, and run sequence

This acceptance uses the existing member binding surface in interactive mode. It does not use
`provision-workflow`: that facade derives workflow and revision identities internally and
selects durable admission whenever `RunImmediately=true` or a cron is present. A client must
not reproduce `BuildProvisionKey` to predict those identities, and any definition containing a
POST/PUT/PATCH/DELETE authored request is interactive-only.

For an existing member, read its exact member detail and binding read models. Use the returned
workflow identity, never the path member ID or published service ID, and allocate one fresh
opaque revision ID for this bind attempt. If a retained artifact has no suitable member, create
a member through the member API with explicitly chosen, mutually distinct member, workflow,
and revision IDs; do not derive one identity from another. The same exact workflow and revision
IDs must appear in preview, every confirmation, and bind.

Each authored-request workflow then follows one two-phase bind protocol:

1. Submit the exact YAML to
   `POST /api/scopes/{scopeId}/workflows:explicit-request-preview` with the intended
   `interactive` execution mode and explicit `workflowId` and `revisionId`.
2. Require one preview item per external call site. Record only safe fields:
   `callSiteId`, `requestContractDigest`, method, path template, effective risk,
   approval requirement, and allowed execution modes.
3. Check that every item matches the local definition. A missing, extra, duplicate, or
   mismatched call site stops the migration.
4. Submit one `explicitRequestConfirmations` item per preview item during member bind. Each
   item contains all five required fields: `callSiteId`, `requestContractDigest`,
   `attestedRisk`, `workflowId`, and `revisionId`. The last two values exactly equal the
   preview request/result identities; digest and risk exactly equal that call site's preview.
5. Verify the returned member, workflow, revision, and published service as distinct
   identities. Do not infer one from another.
6. Run only artifacts allowed by the acceptance matrix below.
7. Read the member run catalog with `take` explicitly supplied and then read the exact
   run/audit resources. An SSE terminal and read-model terminal must agree.

Selector or YAML changes invalidate prior previews. The migration reruns preview and submits
new confirmations; it never reuses stale digests.

Interactive admission is deliberate even for the P2 no-send GET workflow. Trusted read-only
risk is necessary but not sufficient for durable admission: durable also requires the exact
service's active, fresh, owner-matched durable authorization catalog. Manual interactive invoke
is sufficient to answer whether the retained definition runs. Weekly schedule readiness is a
separate follow-up and is not part of this acceptance's success claim.

## Production acceptance matrix

| Artifact | Preview | Grant and bind | Execute | Required terminal |
|---|---:|---:|---:|---|
| Attachment probe, image | N/A | Existing or fresh bind | Yes | completed, `lastSuccess=true`, non-empty extracted text |
| Attachment probe, PDF | N/A | Existing or fresh bind | Yes | typed supported/unsupported extraction result documented without guessing |
| P2 no-send | Yes, interactive | Yes, interactive | Yes, manual interactive invoke | completed, `lastSuccess=true`, non-empty first read and final output |
| P2 send | Yes, interactive | Yes, interactive | No | binding succeeds; no message run is created |
| P1 v5 preview | Yes, interactive | Yes, interactive | Yes with `submit=false` | completed, `lastSuccess=true`, non-empty extraction and preview |
| P1 v6 drain | Yes, interactive, when exact authority exists | Yes when ready | Yes, preview only | completed preview, or typed pre-bind blocker with no runnable revision |
| P1 v2 | No | No | No | file removed after v5 acceptance |

All production calls use the signed-in CLI surface:

```text
nyxid proxy request aevatar ...
```

No direct backend URL, copied bearer, browser cookie, Kubernetes `exec`, or service
credential is used. Kubernetes access is read-only and only correlates safe run identifiers.

## Error handling and evidence

For every artifact, record:

- local file hash before and after migration;
- preview call-site count and safe digest prefixes;
- member ID, workflow ID, published service ID, and revision ID as distinct values;
- command ID, correlation ID, short run ID, terminal status, and read-model state version;
- first failing step and typed error code for failures;
- proof that no write call ran for bind-only or preview-only artifacts.

Never record bearer values, raw Lark responses containing business data, document bytes,
full invoice text, tenant resource IDs, or approval/message payloads in Git, GitHub issues,
or normal logs.

An accepted receipt proves dispatch only. A successful artifact requires a terminal run and
matching read-model evidence. A typed admission blocker is reported as a blocker, never as a
successful run.

## Independent platform defect found during discovery

A production read-only invocation of `list_external_workflow_capabilities` executed the
tool but the NyxID Assistant terminated it as `tool_outcome_unknown`. This indicates that
the binding read tool does not emit a verifiable typed receipt through the Assistant turn
executor. It is separate from the finance definitions because known HTTP contracts can use
`nyxid_request` without MCP discovery.

Create a focused issue containing only safe conversation/turn/operation identifiers and the
typed terminal. Do not expand this migration into a general receipt refactor. The issue must
require a focused regression proving a successful read-only binding tool emits a typed
success receipt and preserves its result.

## Verification

Before production mutation:

- parse every retained workflow with the repository parser;
- assert no retained artifact contains direct `nyxid_*__*` tool names;
- assert no retained artifact passes route identity through runtime arguments;
- assert no retained artifact contains NyxID URLs, bearer handling, or network `fetch()`
  inside `code_execute`;
- verify each authored selector receives exactly one preview item;
- run the existing focused parser, authorization, request-builder, and Studio provisioning
  tests affected by the artifact shapes.

After local checks, execute the production acceptance matrix in order: attachment probe,
P2 no-send, P1 v5 preview, P1 v6 conditional drain, then bind-only artifacts. All migrated
definitions use interactive preview/bind and manual invoke. Stop on the first unexplained typed
failure, correlate it, and fix the root cause before continuing.

## Non-goals

- no platform compatibility for direct per-user tool names or legacy route bags;
- no generic raw HTTP workflow primitive;
- no production Lark message or approval creation in this acceptance;
- no migration of the root-level n8n exports;
- no guessed replacement for a retired UserService;
- no claim that manual execution proves weekly durable scheduling readiness;
- no second capability catalog or runtime proxy path;
- no repository copy of production workflow values or credentials.
