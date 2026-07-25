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

- the `chrono-sandbox` service is deployed and exposes `POST /codex/execute`
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

The internal P0 relies on mutable NyxID UserService policy. Aevatar validates
`forward_access_token=false` and exact `proxy:*` delegation during first-use
readiness, explicit provisioning, and rotation, but cannot prevent the service
owner from changing it later. The five-minute token can access other NyxID
REST proxy services available to the same user, so keep the rollout restricted
to trusted internal users and operators. Do not use `All` beyond that internal
population until #2899 adds immutable/version-bound caller-credential
non-forwarding and NyxID replaces `proxy:*` with authorization limited to
`chrono-llm-public`.

## Configure Aevatar

Start with an explicit NyxID user allowlist:

```text
Aevatar__CodexExecution__ManagedSandbox__Enabled=true
Aevatar__CodexExecution__ManagedSandbox__Eligibility__Mode=Allowlist
Aevatar__CodexExecution__ManagedSandbox__Eligibility__AllowedNyxIdUserIds__0=<canary-nyxid-user-id>
Aevatar__CodexExecution__ManagedSandbox__CredentialLifetimeDays=30
Aevatar__CodexExecution__ManagedSandbox__MaxResponseBytes=1048576
Aevatar__CodexExecution__ManagedSandbox__MutationLeaseSeconds=300
Aevatar__CodexExecution__ManagedSandbox__MutationCompletionSeconds=240
```

After the internal cohort is ready, `All` mode removes the Aevatar allowlist:

```text
Aevatar__CodexExecution__ManagedSandbox__Eligibility__Mode=All
```

Do not set any `AllowedNyxIdUserIds` entry in `All` mode. `All` does not create
NyxID UserServices: each user must already have a personal active
`chrono-sandbox` UserService and a usable `chrono-llm-public` route.

Do not configure an OpenSandbox URL/API key, runner image, model, provider URL,
or delegation token in Aevatar. Those belong to chrono-sandbox and NyxID.

## Canary normal path

Do not pre-provision or poll before the normal canary. Invoke the public Ornn
skill `aevatar-codex-exec-workflow-sample` and its `codex-exec-check` workflow
directly as an eligible user. The first call must:

1. derive the native NyxID subject independently of `scopeId`;
2. use the current user bearer only if key creation or repair is required;
3. commit and observe the per-user descriptor;
4. continue the same workflow call through chrono-sandbox; and
5. return exact `CODEX_EXEC_READY`.

The workflow must finish with:

- `status=succeeded`
- `target=managed_sandbox`
- output equal to `CODEX_EXEC_READY` after trimming
- a sanitized diagnostic ID

Run it for a second eligible user and use redacted projection or diagnostic
evidence to verify a distinct key ID and credential actor identity. Verify that
Aevatar, NyxID, chrono-sandbox, and runner logs contain neither user's agent
key nor either interactive bearer.

## Diagnostic lifecycle API

The manual API is retained for diagnostics, explicit rotation, revocation, and
emergency recovery. It is not part of the normal workflow sequence. The user
calls it with that user's normal authenticated NyxID bearer; there is no
request body and no target user parameter:

```bash
curl -i -X POST \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential
```

A `202 Accepted` response means the command was admitted, not that the
projection is already visible. A diagnostic client may read the status
endpoint:

```bash
curl -sS \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential
```

The response includes `enabled` and `eligible` but never includes a Vault
reference or raw key. `status=expired` is repaired transparently by an
authenticated interactive execution or explicitly through rotation.

Rotation and revocation use the same caller identity:

```bash
curl -i -X POST \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential/rotate

curl -i -X DELETE \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential
```

The public skill invoke endpoint uses the same trusted caller-credential extraction path as workflow chat. It resolves the authenticated NyxID subject and binding into typed `WorkflowCallerNyxIdAuthority` independently of the observatory `scopeId`; a bearer-only workflow credential is a deployment regression.

## Failure handling

- `managed_target_disabled`: enable the Aevatar kill switch only after all prerequisites pass.
- `managed_feature_not_enabled`: add the exact NyxID subject to the P0 allowlist and redeploy.
- `managed_user_authorization_unavailable`: the first call needs the current user's bearer to create or repair a credential; do not substitute an operator credential.
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
blocks automatic readiness, explicit provisioning, and rotation. Status and
revocation remain available so each provisioned canary can revoke its key.

If chrono-sandbox itself is unhealthy, operations rolls back or disables that service independently. Private SSH `codex_exec` remains controlled by its existing NyxID settings.

If the NyxID UserService forwarding policy drifts, disable managed execution immediately, restore the required policy, inspect downstream logs for exposure, rotate affected invocation keys, and keep the feature disabled until the incident is resolved.
