# Finance Workflow Definitions Acceptance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate every retained Aevatar definition in the private finance workflow package to typed NyxID authored-request admission and prove the strongest permitted Mainnet outcome without sending Lark messages or creating approvals.

**Architecture:** Keep the deployed `nyxid_proxy` runtime and actor-owned admission chain. Correct its NyxID inventory adapter to parse the existing `/api/v1/keys` execution view instead of treating it as `/user-services`, then rewrite the private workflow definitions so every external call owns a static `capability.nyxid_request`. Use one interactive preview, five-field confirmation set, and member bind for each definition. Manual invocation proves safe execution; write branches remain bind-only, and a retired service identity remains a typed blocker instead of being guessed.

**Tech Stack:** Aevatar workflow YAML/JSON, `jq`, Ruby Psych for local parsing, .NET workflow parser tests, NyxID CLI, GitHub CLI.

## Global Constraints

- Production Aevatar API calls use only `nyxid proxy request aevatar ...`.
- Do not use direct backend URLs, copied bearer tokens, browser cookies, Kubernetes `exec`, or service credentials.
- Do not send a Lark message and do not create an approval instance.
- Keep `memberId`, `workflowId`, `publishedServiceId`, `revisionId`, and NyxID `user_service_id` distinct.
- Every explicit confirmation contains exact `callSiteId`, `requestContractDigest`, `attestedRisk`, `workflowId`, and `revisionId` values from the matching interactive preview.
- Never infer UserService authority from slug, display name, UUID shape, route position, or a retired ID.
- Do not commit credentials, production UserService IDs, tenant resource IDs, business responses, document text, people, or amounts.
- Manual interactive success does not claim weekly durable scheduling readiness.
- Root-level n8n exports are out of scope.

---

### Task 1: Migrate and statically validate the private definitions

**Files:**
- Modify: `/Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P2-budget-monitor/budget_monitor_weekly.nosend.yaml`
- Modify: `/Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P2-budget-monitor/budget_monitor_weekly.workflow.yaml`
- Modify: `/Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P1-invoice-approval/invoice_file_chain.v5.workflow.json`
- Modify conditionally: `/Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P1-invoice-approval/invoice_file_chain.v6-drain.workflow.json`
- Modify after v5 acceptance: `/Users/eanzhao/workflows/2026-07-30-for-aevatar-team/README.md`
- Delete after v5 acceptance: `/Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P1-invoice-approval/invoice_full_chain.v2-fileinput.workflow.yaml`
- Preserve: `/Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P1-invoice-approval/attach-probe.provision-body.json`

**Interfaces:**
- Consumes: one authenticated, exact Lark UserService identity proven against the private package's owner/tenant authority and held only in a mode-0600 temporary inventory file.
- Produces: retained definitions containing only typed `nyxid_request` selectors and proof-bound runtime slots.

- [ ] **Step 1: Capture private before-state without printing private values**

Run:

```bash
find /Users/eanzhao/workflows/2026-07-30-for-aevatar-team -type f ! -name '.DS_Store' -print0 \
  | sort -z | xargs -0 shasum -a 256 > /tmp/finance-workflows-before.sha256
```

Expected: one hash line per retained package file; keep the file local and uncommitted.

- [ ] **Step 2: Prove the current definitions fail the migration guard**

Run:

```bash
rg -n 'nyxid_[A-Za-z0-9_-]+__|\"(slug|service_id|method|path|response_mode)\"|fetch\(' \
  /Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P1-invoice-approval \
  /Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P2-budget-monitor
```

Expected: matches in P2 direct tool names, P1 legacy proxy route bags, and v2 network code.

- [ ] **Step 3: Migrate P2 reads and the bind-only send call**

For each of the six Bitable read steps, replace the direct per-user tool with this exact shape while preserving the step's current tenant values as runtime values. The values below are deliberately sanitized contract examples; the private copy receives the exact value from the authenticated temporary inventory and the exact values already present in that step:

```yaml
capability:
  nyxid_request:
    user_service_id: usvc-alpha
    method: GET
    path_template: /open-apis/bitable/v1/apps/{app_token}/tables/{table_id}/records
    query_parameters: [page_size, field_names, filter]
    header_parameters: []
    body_mode: none
    response_mode: text
parameters:
  tool: nyxid_proxy
  arguments: >-
    {"path_params":{"app_token":"app-alpha","table_id":"table-alpha"},"query":{"page_size":"500","field_names":"field-alpha","filter":"filter-alpha"}}
```

For the send step, use `POST /open-apis/im/v1/messages`, declare its existing query names, `body_mode=json`, `body_required=true`, and `response_mode=text`; runtime arguments contain only `query` and `body`. Do not execute this definition.

- [ ] **Step 4: Migrate P1 v5**

