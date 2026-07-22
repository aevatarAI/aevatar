---
title: "Managed Codex gVisor Direct-Token Isolation Model"
status: accepted
owner: eanzhao
---

# ADR-0044: Managed codex_exec runs on gVisor with a directly injected short-lived token

## Context

The managed `codex_exec` protection stack was designed to guard a high-value, long-lived
credential: a fail-closed legacy Landlock preflight inside the runner, a `dns+nft` egress
sidecar enforcing an FQDN allow-list, and a planned OpenSandbox Credential Vault placeholder
substitution (#2899). That stack is only satisfiable on a runc + Ubuntu tenant. gVisor's
Sentry does not implement Landlock (`landlock_create_ruleset` returns `ENOSYS`) and its
netstack has no iptables NAT for the sidecar; runc + COS nodes do not enable the LSM. The
credential-protection stack therefore forced the runtime with the weaker escape isolation.

Since #2908, the only credential that reaches the sandbox is a five-minute, single-user,
gateway-scoped `llm:proxy` delegation token injected by NyxID. The persistent per-user agent
key never leaves the NyxID Authorization header, and the interactive workflow bearer is never
used for the chrono request. The token being protected no longer justifies the stack built to
protect it.

Issue #2921 requested a decision between keeping runc + Landlock + Vault (option A) and
switching to gVisor with the token injected directly (option B). PR #2924 demonstrated the
gVisor runner on a live gVisor node: Codex starts with its inner sandbox disabled, without
Landlock or Bubblewrap fallback, and reaches the NyxID gateway with the system CA bundle.

## Decision

Managed `codex_exec` adopts option B.

The runner image (`containers/codex-runner`) carries no inner-sandbox machinery: Bubblewrap,
the `use_legacy_landlock` selection, the fail-closed Landlock preflight, and the
Credential-Proxy MITM CA trust are removed. Codex executes with its inner sandbox disabled as
the non-root `codex` user; escape isolation is the gVisor boundary, and runner pods must be
scheduled under the `gvisor` RuntimeClass.

The five-minute `llm:proxy` delegation token is injected directly as request-local
`NYXID_LLM_TOKEN` through execd's native environment map. There is no sandbox-side Credential
Vault, no placeholder substitution, and no TLS-intercepting credential proxy. The sandbox
create call requests no `networkPolicy` and no `credentialProxy`.

Egress scoping is an IP-level Kubernetes NetworkPolicy owned by operations. It is coarser
than the former FQDN allow-list because the NyxID gateway sits behind a shared CDN range.
The `dns+nft` egress sidecar and the dedicated runc + Ubuntu Landlock tenant are retired for
this path.

The accepted worst case for a fully compromised sandbox is one user's LLM quota for at most
five minutes: no durable credential, no account takeover, no lateral movement. In exchange
the path gains materially stronger escape isolation and sheds four moving parts.

## Consequences

The managed credential boundary in `docs/canon/managed-codex-execution.md` is otherwise
unchanged: the per-user agent key stays only in `ISecretVault`, is resolved immediately
before the NyxID request, and is used only as that request's Authorization value. The NyxID
UserService gate (`forward_access_token=false`, `inject_delegation_token=true`,
`delegation_token_scope=llm:proxy`) remains validated at provisioning and rotation.

Issue #2899 narrows: its remaining scope is replacing the persistent invocation key with a
short-lived caller capability and making caller-credential non-forwarding immutable or
request-level fail-closed. Its third clause — moving the delegated LLM token behind a sandbox
credential vault — is closed by this decision as rejected, not deferred, because satisfying
it forces the weaker-isolation runtime.

Chrono-sandbox creates gVisor sandboxes for the codex path and injects the token as
environment; operations applies the tenant NetworkPolicy and PID/resource limits. Neither
change is owned by this repository; this ADR fixes the contract they implement. The rollout
runbook (`docs/operations/2026-07-16-managed-codex-exec-rollout.md`) reflects the gVisor
prerequisites, and FQDN-level egress claims are withdrawn from all managed codex_exec
documents.
