---
title: NyxID workflow scope service integration runbook
status: active
owner: codex
issue: 1981
---

# NyxID Workflow Scope Service Integration Runbook

## Scope

This issue is zero production code. The validation path must use existing Aevatar scope service endpoints and the existing NyxID downstream/proxy surfaces. Do not add a workflow tool, NyxID client, availability fallback, automatic registration, or any NyxID repository change for this runbook.

This worker has no live secrets or external environment. All response samples below are placeholders or evidence checklist items. Replace placeholders with redacted live evidence only when running in an authorized environment.

## Inputs

| Name | Placeholder |
|---|---|
| Aevatar host | `<AEVATAR_BASE_URL>` |
| NyxID host | `<NYXID_BASE_URL>` |
| Caller token | `<NYXID_OR_AEVATAR_BEARER>` |
| Scope id | `<SCOPE_ID>` |
| Service id | `<SERVICE_ID>` |
| Revision id | `<REVISION_ID>` |
| Endpoint id | `<ENDPOINT_ID>` |
| NyxID slug | `<NYXID_SLUG>` |
| Run id | `<RUN_ID>` |

## Publish Scope Service

Use the existing scope binding surface to publish a workflow-backed service in a scope:

```bash
curl -sS -X PUT "$AEVATAR_BASE_URL/api/scopes/$SCOPE_ID/binding" \
  -H "Authorization: Bearer $NYXID_OR_AEVATAR_BEARER" \
  -H "Content-Type: application/json" \
  -d '{
    "implementationKind": "workflow",
    "displayName": "<DISPLAY_NAME>",
    "serviceId": "<SERVICE_ID>",
    "revisionId": "<REVISION_ID>",
    "workflow": {
      "workflowId": "<WORKFLOW_ID>",
      "workflowYamls": ["<WORKFLOW_YAML>"]
    }
  }'
```

Evidence checklist:

| Check | Evidence |
|---|---|
| Command accepted | `<HTTP_STATUS_AND_REDACTED_BODY>` |
| Service id returned | `<SERVICE_ID>` |
| Revision id returned | `<REVISION_ID>` |
| No synchronous completion claimed | `<ACK_ONLY_CONFIRMATION>` |

Confirm the scope service catalog readmodel exposes the published service before registering it in NyxID:

```bash
curl -sS "$AEVATAR_BASE_URL/api/scopes/$SCOPE_ID/services?take=20" \
  -H "Authorization: Bearer $NYXID_OR_AEVATAR_BEARER"
```

Evidence checklist:

| Check | Evidence |
|---|---|
| Service appears in catalog | `<SERVICE_ID_AND_STATE_VERSION_OR_UPDATED_AT>` |
| Invoke readiness is visible | `<INVOKE_READINESS_STATUS>` |

## Register / Connect In NyxID

Use NyxID's existing service add/update surface to create or update a downstream service that points at the Aevatar scope service endpoint. The exact CLI/API form depends on the authorized NyxID environment and credential type.

```bash
nyxid service add --custom \
  --label "<DISPLAY_NAME>" \
  --endpoint-url "$AEVATAR_BASE_URL" \
  --auth-method bearer \
  --credential-env AEVATAR_SCOPE_SERVICE_TOKEN
```

Alternative API placeholder:

```bash
curl -sS -X POST "$NYXID_BASE_URL/api/v1/keys" \
  -H "Authorization: Bearer <NYXID_USER_TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{
    "label": "<DISPLAY_NAME>",
    "endpoint_url": "<AEVATAR_BASE_URL>",
    "auth_method": "bearer",
    "credential": "<REDACTED>"
  }'
```

Evidence checklist:

| Check | Evidence |
|---|---|
| NyxID connected service created or updated | `<HTTP_STATUS_OR_CLI_OUTPUT>` |
| Slug captured | `<NYXID_SLUG>` |
| Credential not logged | `<CONFIRMATION>` |

## Discover With `nyxid_proxy`

In chat, call `list_external_workflow_capabilities` and use an exact candidate returned for the current caller context. `nyxid_proxy` is invocation-only and requires the exact `service_id + slug + path` route.

```json
{
  "tool": "nyxid_proxy",
  "arguments": {}
}
```

Evidence checklist:

| Check | Evidence |
|---|---|
| Discovery succeeds | `<REDACTED_DISCOVERY_STATUS>` |
| Aevatar service slug present | `<NYXID_SLUG>` |
| Endpoint hints visible if configured | `<REDACTED_ENDPOINT_HINTS>` |

