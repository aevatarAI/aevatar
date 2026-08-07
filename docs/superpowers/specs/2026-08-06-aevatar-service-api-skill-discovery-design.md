# Aevatar Service API Skill Discovery Design

## Governing Practice

The governing practice for this change is capability-based least privilege, zero-trust structured-output handling, schema-first internal contracts, and ports-and-adapters layering.

The model-facing authoring surface may ask for help, but it is never the authority for an executable workflow capability. Authority belongs to typed descriptors, exact versioned source artifacts, and Aevatar admission contracts. Untrusted model output is decoded at the external boundary, correlated with Application-owned input, exact-verified, and then admitted through the existing request-shape path.

## Scope

This design covers the `/api/chat` workflow-authoring path that resolves a caller request for an external service API operation into one of:

- `capability.nyxid_operation` from a matching exact NyxID descriptor.
- `capability.nyxid_request` from an exact Ornn API skill candidate after verification and admission.
- `capability.nyxid_request` from official Web contract fallback after admission.
- `ServiceApiFallbackExhausted` after required discovery and fallback work has actually run.

The asynchronous `nyxid-service-skill-authoring` job is out of scope. Publishing, updating, deleting, or binding Ornn skills from this path is out of scope. Creating an Ornn MCP server is out of scope.

## Decision Chain

The resolution chain is a single authoritative path:

1. Call `list_external_workflow_capabilities`.
2. If a matching exact descriptor exists, copy its exact authoring `selector` and produce `capability.nyxid_operation`.
3. If no matching exact descriptor exists, call `discover_service_api_workflow_capability`.
4. If that typed Application port returns a reliable admitted Ornn result, author only `capability.nyxid_request`.
5. If that typed Application port returns a valid `NoReliableServiceApiSkill`, enter the existing `web_search` then `web_fetch` fallback.
6. If official Web research establishes an HTTP contract, feed the same request-shape admission path and author `capability.nyxid_request`.
7. If Web fallback cannot establish an admitted request shape, return `ServiceApiFallbackExhausted`.

Invalid managed output, infrastructure failure, correlation mismatch, or exact verification failure is not `NoReliableServiceApiSkill`. It must stop the managed branch and must not trigger Web fallback.

## Managed Codex Authority

Managed Codex runs behind a managed credential Actor/read-model/vault boundary. The exact allowed NyxID UserService set is:

- `chrono-sandbox`
- `chrono-llm-public`
- `ornn-api`

The managed credential descriptor stores exact UserService IDs for all three services and keeps:

- `allow_all_services = false`
- `allow_all_nodes = false`
- empty node allowlist

Existing active keys with only the previous two-service set are not ready. Credential readiness, lifecycle comparison, remote-key adoption, reconciliation, rotation, projection, endpoint DTOs, and tests must all preserve the exact third `ornn-api` identity without exposing bearer/API key material.

No Aevatar pseudo-RBAC string such as `ornn:skill:read` is introduced. The NyxID service allowlist is the managed-key authority.

## Typed Contracts

Stable semantics are Protobuf messages in `workflow_capability_admission.proto`.

`ManagedCodexServiceApiSkillDiscoveryResult` is the decoded managed stdout result:

- `ReliableServiceApiSkillCandidate`
- `NoReliableServiceApiSkill`

`ServiceApiCapabilityResolution` is the complete resolution result:

- `ResolvedNyxIdOperation`
- `ResolvedNyxIdRequest`
- `ServiceApiFallbackExhausted`

`ServiceApiWorkflowCapabilityDiscoveryResult` is the model-facing Application-port wrapper for the descriptor-miss managed branch:

- `resolution`, which contains a `ServiceApiCapabilityResolution`
- `no_reliable_api_skill`, which contains `NoReliableServiceApiSkill`

This wrapper keeps valid no-match separate from complete fallback exhaustion. It also prevents the tool from pretending that a managed no-match is a final workflow capability result.

`ResolvedNyxIdRequest` uses typed provenance:

- `ExactOrnnApiSkillProvenance`
- `OfficialWebContractProvenance`

The provenance branches are mutually exclusive. Ornn success sets only `ornn_skill`; Web success sets only `official_web`.

## Managed Codex Input

The narrow managed-discovery input is `ServiceApiSkillDiscoveryInput`. It contains typed fields for:

- caller authority
- scope id
- caller id
- exact `target_user_service_id`
- service slug snapshot
- service label snapshot
- normalized requested capability
- descriptor inventory
- managed discovery policy version
- request-shape admission policy version
- `capability_fingerprint`
- excluded reliable candidates

