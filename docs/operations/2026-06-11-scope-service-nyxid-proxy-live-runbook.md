# Scope Service -> NyxID Proxy Live Runbook

Date: 2026-06-11
Scope: GitHub issue #1981.

This runbook validates the existing Aevatar scope-service invocation chain and
the NyxID proxy path without adding a second workflow tool, embedding workflow
discovery inside `nyxid_proxy`, auto-registering downstream services, or asking
NyxID / chrono-* repositories for new endpoints or schema.

## Goal

Prove the live path that already exists:

```text
Aevatar scope binding
  -> scope service invoke / invoke stream / run read-model endpoints
  -> NyxID custom downstream and proxy service records
  -> chat route using the existing nyxid_proxy tool surface
```

The smoke result is evidence only. Non-streaming response shape, `:stream`
behavior, approval handling, run listing, and run detail availability must be
recorded from live responses. Missing capability is a follow-up issue, not a
fallback path in this runbook.

## Preconditions

- The operator is logged in through the local NyxID-backed CLI state.
- The operator can see at least one Aevatar scope with an active binding.
- The bound service has an invokable endpoint and, for stream validation, a
  stream-capable endpoint.
- NyxID has the target downstream services active for the same identity.
- The operator can redact response bodies before sharing evidence.

Useful local checks:

```bash
aevatar-cli --env mainnet whoami
aevatar-cli --env mainnet scopes list --json
nyxid whoami
nyxid service list --output json
```

If `scopes list` returns `[]`, stop after endpoint discovery. Do not fabricate a
scope id and do not use another user's scope.

## Discovery

Confirm the live Aevatar surface before invoking anything:

```bash
aevatar-cli --env mainnet api GET /api/health
aevatar-cli --env mainnet endpoints --grep scopes --json
aevatar-cli --env mainnet endpoints --grep invoke --json
aevatar-cli --env mainnet endpoints --grep nyxid-chat --json
aexon aevatar endpoints invoke
```

Expected route families:

- `GET /api/scopes/{scopeId}/binding`
- `POST /api/scopes/{scopeId}/invoke/{endpointId}`
- `POST /api/scopes/{scopeId}/invoke/chat:stream`
- `POST /api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}`
- `POST /api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}:stream`
- `GET /api/scopes/{scopeId}/runs`
- `GET /api/scopes/{scopeId}/runs/{runId}`
- `POST /api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:approve`
- `POST /api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:stream`

## Scope Binding Check

Use only a scope returned by `aevatar-cli --env mainnet scopes list --json`.

```bash
aevatar-cli --env mainnet scopes use <scope-id>
aevatar-cli --env mainnet scopes binding
aevatar-cli --env mainnet scopes services
aevatar-cli --env mainnet scopes endpoints --json
```

Record:

| Evidence | Value |
|---|---|
| scope id | `<redacted or stable non-secret id>` |
| active service id | `<service-id>` |
| endpoint id | `<endpoint-id>` |
| stream endpoint id | `<endpoint-id or not available>` |
| binding status | `<active / missing / denied / error>` |

If the binding read returns `SCOPE_ACCESS_DENIED`, stop. That means the
authenticated subject does not own the requested scope.

## Non-Streaming Invoke

Run the non-stream endpoint with the contract supplied by Aevatar:

```bash
aevatar-cli --env mainnet invoke contract <endpoint-id> \
  --service <service-id> \
  --json

aevatar-cli --env mainnet invoke <endpoint-id> \
  --service <service-id> \
  --payload-json @request.json \
  --no-stream
```

Record the HTTP status and the redacted JSON shape. Do not rewrite the result
into a preferred schema; keep the observed top-level fields.

## Streaming Invoke

Use the existing `:stream` endpoint. The stream is not a request/reply fallback;
it is the live observation surface for stream-capable invocation.

```bash
aevatar-cli --env mainnet invoke <endpoint-id> \
  --service <service-id> \
  --payload-json @request.json \
  --stream
```

or, for a direct API probe:

```bash
cat request.json | aexon aevatar api post \
  /api/scopes/<scope-id>/services/<service-id>/invoke/<endpoint-id>:stream \
  --stdin --sse
```

Record the first event type, terminal event type, and whether the stream closes
without client timeout.

## Runs Read Model

After an accepted invocation, verify only through read-model endpoints:

