---
title: "Aevatar API v1 Integration Test Plan"
status: active
owner: eanzhao
---

# Aevatar API v1 Integration Test Plan

This document is the contract-first integration test plan for the public `Aevatar.Mainnet.Host.Api` v1 API surface. It answers the missing scope from the issue titled "Fork of #250: [Test Plan] Aevatar API — v1 API 集成测试覆盖" without expanding into an umbrella coverage project.

The governing practice is contract-driven API integration testing: tests are organized around public HTTP contracts, deterministic fixtures, externally visible assertions, isolation and teardown rules, and CI gates. The tests should exercise the host boundary and typed application facades; they must not assert actor internals, process-local registries, or query-time reconstruction details.

## Source of Truth

The authoritative endpoint contract is the `Aevatar.Mainnet.Host.Api` host surface documented in `src/Aevatar.Mainnet.Host.Api/README.md`.

The current public v1 endpoint matrix is:

| Endpoint | Test responsibility |
| --- | --- |
| `GET /v1/models` | Model list aggregation, bearer propagation to the model aggregator, OpenAI-compatible `list` envelope, and authentication failure. |
| `POST /v1/responses` | OpenAI Responses request normalization, caller-scope resolution, non-stream response rendering, Responses SSE rendering, previous-response continuation, forwarded tool-call reconciliation, and fail-closed error envelopes. |
| `POST /v1/responses/{responseId}/cancel` | Caller-scope resolution, response visibility, session cancellation, forwarded tool-call cancellation, idempotent terminal state handling, and structured 4xx errors for unavailable responses. |
| `POST /v1/messages` | Anthropic Messages request normalization, required `max_tokens`, stateless `LlmSession` registration, Messages SSE frame schedule, unsupported parameter rejection, and shared tool-plan behavior. |
| `POST /v1/chat/completions` | OpenAI Chat Completions request normalization, actor command dispatch, non-stream response rendering, stream chunk rendering, tool-call chunk rendering, usage chunk behavior, and shared caller-scope handling. |

The `scope-first` runtime API surface is part of the same host contract and is covered by `Aevatar.GAgentService.Integration.Tests`:

| Endpoint group | Test responsibility |
| --- | --- |
| `POST /api/scopes/{scopeId}/workflow/draft-run` | Draft workflow run request validation, `workflowYamls` bundle semantics, multipart file ingress, and scope ownership. |
| `PUT /api/scopes/{scopeId}/binding` and `GET /api/scopes/{scopeId}/binding` | Scope binding write/read separation, accepted receipt semantics, and read-model response shape. |
| `GET /api/scopes/{scopeId}/revisions` and `GET /api/scopes/{scopeId}/revisions/{revisionId}` | Revision catalog read model, stable IDs, and catalog version exposure. |
| `POST /api/scopes/{scopeId}/binding/revisions/{revisionId}:activate` and `POST /api/scopes/{scopeId}/binding/revisions/{revisionId}:retire` | Revision lifecycle command mapping and honest accepted/error semantics. |
| `POST /api/scopes/{scopeId}/invoke/chat:stream` | Default service invocation, SSE output, workflow service gating, multipart fail-closed behavior, and stable run ID propagation. |
| `GET /api/scopes/{scopeId}/runs`, `GET /api/scopes/{scopeId}/runs/{runId}`, and `GET /api/scopes/{scopeId}/runs/{runId}/audit` | Formal run list/detail/audit read models, version exposure, and not-found behavior. |
| `POST /api/scopes/{scopeId}/runs/{runId}:resume`, `POST /api/scopes/{scopeId}/runs/{runId}:signal`, and `POST /api/scopes/{scopeId}/runs/{runId}:stop` | Continuation command mapping, path-owned scope identity, and fail-closed invalid run handling. |

## Fixtures And Isolation

Integration tests must use `Microsoft.AspNetCore.TestHost` or the existing package-specific endpoint test kit. Each test owns its host, service overrides, in-memory document and graph projection providers, fake LLM provider, fake caller-scope resolver, and fake model aggregator.

The default test IDs must keep business identities separate:

| Identity | Example |
| --- | --- |
| Caller scope | `scope-alpha` |
| Response ID | `resp-alpha` |
| Run ID | `run-alpha` |
| Service ID | `svc-alpha` |
| Workflow draft ID | `wf-alpha` |
| Member ID | `m-alpha` |

Tests must not reuse one string for `memberId`, `workflowId`, `publishedServiceId`, `responseId`, or `runId`. A test that needs a candidate value before resolving identity should name it `routeIdentityCandidate` or `bindingIdentityCandidate` until the source contract establishes the concrete identity.

## Assertion Rules

Every v1 API integration test must assert only externally observable contract behavior:

- HTTP method, route, status code, and content type.
- Response envelope shape and stable field names.
- SSE event names and required ordering.
- Authentication and caller-scope error envelopes.
- Absence of raw bearer tokens in response bodies, SSE frames, logs captured by the test, and metadata bags.
- Typed route preference and caller credential propagation through application facades.
- Accepted receipt honesty: `accepted` means accepted for dispatch, not committed or read-model observed.
- Read-model version or freshness fields when the endpoint returns query data.

Tests must not assert private actor state, local runtime object type, event-store replay output, process-local dictionaries, or query-time priming behavior.

## Existing Executable Owners

The current executable coverage owners are:

| Test file | Coverage owner |
| --- | --- |
| `test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs` | Host composition and public v1 route registration. |
| `test/Aevatar.Capabilities.Tests/MainnetResponsesEndpointsTests.cs` | `GET /v1/models`, `POST /v1/responses`, and `POST /v1/responses/{responseId}/cancel`. |
| `test/Aevatar.Capabilities.Tests/MainnetMessagesEndpointsTests.cs` | `POST /v1/messages`. |
| `test/Aevatar.Capabilities.Tests/MainnetChatCompletionsEndpointsTests.cs` | `POST /v1/chat/completions`. |
| `test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpoints/*.cs` | Scope-first runtime endpoint contracts. |
| `test/Aevatar.GAgentService.Integration.Tests/GAgentServiceHostingServiceCollectionExtensionsTests.cs` | Scope-first endpoint registration. |

New v1 API integration coverage should extend these owners unless a new public endpoint family is added. If a new test project or harness is proposed, the proposal must explain why the existing owner cannot express the contract.

## CI Gates

Local iteration must run the repository-scoped affected test command:

```bash
scripts/run.sh test-affected
```

Comprehensive CI remains:

```bash
scripts/run.sh test
```

Changes that add or modify tests must also satisfy the repository stability guard:

```bash
bash tools/ci/test_stability_guards.sh
```

Changes that touch query/read paths, projection priming, projection lifecycle, current-state read models, state versions, or state mirror projectors must run the specific architecture guards named in `AGENTS.md`.

No v1 API test should introduce arbitrary `Task.Delay(...)` polling. If an eventually consistent cross-process probe cannot be made deterministic, the test file must be listed in `tools/ci/test_polling_allowlist.txt` with the reason for the exception.
