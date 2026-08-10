# Service API Capability Resolution Design

## Governing Practice

This change follows capability-based least privilege, zero-trust point-of-use admission,
schema-first contracts, and ports-and-adapters layering.

Capability resolution and executable readiness are separate decisions:

- Resolution selects an authoritative operation or admitted request contract.
- Readiness inspects the selected contract at use time under the authenticated caller authority.
- A resolved selector is not evidence that credentials, access, or runtime dependencies are ready.
- Model output is never authority for identity, correlation, readiness, or remediation completion.

## Authoritative Path

`ServiceApiWorkflowCapabilityResolutionService` owns one deterministic path:

1. Canonicalize the producer capability key and compute its fingerprint.
2. Read the current typed external capability descriptor inventory.
3. Select the single exact NyxID operation descriptor for the target UserService when one exists.
4. Otherwise invoke managed Service API skill discovery and exact Ornn verification.
5. Map a verified candidate only to `ResolvedNyxIdRequest` with `ornn_skill` provenance.
6. Forward a valid `NoReliableServiceApiSkill` to `IServiceApiCapabilityFallbackPort`.
7. Preserve managed transport, decoding, correlation, and contract failures as failures.

The temporary `UnavailableServiceApiCapabilityFallbackPort` returns a typed
`ServiceApiFallbackExhausted` result. Workstream D can replace this adapter without changing the
Application orchestration or model-facing contract.

## Terminal Contract

`ServiceApiCapabilityResolution` has exactly three terminal branches:

- `ResolvedNyxIdOperation`
- `ResolvedNyxIdRequest`
- `ServiceApiFallbackExhausted`

`NoReliableServiceApiSkill` remains internal to managed discovery and fallback routing. It is not a
model-facing terminal result.

## Readiness Handoff

`ServiceApiWorkflowCapabilityDiscoveryResult` returns either a terminal resolution or
`ServiceApiCapabilityReadinessHandoff`.

The handoff contains:

- The typed `ExternalCapabilityReadiness`, exact selected selector, blockers, and remediation actions.
- Any trusted remediation locator produced by the authoritative readiness source.
- A typed retry contract containing the original normalized capability key, fingerprint, descriptor
  inventory, policy versions, caller authority, target UserService, and distinct `workflow_id`,
  `member_id`, and `published_service_id` values.

Credential mutation remains outside chat and request execution. After an authorized lifecycle action
completes, `RetryAfterRemediationAsync` accepts only the original typed retry contract and verifies the
authenticated scope and caller before rerunning resolution.

## Chat Boundary

`discover_service_api_workflow_capability` is a read-only adapter over the Application port. It derives
scope, caller identity, and credentials from typed request context, accepts no arbitrary prompt or
credential input, and returns workflow-safe selector field names.

The Studio workflow prompt delegates Service API descriptor priority, managed discovery, fallback,
readiness, and correlation to this tool. Direct descriptor, readiness, Ornn-search, and skill-loading
tools are not in the Studio allowlist, preventing a parallel model-owned resolution state machine.