```bash
aexon aevatar api /api/scopes/<scope-id>/services/<service-id>/runs
aexon aevatar api /api/scopes/<scope-id>/services/<service-id>/runs/<run-id>
aexon aevatar api /api/scopes/<scope-id>/runs
aexon aevatar api /api/scopes/<scope-id>/runs/<run-id>
```

Record whether list/get are available and whether the observed `run_id`,
`command_id`, `status`, and version/freshness fields line up with the invoke
receipt. If the read model is eventually consistent or missing, record that
honestly; do not query actor state or event store as a substitute.

## NyxID Proxy And Downstream Check

Confirm that the relevant downstream services are active in NyxID:

```bash
nyxid service list --output json \
  | jq '.keys[] | {slug, status, credential_type, service_type, endpoint_url}'
```

For the Aevatar proxy itself, the expected service record has `slug` equal to
`aevatar`, `status` equal to `active`, and `forward_access_token` enabled. For
LLM calls, at least one downstream LLM service such as `chrono-llm`,
`llm-openai`, `llm-anthropic`, or `llm-deepseek` must also be active.

Do not auto-register missing services as part of this smoke. Ask the operator to
complete the normal NyxID service setup, then rerun the smoke.

## Chat nyxid_proxy Check

Use an existing NyxID chat conversation in the authenticated scope. This is a
chat smoke for the existing `nyxid_proxy` surface, not workflow discovery.

```bash
aexon aevatar api /api/scopes/<scope-id>/nyxid-chat/conversations

cat chat-request.json | aexon aevatar api post \
  /api/scopes/<scope-id>/nyxid-chat/conversations/<actor-id>:stream \
  --stdin --sse
```

The prompt should ask for a low-risk read-only call through an already active
NyxID downstream service, for example a GitHub `rate_limit` probe when
`api-github` is active. Record whether the stream emits a tool call/tool result
sequence and whether final text reflects the tool result.

Approval validation, if the downstream service requires approval, must use the
existing approval endpoint:

```bash
aexon aevatar api post \
  /api/scopes/<scope-id>/nyxid-chat/conversations/<actor-id>:approve \
  '{"approval_id":"<approval-id>","decision":"approve"}'
```

Record the observed approval response. Do not add an availability fallback.

## Evidence Table

Fill this table from a real run:

| Probe | Status | Evidence |
|---|---|---|
| Aevatar health | Observed 2026-06-11: `503 not-ready` on mainnet | `GET /api/health` returned `status=not-ready`; `gagent-service` detail was healthy in the redacted response |
| Endpoint discovery | Observed 2026-06-11: available | `aexon aevatar endpoints invoke` listed non-stream and `:stream` invoke endpoints |
| Current user scopes | Observed 2026-06-11: none visible | `aevatar-cli --env mainnet scopes list --json` returned `[]` |
| Scope binding | Blocked by missing visible scope | Rerun with an operator identity that can see the target scope |
| Non-stream invoke | Blocked by missing visible scope | Record live response shape when scope is available |
| `:stream` invoke | Blocked by missing visible scope | Record SSE event sequence when scope is available |
| Runs list/get | Blocked by missing visible scope | Record read-model availability after invocation |
| NyxID Aevatar service | Observed 2026-06-11: active | `nyxid service list --output json` showed `slug=aevatar`, `status=active`, `forward_access_token=true` |
| NyxID downstream services | Observed 2026-06-11: active examples | `chrono-llm`, `llm-openai`, `llm-anthropic`, `llm-deepseek`, `api-github`, and `api-lark-bot` were active in the redacted list |
| Chat `nyxid_proxy` | Blocked by missing visible scope | Rerun with a scope and conversation actor owned by the operator |
| Approval endpoint | Route observed, live behavior not exercised | `/api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:approve` is present in endpoint discovery |

## Follow-Up Candidates

Only file follow-up issues for observed gaps:

- Non-stream invoke response shape is missing required caller-facing fields.
- `:stream` endpoint times out, omits terminal events, or emits malformed SSE.
- Approval response cannot be correlated with the pending tool call.
- Runs list/get read models are unavailable after accepted invocation.
- Endpoint discovery times out for a specific CLI surface while another live
  surface can list the same endpoints.

Do not file follow-ups that ask NyxID or chrono-* repositories to add Aevatar
specific endpoints, schemas, or fallback behavior.
