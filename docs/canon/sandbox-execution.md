---
title: "Sandbox Execution"
status: active
owner: eanzhao
---

# Sandbox Execution Entry Selection

Aevatar exposes two execution verbs because they represent different user actions. Their
similar output fields do not make them one capability, and callers must select the verb from
the requested work rather than from an implementation detail.

| User intent | Tool and target | Result and approval boundary |
|---|---|---|
| Run an exact source program supplied by the caller | `code_execute` | One-shot remote code runtime; returns stdout, stderr, and exit code. |
| Delegate a natural-language task to an isolated Codex agent | `codex_exec` with `managed_sandbox` | Fixed managed runtime and empty Git workspace; no human approval is required by this target. |
| Delegate a natural-language task to Codex on a real user host | `codex_exec` with `private_ssh` | Uses the selected NyxID-backed host and requires durable human approval. |

## `code_execute`

NyxID-routed capabilities keep three ownership contracts separate:

- `PlatformBuiltIn`: the platform owns the capability and route contract while the caller owns
  invocation authority. `code_execute` is in this class.
- `RolloutRestrictedPlatform`: the platform owns the capability, but a typed rollout boundary and
  eligibility policy limit availability. Managed `codex_exec` remains in this class.
- `CallerConnected`: the caller or an allowed organization owns the connected service. Ordinary
  `nyxid_proxy`, explicit workflow capabilities, and LLM routes remain in this class.

Resolvers and eligibility policies are private to their class. In particular, code-execution
catalog and delegation checks must not filter `CallerConnected` inventory or alter managed Codex
rollout eligibility.

Use `code_execute` when the caller has already supplied the program to run. The input is exact
Python, JavaScript, TypeScript, or Bash source. The output is the program's stdout, stderr, and
exit code.

The read-only classification describes the isolated runtime's durable-effect boundary. It does
not promise that arbitrary caller-provided code is deterministic, pure, successful, or safe to
run outside that runtime. Do not present it as a natural-language agent delegation surface.

Route selection is fail closed, but credential provenance is not a capability lifecycle. During
interactive admission, Aevatar reads the caller-visible typed NyxID UserService inventory and
selects exactly one active, catalog-backed `chrono-sandbox` route whose access and delegation
policy satisfy the platform contract. A personal route is accessible by ownership; an
organization/member route is accessible only when NyxID reports
`credential_source.allowed=true`. An arbitrary custom UserService with the same slug has no
canonical `catalog_service_id` and cannot shadow the platform route. The resolved exact
UserService ID is sent through `_nyxid_via`; there is no slug fallback or first-candidate
selection.

Interactive NyxID ingress may authorize that admission read with either a source-readable user
bearer or the short-lived proxy delegation token injected for Aevatar when it carries NyxID's
`account:read` grant. The delegation token remains the execution credential for the exact proxy
route; it terminates at NyxID and is never forwarded to the sandbox. This code-execution admission
rule is not shared by connected-service discovery, ordinary `nyxid_proxy`, LLM, or managed Codex
paths.

For workflows, `code_execute` is compiled as an external capability with no caller-authored route
selector. Save and bind readiness reads the same typed inventory resolver and commits the exact
UserService ID, slug snapshot, catalog identity, and contract digest into an
`external-capability-admission.v5` call-site proof. Durable bind/invoke additionally requires the
existing actor-owned NyxID authorization catalog to prove that exact service grant. Interactive
runtime re-reads NyxID facts when it has a source-readable caller credential. With delegation-only
interactive ingress, runtime does not read inventory again and may call only the exact UserService
ID sealed in the valid admission proof. Scheduled runtime follows the same exact-proof rule: both
an authority-refreshed short-lived proxy delegation token and a restricted scheduled-invocation
Agent Key are execution credentials, not source-readable inventory credentials, so neither may
auto-resolve a slug.
NyxID then enforces the exact route's slug constraint and credential allowlist on the proxy
request. Without either a source-readable credential or an exact admitted route, execution fails
before network access. Existing v4 plans did not contain code-execution proofs; they remain
supported for source-readable interactive runtime resolution, but delegation-only and scheduled
execution require an exact proof. New live admissions write v5 proofs.

This resolver is private to the platform `code_execute` route. It does not filter connected
services, `nyxid_proxy`, explicit external-capability selections, LLM routes, or managed
`codex_exec`; those paths retain their existing exact UserService and authorization contracts.

Route diagnostics distinguish `code_execution_route_missing`, `code_execution_route_inactive`,
`code_execution_route_policy_mismatch`, `code_execution_route_ambiguous`, and
`code_execution_route_access_denied`. Each runtime rejection carries a local diagnostic ID. Logs
contain only that ID, the reason category, and canonical, accessible, active, and eligible candidate
counts; they do not contain bearer tokens, source code, or the raw inventory.

## `codex_exec`

Use `codex_exec` when the caller wants Codex to interpret and carry out a natural-language task.
The target is part of that request:

- `managed_sandbox` runs under the operator-owned fixed isolation and workspace policy. It does
  not request human approval.
- `private_ssh` operates on a real user host using its local Codex configuration. It requires
  durable human approval for the exact tool call.

The tool's static approval mode is fail-closed metadata. The admitted execution path resolves
the effective approval requirement from the selected target; documentation and prompts must not
describe all `codex_exec` calls as approved or approval-free.

## Selection Rules

- Exact source code to execute: choose `code_execute`.
- Natural-language task for an isolated agent: choose `codex_exec.managed_sandbox`.
- Natural-language task that must act on a real host: choose `codex_exec.private_ssh` and obtain
  approval.
- Connected-service inventory or catalog inspection is not an execution task. Load the current
  discovery skill and use the typed NyxID catalog/service-inspection capability because it
  establishes sender-specific service facts; execution tools do not.

Do not choose between these tools from an approval label, service identity, or similar output
fields. Choose from the user's input and intended execution location. The final tool schemas remain
the authority for availability in the current turn; prompt text and this guide explain selection
but do not grant either capability or alter its runtime policy.