Replace each `nyxid_proxy` route bag with a call-site-owned `nyxid_request` selector. Use static templates for contact lookup, approval definition/form reads, approval creation, and approval status reads. Move dynamic path segments into `path_params`, query-string values into `query`, and request payloads into `body`. Runtime arguments may contain only `path_params`, `query`, `headers`, and `body`; authored requests omit runtime `response_mode` because it is fixed in the selector. Preserve the existing `submit=false` switch path.

- [ ] **Step 5: Classify P1 v6 without guessing**

If authenticated inventory proves one exact authority for both message listing and file download, migrate `dr_list` to text GET and `dr_dl` to a static GET template with `response_mode=file_artifact`, then migrate downstream calls as in v5. Otherwise leave v6 unchanged and record `NYXID_EXACT_USER_SERVICE_AUTHORITY_UNPROVEN`; do not bind or present it as runnable.

- [ ] **Step 6: Run the local passing guard**

Run:

```bash
jq -e . /Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P1-invoice-approval/*.json >/dev/null
ruby -e 'require "yaml"; ARGV.each { |f| YAML.safe_load_file(f, permitted_classes: [], aliases: false) }' \
  /Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P2-budget-monitor/*.yaml
! rg -n 'nyxid_[A-Za-z0-9_-]+__' \
  /Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P2-budget-monitor
! rg -n '\"(slug|service_id|user_service_id|method|path)\"' \
  /Users/eanzhao/workflows/2026-07-30-for-aevatar-team/P1-invoice-approval/invoice_file_chain.v5.workflow.json
```

Expected: JSON/YAML parse successfully and both negative searches return no match.

### Task 2: Correct explicit admission's NyxID execution inventory contract

**Files:**
- Modify: `docs/canon/nyxid-connected-service-tools.md`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiAccessContracts.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdExplicitWorkflowCapabilitySource.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdApiAccessContractTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdExplicitWorkflowCapabilitySourceTests.cs`
- Test: `test/Aevatar.Workflow.Application.Tests/WorkflowExplicitRequestAdmissionTests.cs`

**Interfaces:**
- Consumes: the published `GET /api/v1/keys` response used by `nyxid service list`.
- Produces: exact UserService readiness from caller access, direct credential status, and
  node dispatchability without introducing a second inventory source.

- [ ] **Step 1: Preserve the observed contract failure**

Use realistic `keys`-envelope fixtures for explicit admission. Before the production change,
verify the focused source suite fails as `SourceStale` because `ParseUserServices` requires a
`services` envelope.

- [ ] **Step 2: Add a strict typed `/keys` parser**

Keep `ParseUserServices` for actual `/user-services` consumers. Add a separate typed parser
for `/keys` that requires exact IDs, `status`, `is_active`, `credential_source`, and the
node ID/status pair. Reject the `services` envelope, duplicate IDs, unknown statuses, and a
node ID without a node status. Ignore only unrelated additive fields.

- [ ] **Step 3: Mirror NyxID proxy readiness**

Use the `/keys` parser in explicit admission. Require `active` credentials for direct routes.
For node routes, require `node_status=online` but permit a non-active server credential because
NyxID's node agent supplies it. Return typed access, credential, or node blockers and include
credential/node state in the source digest.

- [ ] **Step 4: Run focused regression tests**

Run:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --no-restore --nologo \
  --filter 'FullyQualifiedName~NyxIdApiAccessContractTests|FullyQualifiedName~NyxIdExplicitWorkflowCapabilitySourceTests'
```

Expected: parser and admission tests pass for direct active/inactive credentials, online and
unavailable node routes, strict malformed responses, exact identity, and caller access.

### Task 3: Preview and bind the interactive definitions

**Files:** no repository files; request/response bodies live in a mode-0700 temporary directory and are deleted after redacted evidence is recorded.

**Interfaces:**
- Consumes: migrated YAML, exact member read models, and exact production UserService inventory.
- Produces: one successful binding run per ready definition, with no schedule and no business mutation.

- [ ] **Step 1: Confirm the authenticated CLI contract**

Run:

```bash
nyxid whoami
nyxid proxy request --help
```

Expected: signed-in identity succeeds; proxy help documents stdin/file JSON and streaming flags. Do not print credentials.

- [ ] **Step 2: Re-read exact member and UserService facts**

Use NyxID CLI only. Require each selected member detail to expose a workflow implementation reference and a published service identity distinct from member identity. Require the chosen UserService inventory record to be active, caller-visible, credential-allowed, and exact-owner/tenant matched. A missing or ambiguous match stops that artifact.

- [ ] **Step 3: Allocate explicit bind identities**

For each ready existing member, take `workflowId` from its implementation read model and generate one opaque `revisionId` locally with `revision-$(uuidgen | tr '[:upper:]' '[:lower:]')`. Do not use the member ID or published service ID as either value.

- [ ] **Step 4: Preview every ready definition in interactive mode**

POST each exact YAML to `/api/scopes/{scopeId}/workflows:explicit-request-preview` with `executionMode=interactive`, the exact `workflowId`, and exact `revisionId`. Assert the returned identities match and that call-site IDs are unique and equal the local authored-request call sites.