## Invoke Through NyxID Proxy

Call the discovered slug/path. Use the endpoint path that maps to the Aevatar scope service invoke route.

```json
{
  "tool": "nyxid_proxy",
  "arguments": {
    "slug": "<NYXID_SLUG>",
    "method": "POST",
    "path": "/api/scopes/<SCOPE_ID>/services/<SERVICE_ID>/invoke/<ENDPOINT_ID>",
    "body": {
      "prompt": "Hello from NyxID proxy validation."
    }
  }
}
```

Evidence checklist:

| Check | Evidence |
|---|---|
| Proxy call reaches Aevatar | `<HTTP_STATUS>` |
| Accepted receipt has stable run id | `<RUN_ID>` |
| No fabricated completion state in ACK | `<ACK_FIELDS>` |

## Stream Validation

Use the existing streaming invoke endpoint when the service endpoint supports SSE.

```bash
curl -N -X POST "$NYXID_BASE_URL/api/v1/proxy/s/$NYXID_SLUG/api/scopes/$SCOPE_ID/services/$SERVICE_ID/invoke/$ENDPOINT_ID:stream" \
  -H "Authorization: Bearer <NYXID_AGENT_KEY>" \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -d '{"prompt":"Hello from stream validation."}'
```

Evidence checklist:

| Check | Evidence |
|---|---|
| SSE opens | `<FIRST_EVENT>` |
| Progress events arrive | `<REDACTED_EVENT_SEQUENCE>` |
| Terminal event arrives or expected suspension is observed | `<TERMINAL_OR_SUSPENDED_EVENT>` |

## Run Query Validation

Read the run through Aevatar's read-model-backed run query. Do not use event replay or query-time projection priming.

```bash
curl -sS "$AEVATAR_BASE_URL/api/scopes/$SCOPE_ID/services/$SERVICE_ID/runs/$RUN_ID" \
  -H "Authorization: Bearer $NYXID_OR_AEVATAR_BEARER"
```

Evidence checklist:

| Check | Evidence |
|---|---|
| Run readmodel exists | `<HTTP_STATUS_AND_REDACTED_BODY>` |
| State version or update timestamp exposed | `<VERSION_OR_TIMESTAMP>` |
| Status matches stream/accepted observations | `<STATUS>` |

## Approval Validation

If NyxID approval is enabled for the connected service, the first proxy call can return an approval-required response. Capture the redacted response and approve or deny through NyxID's existing approval surface.

```bash
curl -sS "$NYXID_BASE_URL/api/v1/approvals/requests?status=pending" \
  -H "Authorization: Bearer <NYXID_USER_TOKEN>"
```

```bash
curl -sS -X POST "$NYXID_BASE_URL/api/v1/approvals/requests/<REQUEST_ID>/decide" \
  -H "Authorization: Bearer <NYXID_USER_TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{"decision":"approved"}'
```

Evidence checklist:

| Check | Evidence |
|---|---|
| Approval challenge is returned or not required | `<APPROVAL_STATUS>` |
| Approve/deny decision recorded | `<REQUEST_ID_AND_DECISION>` |
| Retry behavior matches NyxID policy | `<RETRY_RESULT>` |

## Follow-up Gap Templates

Create follow-up issues only for observed future failures. Do not create them from this worker.

| Observed failure | Follow-up template |
|---|---|
| NyxID capability listing omits the Aevatar service after registration | "NyxID discovery gap: registered Aevatar service `<slug>` is not returned by typed external capability listing. Evidence: `<redacted commands/results>`." |
| NyxID proxy cannot invoke the discovered Aevatar path | "NyxID proxy invocation gap: discovered slug `<slug>` cannot reach Aevatar path `<path>`. Evidence: `<redacted status/body>`." |
| Aevatar accepted run is not visible in run query after expected projection lag | "Aevatar run readmodel gap: accepted run `<runId>` for service `<serviceId>` is not query-visible. Evidence: `<accepted receipt>`, `<query response>`, `<timestamps>`." |
| SSE stream misses terminal or suspension observation | "Scope service stream gap: stream for run `<runId>` did not emit expected terminal/suspended event. Evidence: `<event sequence>`." |
| Approval-required response is ambiguous or not actionable | "NyxID approval gap: proxy approval response lacks actionable request id/status. Evidence: `<redacted response>`." |
