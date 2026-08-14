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
selects exactly one active, catalog-backed `chrono-sandbox` route that delivers an execution
credential the code runtime accepts. A personal route is accessible by ownership; an
organization/member route is accessible only when NyxID reports
`credential_source.allowed=true`. An arbitrary custom UserService with the same slug has no
canonical `catalog_service_id` and cannot shadow the platform route. The resolved exact
UserService ID is sent through `_nyxid_via`; there is no slug fallback or first-candidate
selection.

### Accepted execution credential

The code runtime authenticates the request itself; the route only decides which credential
reaches it. NyxID delivers the two credentials through different headers and keeps their settings
independent, so the accepted route policy is a disjunction, not a matched pair:

- `forward_access_token=true` re-sends the caller's own bearer as `Authorization`. The runtime
  introspects it and requires the caller to hold the `sandbox:execute` permission. This is the
  authoritative credential whenever it is present.
- Otherwise `inject_delegation_token=true` with `sandbox:execute` among the whitespace-separated
  `delegation_token_scope` values. NyxID mints a short-lived delegated token in
  `X-NyxID-Delegation-Token`, which the runtime accepts only as the fallback when no bearer was
  forwarded.

A route satisfying neither is rejected as `CODE_EXECUTION_ROUTE_POLICY_MISMATCH`, and the blocker
names the unsatisfied setting so the owner can repair the service without reading logs. Admission
reads scope membership, never an exact scope set: unrelated delegated scopes on the same route do
not affect code execution and must not block it. `delegation_token_scope` is a NyxID delegation
scope and is a different namespace from the runtime's `sandbox:execute` RBAC permission, which
Aevatar cannot observe — a route can therefore pass admission and still be refused by the runtime
for a caller who lacks that permission.

Fresh workflow command admission may converge this mismatch without a platform administrator.
`code_execute` owns the typed route contract above; it does not own a second route-management
lifecycle. When authenticated ingress supplies a verified direct human NyxID access token and
there is exactly one active, catalog-backed candidate that token may write, the shared route
convergence boundary updates only that exact UserService to `forward_access_token=false`,
`inject_delegation_token=true`, preserves the observed unique delegation scopes, and appends
`sandbox:execute`. Write authority means either a caller-owned personal route or an organization
route for which the caller is an organization admin. It then rereads NyxID and admits only when
the exact route identity, catalog identity, credential policy, and preserved scopes verify. An
already accepted forwarding or delegated route remains unchanged.

An allowed organization member/viewer may use an already compatible route but may not change it;
a mismatch remains a typed write-authority blocker. Readiness queries and runtime never write.
Neither proxy delegation, a broker-issued read token, API key, service account, nor relay
credential authorizes this convergence. The write-authority marker is transient ingress evidence:
it is not serialized into a workflow command envelope, admission plan, actor state, or read model.
NyxID UserService updates currently provide no compare-and-swap revision, so this is bounded
best-effort convergence: Aevatar sends the minimal field patch, performs fresh readback, fails
closed on final drift, and does not claim that a concurrent scope edit cannot be overwritten.

Managed `codex_exec` selects the same exact `chrono-sandbox` UserService ID but calls
`/codex/execute` with its own request credential and eligibility contract. The UserService route
configuration is shared: managed execution requires delegation containing `proxy:*`, while
`code_execute` convergence preserves existing scopes and appends `sandbox:execute`. Neither
capability may replace the other's contract or fall back to the other's runtime path.

Interactive NyxID ingress may authorize that admission read with either a source-readable user
bearer or the short-lived proxy delegation token injected for Aevatar when it carries NyxID's
`account:read` grant. The delegation token remains the execution credential for the exact proxy
route. It is presented to NyxID as the request bearer, so on a route that forwards access tokens
NyxID re-sends it to the runtime, which introspects it as the caller. Reason about its blast radius
as a short-lived caller credential the runtime sees, not as one that stops at NyxID. This
code-execution admission rule is not shared by connected-service discovery, ordinary `nyxid_proxy`,
LLM, or managed Codex paths.

For workflows, `code_execute` is compiled as an external capability with no caller-authored route
selector. Fresh draft-run, save, and bind command admission performs the bounded exact-route
convergence before live readiness when the caller has write authority; standalone readiness
remains read-only. Admission commits
the exact UserService ID, slug snapshot, catalog identity, and contract digest into an
`external-capability-admission.v5` call-site proof. Durable bind/invoke additionally requires the
existing actor-owned NyxID authorization catalog to prove that exact service grant. Interactive
runtime re-reads NyxID facts when it has a source-readable caller credential. If that source read
returns `401 Unauthorized` after admission because the short-lived credential expired during a long
run, runtime may continue only when the valid admission proof seals an exact UserService ID; NyxID
then performs the final live credential and allowlist check on that exact proxy request. A `403`
response or an inventory read that succeeds and explicitly denies the route still fails closed. With
delegation-only interactive ingress, runtime does not read inventory again and may call only the
exact UserService ID sealed in the valid admission proof. Scheduled runtime follows the same
exact-proof rule:
authority-refreshed tokens, short-lived tokens projected through a scheduled durable handle, and
restricted scheduled-invocation Agent Keys all retain the `ProxyDelegation` kind. They are execution
credentials, not source-readable inventory credentials, so none may auto-resolve a slug.
NyxID then enforces the exact route's slug constraint and credential allowlist on the proxy
request. Without either a source-readable credential or an exact admitted route, execution fails
before network access. Existing v4 plans did not contain code-execution proofs; they remain
supported for source-readable interactive runtime resolution, but delegation-only and scheduled
execution require an exact proof. New live admissions write v5 proofs.

The selector and accepted credential contract are private to the platform `code_execute` route.
They do not filter connected services, `nyxid_proxy`, explicit external-capability selections, LLM
routes, or managed `codex_exec`. Other capabilities may use the same generic convergence mechanism
only by declaring their own typed route contract; they cannot inherit `sandbox:execute` or this
route's preferred credential delivery settings.

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