- [ ] **Step 5: Build the exact five-field confirmation array**

Map each preview item to:

```json
{
  "callSiteId": "preview item callSiteId",
  "requestContractDigest": "preview item requestContractDigest",
  "attestedRisk": "preview item effectiveRisk",
  "workflowId": "same preview workflowId",
  "revisionId": "same preview revisionId"
}
```

Reject missing, extra, duplicate, or mismatched items before mutation.

- [ ] **Step 6: Bind and observe actor-owned completion**

PUT `/api/scopes/{scopeId}/members/{memberId}/binding` with the exact workflow/revision identities, exact YAML, and confirmations. Poll only the binding-run read model returned by the accepted receipt until `succeeded`, `failed`, or `rejected`; accepted alone is not success. Do not create a schedule.

### Task 4: Run the permitted acceptance matrix

**Files:** no repository files; safe evidence contains identifiers and status only.

**Interfaces:**
- Consumes: ready interactive bindings and safe local image/PDF fixtures.
- Produces: SSE terminal plus exact member-run read-model evidence for each executed artifact.

- [ ] **Step 1: Re-run attachment image and PDF probes**

Invoke the attachment member with one sanitized image, then one sanitized PDF, through `nyxid proxy request aevatar ... --stream`. Require a real run ID. Image must finish with `lastSuccess=true` and non-empty extraction output. PDF records its actual typed supported/unsupported outcome without guessing.

- [ ] **Step 2: Run P2 no-send**

Invoke only the no-send member with prompt `run`. Require SSE terminal completion, then `GET /api/scopes/{scopeId}/members/{memberId}/runs?take=20` and the exact run/audit read models. Pass only when `lastSuccess=true`, the first Bitable step is non-empty, and final output is non-empty. Do not invoke the send member.

- [ ] **Step 3: Run P1 v5 preview**

Invoke v5 with a sanitized image and input selecting `submit=false`. Require non-empty extraction and preview output plus matching SSE/read-model terminal success. Assert no approval-creation step executed.

- [ ] **Step 4: Run P1 v6 only if Task 1 proved exact authority**

When ready, invoke its no-submit preview path and require managed `file_artifact` ingestion followed by extraction and preview success. Otherwise report the typed authority blocker and create no binding/run.

- [ ] **Step 5: Bind-only verification for write definitions**

Verify P2 send and P1 v5's write call sites were admitted in interactive mode and their binding runs succeeded. Confirm no production run reached the Lark send or approval-create call sites.

- [ ] **Step 6: Remove the duplicate v2 and update the package README**

Only after v5 preview passes, delete v2 and replace obsolete issue/deployment instructions in the README with the canonical artifact matrix, typed admission requirement, exact run results, v6 blocker/result, and the explicit statement that send/approval and weekly durable scheduling remain untested.

### Task 5: Record the independent receipt defect and finish verification

**Files:**
- Verify: `docs/superpowers/specs/2026-08-01-finance-workflow-definitions-acceptance-design.md`
- Verify: `docs/superpowers/plans/2026-08-01-finance-workflow-definitions-acceptance.md`

**Interfaces:**
- Consumes: safe production conversation/turn/operation/tool-call identifiers and `tool_outcome_unknown`.
- Produces: one non-duplicate GitHub issue plus final redacted acceptance ledger.

- [ ] **Step 1: Search before creating the issue**

Run a read-only `gh issue list --search` for `list_external_workflow_capabilities tool_outcome_unknown`. If an open issue already describes the same receipt loss, append safe reproduction evidence instead of creating a duplicate.

- [ ] **Step 2: Create or update the focused issue**

The issue requires a regression where a successful read-only binding tool emits a typed success receipt and preserves its result through the Assistant turn executor. Include only safe conversation/turn/operation/tool-call IDs and the typed terminal; exclude UserService IDs, tenant data, tokens, or business responses.

- [ ] **Step 3: Run completion checks**

Run:

```bash
bash tools/docs/lint.sh
git diff --check
find /Users/eanzhao/workflows/2026-07-30-for-aevatar-team -type f ! -name '.DS_Store' -print0 \
  | sort -z | xargs -0 shasum -a 256 > /tmp/finance-workflows-after.sha256
```

Compare before/after hashes, verify only intended private artifacts changed, and delete temporary request/response files.

- [ ] **Step 4: Commit and integrate repository documentation**

Commit the plan separately, fetch `origin/feature/integrate`, rebase the isolated branch if needed, rerun docs lint and `git diff --check`, then push the verified HEAD to `origin/feature/integrate` with an explicit refspec. Do not commit the private workflow package.

- [ ] **Step 5: Report exact outcomes**

For each artifact report one of: terminal success with run/read-model evidence, bind-only success with proof no write ran, or typed blocker. Never summarize a blocker or accepted receipt as “all workflows succeeded.”
