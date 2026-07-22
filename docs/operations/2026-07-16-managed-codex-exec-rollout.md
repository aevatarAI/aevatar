# Managed codex_exec rollout runbook

This runbook enables Aevatar's NyxID-to-chrono-sandbox managed Codex path. The feature is disabled by default and must remain restricted to explicit internal users during P0.

## Ownership

Aevatar owns the typed tool contract, per-user credential actor/projection, `ISecretVault` storage, authenticated self-service lifecycle API, fixed NyxID proxy request, and sanitized failure mapping.

NyxID owns agent-key scope enforcement and the five-minute `llm:proxy` delegation token injected into calls to `chrono-sandbox`.

Chrono-sandbox owns the deployed OpenSandbox control plane, immutable runner image, fixed Codex command/profile, request-local delegation-token environment mapping, egress policy, resource limits, output bounds, cancellation, cleanup, and live execution proof. Operations deploys and configures NyxID and chrono-sandbox; it does not receive or store users' agent keys.

## Operations prerequisites

Before enabling Aevatar, operations must confirm:

- the `chrono-sandbox` service is deployed and exposes `POST /codex/execute`
- its NyxID service definition is active with `forward_access_token=false`
- `inject_delegation_token=true` and `delegation_token_scope=llm:proxy`
- each canary user directly owns an active `chrono-sandbox` UserService
- each canary user has a usable `chrono-llm-public` route
- chrono-sandbox can pull the approved `containers/codex-runner` image digest
- chrono-sandbox validates the injected token before sandbox creation and passes it only as request-local `NYXID_LLM_TOKEN` through execd's native environment map
- its OpenSandbox runtime has the required kernel, egress, quota, timeout, and cleanup controls
- a chrono-owned end-to-end smoke returns exact `CODEX_EXEC_READY` and proves sandbox cleanup

The runner image itself must contain no provider, NyxID, OpenSandbox control-plane, or user credential. The P0 delegation token must not be written to a shell wrapper, profile, workspace, persisted session, logs, or result. Aevatar no longer needs an OpenSandbox endpoint or API key.

The internal P0 relies on mutable NyxID UserService policy. Aevatar validates `forward_access_token=false` during credential provisioning and rotation, but cannot prevent the service owner from changing it later. Keep the rollout restricted to trusted internal users and operators. Do not broaden eligibility until #2899 adds immutable/version-bound policy or request-level fail-closed caller-credential non-forwarding.

## Configure Aevatar

P0 configuration uses an explicit NyxID user allowlist:

```text
Aevatar__CodexExecution__ManagedSandbox__Enabled=true
Aevatar__CodexExecution__ManagedSandbox__ProvisioningAllowedNyxIdUserIds__0=<canary-nyxid-user-id>
Aevatar__CodexExecution__ManagedSandbox__CredentialLifetimeDays=30
Aevatar__CodexExecution__ManagedSandbox__MaxResponseBytes=1048576
Aevatar__CodexExecution__ManagedSandbox__MutationLeaseSeconds=300
Aevatar__CodexExecution__ManagedSandbox__MutationCompletionSeconds=240
```

Do not configure an OpenSandbox URL/API key, runner image, model, provider URL, or delegation token in Aevatar. Those belong to chrono-sandbox and NyxID. The P0 credential model has no allow-all admission switch; every enabled user must appear explicitly in `ProvisioningAllowedNyxIdUserIds`.

## Provision a canary user

The canary user performs self-service provisioning with that user's normal authenticated NyxID bearer. There is no request body and no target user parameter:

```bash
curl -i -X POST \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential
```

A `202 Accepted` response means the command was admitted, not that the projection is already visible. Poll the read-only status endpoint:

```bash
curl -sS \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential
```

Proceed only after it reports `status=active`. `status=expired` requires rotation or revocation/provisioning before execution. The response never includes a Vault reference or raw key.

Rotation and revocation use the same caller identity:

```bash
curl -i -X POST \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential/rotate

curl -i -X DELETE \
  -H "Authorization: Bearer ${NYXID_ACCESS_TOKEN}" \
  https://<aevatar-host>/api/managed-codex/credential
```

## Workflow proof

After status is active, run the public Ornn skill `aevatar-codex-exec-workflow-sample` and its `codex-exec-check` workflow as that user. Configuration checks and a standalone chrono smoke do not prove workflow identity propagation.

The workflow must finish with:

- `status=succeeded`
- `target=managed_sandbox`
- output equal to `CODEX_EXEC_READY` after trimming
- a sanitized diagnostic ID

Verify that Aevatar, NyxID, chrono-sandbox, and runner logs contain neither the user's agent key nor the interactive bearer.

## Failure handling

- `managed_target_disabled`: enable the Aevatar kill switch only after all prerequisites pass.
- `managed_feature_not_enabled`: add the exact NyxID subject to the P0 allowlist and redeploy.
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

Set `Aevatar__CodexExecution__ManagedSandbox__Enabled=false` and roll the Aevatar deployment. This removes managed execution from tool discovery and blocks new provisioning/rotation. Status and revocation remain available so each provisioned canary can revoke its key.

If chrono-sandbox itself is unhealthy, operations rolls back or disables that service independently. Private SSH `codex_exec` remains controlled by its existing NyxID settings.

If the NyxID UserService forwarding policy drifts, disable managed execution immediately, restore the required policy, inspect downstream logs for exposure, rotate affected invocation keys, and keep the feature disabled until the incident is resolved.
