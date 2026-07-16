# Aevatar Codex Runner

This image is the one-shot workload used by Aevatar's managed `codex_exec` target. A trusted Aevatar infrastructure adapter creates the OpenSandbox sandbox, writes the workspace and prompt, starts the fixed Codex command, consumes JSONL, and kills the sandbox in `finally`.

The image contains no provider configuration or credentials. For public execution, the adapter must write a run-scoped Codex provider configuration whose credential is supplied through OpenSandbox Credential Vault. Never copy a local `~/.codex/auth.json` into this image.

## Contents

- Debian Bookworm-based Node.js 22 image pinned by multi-architecture digest
- `@openai/codex` pinned to `0.144.5`
- Git, Bash, Bubblewrap, CA certificates, and Tini
- non-root `codex` user with UID/GID `10001`
- writable `/workspace` and private `$CODEX_HOME`
- `SSL_CERT_FILE=/opt/opensandbox/mitmproxy-ca-cert.pem` for Credential Proxy TLS interception

The Codex version and invocation follow the official [non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode.md). The P0 runtime explicitly selects Codex 0.144.5's deprecated legacy Landlock backend because Bubblewrap cannot create its required mounts in the deployed GKE runc tenant.

## Build and smoke test

From the repository root:

```bash
bash containers/codex-runner/smoke.sh
```

The test builds `aevatar/codex-runner:0.144.5-r2-local`, starts the long-lived workload process expected by OpenSandbox, creates a deterministic Git baseline, verifies that no provider/control-plane credential is present in the image configuration, and proves that legacy Landlock permits writes inside `/workspace` while denying writes outside it.

On an ARM64 workstation, build and inspect the production-oriented AMD64 variant through emulation:

```bash
DOCKER_DEFAULT_PLATFORM=linux/amd64 \
CODEX_RUNNER_IMAGE=aevatar/codex-runner:0.144.5-r2-amd64-local \
SKIP_CODEX_RUNNER_LANDLOCK_PROBE=1 \
bash containers/codex-runner/smoke.sh
```

QEMU/Rosetta user-mode emulation does not reliably forward Landlock enforcement and can return `LandlockRestrict` even when the host kernel supports it. The explicit skip above verifies the AMD64 image structure but does not prove isolation. Native AMD64 CI and the deployed OpenSandbox direct SDK smoke must run without this override.

## P0 compatibility baseline

The public readiness sample uses these initial OpenSandbox limits:

- platforms: `linux/amd64` and `linux/arm64`
- request: `250m` CPU and `512Mi` memory
- limit: `1` CPU and `2Gi` memory
- PID limit: `128`
- ephemeral workspace: `2Gi`
- Codex command timeout: `180` seconds
- OpenSandbox server TTL: command timeout plus a `60` second cleanup guard
- bounded combined command output: `1MiB`

These values cover the `empty_git` readiness workflow, not arbitrary repository builds. Broader workloads need separately measured profiles and must not silently inherit the readiness limits.

The direct SDK smoke runs a fail-closed legacy Landlock preflight before making a model request. The profile grants full-filesystem read access and write access only to the workspace. It explicitly permits `.git`, `.agents`, and `.codex` under the workspace because Codex 0.144.5's legacy Landlock model cannot preserve the built-in `workspace-write` profile's nested read-only carve-outs. This exception is limited to an empty, ephemeral Git workspace; it is not an approval for arbitrary repositories. Running Codex as root or falling back to `danger-full-access` is not accepted.

The deployed OpenSandbox runtime must allow Landlock and `PR_SET_NO_NEW_PRIVS` for UID `10001`. Runner pods using Credential Vault must not also receive a transparent service-mesh sidecar because both mechanisms intercept traffic in the same network namespace. The current private PSC topology also requires `Alibaba.OpenSandbox` to use `UseServerProxy=true`.

To test a prebuilt image without rebuilding:

```bash
CODEX_RUNNER_IMAGE=example/codex-runner@sha256:... \
SKIP_CODEX_RUNNER_BUILD=1 \
bash containers/codex-runner/smoke.sh
```

## OpenSandbox execution contract

The adapter writes the prompt to `/workspace/.aevatar/prompt.txt` without interpolating it into a command, then executes the fixed command:

```bash
codex --ask-for-approval never exec --ephemeral --json \
  - < /workspace/.aevatar/prompt.txt
```

The runtime-written config selects `default_permissions = "aevatar-landlock"` and `features.use_legacy_landlock = true`; callers cannot select the profile, backend, image, or flags. A successful image smoke test does not prove the deployed isolation boundary. Before public rollout, the OpenSandbox environment must separately prove Credential Vault substitution, NyxID gateway-only egress, the Landlock workspace boundary, JSONL streaming, timeout/cancellation, and `KillAsync` cleanup.