`capability_fingerprint` is lowercase SHA-256 over deterministic Protobuf bytes of the normalized requested capability. Managed Codex receives it only as a correlation echo. Application continues using its own authoritative `target_user_service_id` and fingerprint after decoding.

## Managed Codex Output

The external managed stdout boundary is JSON, but only at that boundary. It must be exactly one UTF-8 JSON object using schema version `service_api_skill_discovery.v1`.

The decoder rejects:

- Markdown fences, prefixes, suffixes, logs, natural language, or second JSON values.
- Unknown fields, wrong types, missing required branch fields, or unsupported schema versions.
- `target_user_service_id` mismatch.
- `capability_fingerprint` mismatch.
- Outcome/branch mismatch.
- Non-literal skill versions such as `latest`.
- Non-lowercase SHA-256 skill hashes.
- Unsupported method/body/response/risk combinations.
- Unsafe paths, duplicate parameters, reserved credential-bearing parameters, or host overrides.

The decoded result is immediately mapped to Protobuf. Raw stdout, Codex transcripts, raw Ornn responses, exact skill packages, credentials, bearer tokens, and API keys are not persisted or returned to workflow YAML.

## Exact Ornn Verification

A reliable managed candidate is only a proposal. Aevatar independently exact-pulls the candidate by GUID and literal version through the existing Ornn client/exact-fetch infrastructure. It must never read `latest`.

Verification checks:

- canonical GUID
- literal `<major>.<minor>` version
- exact detail GUID
- exact skill package version
- canonical name
- publisher identity
- full-package SHA-256
- evidence file, section, and operation locator
- candidate request shape support in that exact package

Only after this verification can the request shape proceed to admission.

## Request-Shape Admission

The admitted API skill path uses the existing `NyxIdRequestSelector` contract. It does not add a parallel HTTP request model.

Admission continues to enforce:

- supported method
- safe relative path
- unique path placeholders
- no host/service override
- no traversal, query string, fragment, encoded routing syntax, or whitespace
- unique static query/header names
- no credential/reserved NyxID query names
- safe header allowlist
- GET/HEAD/OPTIONS with no body
- JSON body only when body is required
- file artifact response only for GET with no body
- method/risk floor and workflow execution policy
- stable mapping to `capability.nyxid_request`

An Ornn API skill can never produce `capability.nyxid_operation`. Only exact NyxID operation descriptors can do that.

## Authoring Tool Boundary

`WorkflowDefinitionCatalog` keeps `codex_exec` out of the Studio workflow authoring allowlist. It also removes direct `ornn_search_skills` and `use_skill` from the Studio workflow authoring allowlist.

The only model-facing managed-discovery tool is `discover_service_api_workflow_capability`. It is read-only, takes typed fields, and delegates to `IServiceApiWorkflowCapabilityDiscoveryPort`. It does not accept an arbitrary prompt and does not return credentials.

The tool returns:

- a typed resolution and authoring selector when the Application port resolves a request shape
- a typed `NoReliableServiceApiSkill` branch without an authoring selector when managed discovery has a valid no-match
- a safe error when input parsing, correlation, infrastructure, or verification fails

Only the valid no-reliable branch may enter Web fallback.

## Layer Ownership

Application owns:

- descriptor priority
- managed discovery orchestration contract
- candidate iteration policy
- exact verification contract
- request-shape admission
- Web fallback routing
- terminal result mapping

Infrastructure owns:

- managed Codex execution
- strict stdout decoding
- Ornn exact-read adapters
- Web tool adapters

Host owns only:

- authentication
- DTO mapping
- DI composition
- invoking the Application contract

Model-facing tools are adapters over Application contracts; they are not a second workflow authoring system.

## Verification Strategy

Tests cover:

- three-service managed Codex credential readiness and reconciliation
- exact allowed-service policy with no broad service/node authority
- endpoint/projection/state preservation of `ornn-api` identity without secrets
- strict `service_api_skill_discovery.v1` stdout decoding
- typed no-reliable reasons
- narrow read-only `discover_service_api_workflow_capability` tool registration
- no direct `ornn_search_skills` or `use_skill` in Studio workflow authoring
- descriptor-first prompt order
- web fallback only after valid `NoReliableServiceApiSkill`

Repository verification uses:

- focused project tests while implementing
- `bash tools/ci/test_stability_guards.sh` for test changes
- `scripts/run.sh test-affected` as the required local delivery gate
