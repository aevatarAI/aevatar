# Managed codex_exec rollout runbook

This runbook enables the direct Aevatar-to-OpenSandbox path after the immutable runner and offline tests have passed. The feature is disabled by default and must remain allowlisted during P0.

## Ownership

Aevatar owns the typed tool contract, fixed command, short-lived NyxID credential exchange, runtime config, JSONL validation, failure mapping, and unconditional cleanup.

Operations owns the deployed OpenSandbox endpoint and API key, private control-plane connectivity, image pull access, Credential Proxy/egress components, kernel/runtime support, resource quotas, observability, and the live tenant proof.

No chrono-sandbox API or code change is required.

## OpenSandbox prerequisites

Confirm all of the following before enabling Aevatar:

- OpenSandbox server `>= 0.2.0`
- egress component `>= 1.1.1`
- Credential Proxy in `dns+nft` mode
- no service-mesh sidecar injected into runner pods
- private domain reachable from the trusted Aevatar workload with `UseServerProxy=true`
- runner registry digest pullable on the selected architecture
- non-root Landlock and `PR_SET_NO_NEW_PRIVS` available for UID/GID `10001`
- proxy CA mounted at `/opt/opensandbox/mitmproxy-ca-cert.pem`
- PID, CPU, memory, ephemeral-storage, and tenant concurrency limits configured

Use this immutable image:

```text
ghcr.io/aevatarai/codex-runner@sha256:bad7537bc07e8e151bcbaf7fea5e334db8569d71adb563998be6f5f762e42417
```

Initial request/limit values are `250m` CPU request, `512Mi` memory request, `1` CPU limit, `2Gi` memory limit, and `2Gi` ephemeral storage.

## Run the tenant smoke first

Follow `tools/opensandbox-codex-smoke/README.md`. Supply secrets only through the trusted workload's environment or Secret store. Use a short-lived token carrying only `llm:proxy`; never paste it into GitHub, logs, or checked-in configuration.

The smoke is successful only when it:

1. creates and awaits one sandbox
2. installs a sanitized Credential Vault binding
3. initializes the empty Git workspace
4. passes the native Landlock boundary probe
5. receives exact `CODEX_EXEC_READY` through the NyxID Responses gateway
6. consumes bounded JSONL
7. kills the sandbox and observes it absent

Record only redacted sandbox/execution IDs, image digest, architecture, elapsed time, exact output, and cleanup status.

## Configure Aevatar

Store `ApiKey` in Kubernetes Secret-backed configuration. Do not add it to `appsettings.json` or a ConfigMap. The .NET environment names are:

```text
Aevatar__CodexExecution__ManagedSandbox__Enabled=true
Aevatar__CodexExecution__ManagedSandbox__Domain=<private-host[:port]>
Aevatar__CodexExecution__ManagedSandbox__ApiKey=<secret-ref>
Aevatar__CodexExecution__ManagedSandbox__Protocol=https
Aevatar__CodexExecution__ManagedSandbox__UseServerProxy=true
Aevatar__CodexExecution__ManagedSandbox__RunnerImage=ghcr.io/aevatarai/codex-runner@sha256:bad7537bc07e8e151bcbaf7fea5e334db8569d71adb563998be6f5f762e42417
Aevatar__CodexExecution__ManagedSandbox__RunnerArchitecture=amd64
Aevatar__CodexExecution__ManagedSandbox__NyxIdGatewayUrl=https://nyx-api.chrono-ai.fun/api/v1/llm/gateway/v1
Aevatar__CodexExecution__ManagedSandbox__Model=gpt-5.4
Aevatar__CodexExecution__ManagedSandbox__AllowedNyxIdUserIds__0=2db990b5-29ea-4a32-acf5-0008420afa1f
Aevatar__CodexExecution__ManagedSandbox__ReadyTimeoutSeconds=60
Aevatar__CodexExecution__ManagedSandbox__CleanupTimeoutSeconds=30
Aevatar__CodexExecution__ManagedSandbox__MaxOutputBytes=1048576
Aevatar__CodexExecution__ManagedSandbox__MaxConcurrentExecutions=1
```

Keep the architecture consistent with the node pool. Startup option validation rejects mutable images, missing allowlist/API key, non-HTTPS gateway URLs, disabled server proxy, unsupported architecture, and out-of-range limits.

## NyxID account readiness

The cluster-owned Aevatar OAuth client must advertise `llm:proxy`. A user binding created before that scope was added must be refreshed through the normal NyxID/Aevatar consent flow. Do not work around `invalid_scope` by forwarding the inbound bearer or broadening the delegated scope.

For P0, allow only the intended NyxID subject. The initial subject is:

```text
2db990b5-29ea-4a32-acf5-0008420afa1f
```

After deployment, run the public Ornn skill `aevatar-codex-exec-workflow-sample` and its `codex-exec-check` workflow as that account. Health checks, option validation, and a successful standalone smoke do not prove workflow identity propagation. The workflow result must have `status=succeeded`, `target=managed_sandbox`, and `output=CODEX_EXEC_READY` exactly after trimming.

## Failure handling

- `managed_sandbox_disabled` / `target_not_configured`: host feature wiring is disabled.
- `managed_feature_not_enabled`: caller subject is absent from the allowlist.
- `nyxid_binding_required` / `nyxid_binding_revoked`: repeat the normal login/consent binding flow.
- `llm_proxy_scope_missing`: OAuth client or binding predates `llm:proxy`; refresh consent.
- `llm_service_access_missing`: binding does not grant the configured LLM resource.
- `managed_capacity_unavailable`: P0 process-local slot is busy; retry later.
- `sandbox_provisioning_failed`: inspect control-plane connectivity, API key, quota, architecture, and image pull.
- `credential_vault_binding_failed`: inspect Credential Proxy version/config without logging the token.
- `landlock_preflight_failed`: stop rollout; do not enable a weaker sandbox fallback.
- `codex_jsonl_*` / `codex_terminal_*`: correlate with the sanitized diagnostic ID.
- `sandbox_cleanup_failed`: treat as an incident and verify the pod/process tree manually.

## Rollback

Set `Aevatar__CodexExecution__ManagedSandbox__Enabled=false` and roll the Aevatar deployment. This removes the managed target from tool discovery; private SSH `codex_exec` remains independently controlled by `EnableSshExecTool`. Then verify no managed sandbox remains and revoke the OpenSandbox API key if compromise is suspected.
