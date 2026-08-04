# Finance Workflow Acceptance Design

## Goal

Make the six finance workflow artifacts under `~/workflows/2026-07-30-for-aevatar-team` authorable, bindable, and executable through the canonical Aevatar workflow path without weakening actor-owned capability admission. Fix #3052, #3061, and #3062, and add a typed explicit NyxID request contract for APIs the author already knows even when no OpenAPI/MCP endpoint contract exists.

## Semantic decision

`nyxid_proxy` is the runtime adapter, not the authoring authority. A managed workflow may reach it through exactly one of two mutually exclusive typed selectors:

1. `capability.nyxid_operation` is `PublishedEndpoint(endpoint_id)`: it selects `user_service_id + endpoint_id`. NyxID MCP catalog owns the published endpoint contract and Aevatar derives the call-site proof from it.
2. `capability.nyxid_request` is `AuthoredRequest(request_contract_digest)`: it selects an exact `user_service_id`, static HTTP method, static relative path template, declared query/header names, request-body mode, and response mode. It is only a contract proposal until an authenticated binder explicitly confirms its current digest and risk; the definition actor then persists a typed `NyxIdExplicitRequestGrant`. OpenAPI is not consulted.

Both selectors produce an actor-owned call-site admission. Apply/save preserves the request selector but cannot create a grant. Neither selector permits runtime `slug`, `service_id`, `user_service_id`, `method`, `path`, endpoint identity, policy, response-mode override, or digest fields in `parameters.arguments`. Legacy route bags remain rejected.

## Explicit request contract

Canonical YAML:

```yaml
steps:
  - id: read_records
    type: tool_call
    capability:
      nyxid_request:
        user_service_id: usvc-alpha
        method: GET
        path_template: /v1/resources/{resource_id}
        query_parameters: [page_size, filter]
        body_mode: none
        response_mode: text
    parameters:
      tool: nyxid_proxy
      arguments: '{"path_params":{"resource_id":"${input.resource_id}"},"query":{"page_size":"20"}}'
```

Typed fields and constraints:

- `user_service_id` is required, exact, static, and opaque.
- `method` is one of `GET`, `HEAD`, `OPTIONS`, `POST`, `PUT`, `PATCH`, or `DELETE`.
- `path_template` is a normalized relative proxy path beginning with `/`; placeholders are single path segments and must have unique names. Scheme/authority, query, fragment, traversal, encoded traversal, and workflow templates are rejected.
- `query_parameters` and `header_parameters` are normalized, unique allowlists. Credential-bearing and NyxID-reserved headers/query names are rejected.
- `body_mode` is `none` or `json`; `none` rejects a body, while `json` accepts one JSON value.
- `response_mode` is `text` or `file_artifact`. `file_artifact` is allowed only for `GET`, forbids a body, keeps the existing maximum-byte limit, and stores bytes only through the managed workflow file-artifact ingress.

At bind time, the admission source reads NyxID's exact UserService inventory, not MCP/OpenAPI. It verifies that exactly one active, credential-allowed service matches `user_service_id`, derives the server-side slug constraint, and emits a `NYX_ID_USER_SERVICES` source stamp. The request-contract digest covers exact `user_service_id`, normalized request shape, placeholders, declarations, body mode/requiredness, and response mode; the proof additionally covers the server-derived route constraint. Runtime reads neither MCP/OpenAPI nor inventory: it validates the committed proof and matching typed grant, then sends exactly one exact proxy route using the exact `user_service_id` and server-derived slug constraint. There is no slug-only fallback.

## Execution policy

The proof carries a typed authorization basis so runtime does not infer semantics from IDs or paths:

- published operation: existing MCP-derived risk/approval policy remains unchanged;
- explicit request `GET/HEAD/OPTIONS` with trusted `READ_ONLY` binder attestation: no per-run approval, interactive and durable only with the existing exact-service durable authorization;
- `GET/HEAD/OPTIONS` without that trusted attestation: conservatively write, Aevatar approval required, interactive only;
- explicit request `POST/PUT/PATCH`: write, Aevatar approval required, interactive only;
- explicit request `DELETE`: destructive, Aevatar approval required, interactive only.

This is narrower than raw proxy access: authorization is limited to one actor-owned exact service/method/path-template contract and the workflow definition digest covers all authored runtime argument names and templates.

## Existing issue fixes

### #3062 Studio authoring

Studio models `capability` as a typed step field and preserves both selectors through YAML parse, document JSON, normalize, and YAML serialize. Unknown capability variants fail closed. The canonical runtime parser remains the source of executable validation.

### #3052 caller credential

Admission HTTP boundaries share one header selector: one valid `Authorization: Bearer` wins; when Authorization is absent, one valid `X-NyxID-Delegation-Token` is used; an explicit malformed/unsupported Authorization fails closed and never falls back. Tokens remain transient and redacted.

### #3061 Orleans scheduler

The channel background-delivery reservation/registration chain preserves the Orleans activation scheduler across actor runtime and dispatch awaits. Non-activation work may continue to use context-free awaits; activation-sensitive awaits may not. A real Orleans integration regression must force an asynchronous continuation before actor runtime access.

## Finance acceptance order

1. Parse/serialize typed selectors without loss.
2. Bind and run the P2 no-send workflow; require a non-empty first Bitable result and terminal `completed/success=true`.
3. Run the attachment probe through Lark bot -> `aevatar_invoke_team`; image and PDF must create real runs and non-empty extraction output.
4. Run P1 file-input and preview paths.
5. Run P1 v6 binary drain through explicit `file_artifact`.
6. Only after finance confirms the numbers, run the send/approval side-effect paths.

Repository tests use sanitized, distinct identities (`memberId=m-alpha`, `workflowId=wf-alpha`, `publishedServiceId=svc-alpha`, `user_service_id=usvc-alpha`) and contain no finance tenant IDs, amounts, people, Lark resource IDs, or credentials.

## Verification

- focused Studio, Workflow Core/Application, AI/NyxID, channel/Orleans tests;
- `bash tools/ci/test_stability_guards.sh`;
- `bash tools/ci/workflow_binding_boundary_guard.sh`;
- `bash tools/ci/architecture_guards.sh`;
- `bash tools/docs/lint.sh`;
- `dotnet build aevatar.slnx --nologo`;
- `dotnet test aevatar.slnx --nologo`;
- production calls only through `nyxid proxy request aevatar ...`; no send/approval mutation before finance confirmation.

## Non-goals

- no generic raw-HTTP workflow primitive;
- no hidden compatibility path for legacy `{slug, service_id, method, path}` arguments;
- no second workflow runtime, proxy client, or projection pipeline;
- `AuthoredRequest` runtime performs no MCP/OpenAPI or inventory read; `PublishedEndpoint` retains runtime MCP endpoint-digest revalidation;
- no credentials or tenant-specific workflow data in GitHub issues or repository fixtures.
