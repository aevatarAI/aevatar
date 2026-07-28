# Managed codex_exec rollout runbook

This runbook enables Aevatar's NyxID-to-chrono-sandbox managed Codex path. The feature is disabled by default and must remain restricted to trusted internal users during P0.

## Ownership

Aevatar owns the typed tool contract, Application readiness coordinator,
per-user credential actor/projection, `ISecretVault` storage, diagnostic
lifecycle API, fixed NyxID proxy request, and sanitized failure mapping.

NyxID owns agent-key scope enforcement and the temporary five-minute `proxy:*` delegation token injected into internal-canary calls to `chrono-sandbox`.

Chrono-sandbox owns the deployed OpenSandbox control plane, immutable runner image, fixed Codex command, request-local delegation-token environment mapping, resource limits, output bounds, cancellation, cleanup, and live execution proof. Operations owns the gVisor tenant and its IP-level egress NetworkPolicy (ADR-0044), deploys and configures NyxID and chrono-sandbox, and does not receive or store users' agent keys.

## Operations prerequisites

Before enabling Aevatar, operations must confirm:

- the NyxID UserService that fronts Aevatar temporarily uses
  `forward_access_token=true` for the internal P0 so authenticated users can
  call the explicit credential lifecycle API; normal `codex_exec` does not use
  that bearer to provision or repair a credential
- the `chrono-sandbox` service is deployed from commit `1e8134d` or a
  descendant and exposes `POST /codex/execute`
- its NyxID service definition is active with `forward_access_token=false`
- `inject_delegation_token=true` and `delegation_token_scope=proxy:*`
- each canary user directly owns an active `chrono-sandbox` UserService
- each canary user has a usable `chrono-llm-public` route
- chrono-sandbox sets `NYXID_LLM_PROXY_URL=https://nyx-api.chrono-ai.fun/api/v1/proxy/s/chrono-llm-public`; it does not use `/api/v1/llm/gateway/v1` or `/api/v1/llm/chrono-llm-public/v1`
- chrono-sandbox can pull the approved `containers/codex-runner` image digest
- runner pods are scheduled under the `gvisor` RuntimeClass with Codex's inner sandbox disabled per ADR-0044; there is no Landlock preflight, and the sandbox create call requests no `networkPolicy` and no `credentialProxy`
- chrono-sandbox validates the injected token before sandbox creation and passes it only as request-local `NYXID_LLM_TOKEN` through execd's native environment map
- its OpenSandbox runtime has the required quota, PID, timeout, and cleanup controls, and operations has applied the IP-level egress NetworkPolicy for the codex tenant (coarser than an FQDN allow-list; the NyxID gateway sits behind a shared CDN range)
- a chrono-owned end-to-end smoke returns exact `CODEX_EXEC_READY` and proves sandbox cleanup

The runner image itself must contain no provider, NyxID, OpenSandbox control-plane, or user credential. The P0 delegation token must not be written to a shell wrapper, profile, workspace, persisted session, logs, or result. Aevatar no longer needs an OpenSandbox endpoint or API key.

The internal P0 uses two distinct NyxID UserService policies. The UserService
fronting Aevatar temporarily forwards the interactive access token so the same
authenticated user can call the explicit lifecycle API, which confirms
`/users/me` before creating or repairing that user's Agent Key. The user's
`chrono-sandbox` UserService must instead keep
`forward_access_token=false`, `inject_delegation_token=true`, and exact
`proxy:*` delegation; Aevatar validates that chrono policy during explicit
provisioning, reconciliation, and rotation, but cannot prevent the service owner
from changing it later.

The five-minute runner token can access other NyxID REST proxy services
available to the same user, and Aevatar ingress temporarily handles the
forwarded interactive bearer, so keep the rollout restricted to trusted
internal users and operators. Do not use `All` beyond that internal population
until #2899 adds immutable/version-bound caller-credential non-forwarding,
NyxID replaces `proxy:*` with authorization limited to `chrono-llm-public`,
and the explicit lifecycle boundary no longer depends on forwarding the
interactive bearer.

## Configure Aevatar

Start with an explicit NyxID user allowlist:

```text
Aevatar__CodexExecution__ManagedSandbox__Enabled=true
Aevatar__CodexExecution__ManagedSandbox__RolloutBoundary=InternalOnly
Aevatar__CodexExecution__ManagedSandbox__Eligibility__Mode=Allowlist
Aevatar__CodexExecution__ManagedSandbox__Eligibility__AllowedNyxIdUserIds__0=<canary-nyxid-user-id>
Aevatar__CodexExecution__ManagedSandbox__CredentialLifetimeDays=30
Aevatar__CodexExecution__ManagedSandbox__MaxResponseBytes=1048576
Aevatar__CodexExecution__ManagedSandbox__MutationLeaseSeconds=300
Aevatar__CodexExecution__ManagedSandbox__MutationCompletionSeconds=240
Aevatar__CodexExecution__ManagedSandbox__ExecutionLifecycleGraceSeconds=120
Aevatar__NyxId__MaxRequestDurationSeconds=330
```

After the internal cohort is ready, `All` mode removes the Aevatar allowlist:

```text
Aevatar__CodexExecution__ManagedSandbox__Eligibility__Mode=All
```

Do not set any `AllowedNyxIdUserIds` entry in `All` mode. `All` does not create
NyxID UserServices: each user must already have a personal active
`chrono-sandbox` UserService and a usable `chrono-llm-public` route.
Keep `RolloutBoundary=InternalOnly`; startup rejects any enabled configuration
without that explicit boundary, and no public boundary is supported while
delegation scope remains `proxy:*`.

Do not configure an OpenSandbox URL/API key, runner image, model, provider URL,
or delegation token in Aevatar. Those belong to chrono-sandbox and NyxID.

## Configure chrono-sandbox and proxy deadlines

Deploy chrono-sandbox commit `1e8134d` or a descendant. No additional
chrono-sandbox source change is required for this rollout. Keep these values:

```text
MANAGED_CODEX_ENABLED=true
CODEX_TIMEOUT_MAX_SECS=180
CODEX_CLEANUP_TIMEOUT_SECS=30
SANDBOX_TIMEOUT_SECS=30
```

Set the NyxID/ingress non-streaming proxy timeout to at least 315 seconds. The
Aevatar NyxID `HttpClient` ceiling is 330 seconds, and every Workflow canary
must use an outer timeout of at least 360 seconds. The required ordering is:

```text
180s chrono execution
  < 300s Aevatar managed request
  < >=315s NyxID/ingress proxy
  < 330s Aevatar NyxID HttpClient
  < >=360s Workflow canary
```

## Explicit credential preparation

Normal `codex_exec` never creates or repairs a credential. Before the canary,
the user calls the idempotent provision/reconciliation endpoint with that
user's authenticated NyxID bearer; there is no request body or target user
parameter:

```bash
curl -i -X POST \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential
```

A `202 Accepted` response means the command was admitted, not that the
projection is already visible. Read the status endpoint on a bounded
operational deadline; stop rather than polling indefinitely:

```bash
curl -sS \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential
```

Proceed only when a newer authoritative `state_version` reports
`execution_ready=true` and `execution_readiness_reason=ready`. The response
includes lifecycle `status`, but `status=active` alone is not sufficient: an
active descriptor may still fail its owner, Vault reference, or service-binding
invariants. The response never includes a Vault reference or raw key.

If reconciliation cannot preserve a valid credential, force replacement with
`/rotate`, then repeat the same bounded status readback. Rotation and revocation
use the same caller identity:

```bash
curl -i -X POST \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential/rotate

curl -i -X DELETE \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential
```

## Workflow canaries

Invoke the public Ornn skill `aevatar-codex-exec-workflow-sample` and its
`codex-exec-check` workflow directly as the prepared eligible user. Use a
Workflow timeout of at least 360 seconds for all three canaries:

1. a trivial exact-output task that returns `CODEX_EXEC_READY`;
2. a task designed to execute for approximately 80 seconds, proving the old
   100-second HTTP ceiling is absent;
3. a complex task allowed to use the full 180-second chrono execution budget.

Every workflow must finish with:

- `status=succeeded` for a successful task and `target=managed_sandbox`
- exact or task-specific bounded output after trimming
- a sanitized chrono diagnostic ID
- confirmed sandbox deletion before the chrono terminal response

Capture the Workflow terminal result, Aevatar's sanitized result or failure,
the chrono diagnostic ID, and sandbox deletion evidence for every run. Confirm
there is no 100-second local timeout, no hidden credential mutation wait, no
misleading chrono 502 for a true deadline, and no raw key or interactive bearer
in Aevatar, NyxID, chrono-sandbox, runner logs, Actor state, or read models.

The public skill invoke endpoint uses the same trusted caller-credential extraction path as workflow chat. It resolves the authenticated NyxID subject and binding into typed `WorkflowCallerNyxIdAuthority` independently of the observatory `scopeId`; a bearer-only workflow credential is a deployment regression.

## Failure handling

- `managed_target_disabled`: enable the Aevatar kill switch only after all prerequisites pass.
- `managed_feature_not_enabled`: add the exact NyxID subject to the P0 allowlist and redeploy.
- `managed_credential_not_provisioned`: call the explicit credential POST endpoint as that user; normal `codex_exec` will not repair it.
- `managed_credential_inactive` / `managed_credential_expired`: reconcile through POST or force replacement through `/rotate`.
- `managed_credential_owner_invalid` / `managed_credential_reference_invalid` / `managed_credential_service_binding_invalid`: stop execution and run explicit reconciliation; do not patch the read model.
- `managed_user_authorization_unavailable`: an explicit lifecycle action needs the current user's bearer; do not substitute an operator credential.
- `managed_credential_commit_timeout`: Actor commit or Projection Session observation did not finish inside the bounded mutation window; inspect dispatch/projection health rather than polling in the workflow.
- `managed_user_services_unavailable`: the user's required `chrono-sandbox` or `chrono-llm-public` UserService is absent, ambiguous, inactive, or has invalid delegation configuration.
- `nyxid_identity_mismatch`: the authenticated claim and `/users/me` owner differ; do not override it.
- `chrono_sandbox_service_*`: repair the user's exact NyxID UserService or delegation settings.
- `chrono_llm_route_unavailable`: repair the user's `chrono-llm-public` readiness.
- `managed_credential_untracked_key_exists`: reconcile and revoke the old named key before retrying.
- `managed_credential_mutation_in_progress`: another mutation holds the user's distributed lease; retry after it completes.
- `managed_credential_cleanup_pending`: an external NyxID/Vault cleanup track is still incomplete; retry with the same user bearer after the dependency recovers.
- `managed_credential_persistence_pending`: Actor dispatch may already have been admitted; do not delete or rotate the new key manually. Re-read status, then retry the same lifecycle action so deterministic reconciliation can complete.
- `managed_credential_vault_unavailable`: Vault reconciliation could not determine whether the deterministic reference exists. Restore Vault health and retry; do not revoke the active NyxID key manually.
- `managed_credential_unavailable` / `managed_credential_invalid`: inspect projection and Vault health without exposing the secret.
- `managed_proxy_authorization_denied`: inspect the exact agent-key service grant and NyxID proxy policy.
- `managed_proxy_target_unavailable`: verify the user's `chrono-sandbox` UserService and deployed route.
- `managed_proxy_timeout` / `managed_proxy_unavailable`: inspect NyxID and chrono-sandbox capacity.
- `managed_response_invalid` / `managed_response_too_large`: correlate with the sanitized chrono diagnostic ID and treat the contract drift as a rollout blocker.

## Rollback

Set `Aevatar__CodexExecution__ManagedSandbox__Enabled=false` and roll the
Aevatar deployment. This removes managed execution from tool discovery and
blocks normal execution, explicit provisioning, and rotation. Status and
revocation remain available so each provisioned canary can revoke its key.

If chrono-sandbox itself is unhealthy, operations rolls back or disables that service independently. Private SSH `codex_exec` remains controlled by its existing NyxID settings.

If the NyxID UserService forwarding policy drifts, disable managed execution immediately, restore the required policy, inspect downstream logs for exposure, rotate affected invocation keys, and keep the feature disabled until the incident is resolved.
