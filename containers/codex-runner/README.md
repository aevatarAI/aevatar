# Aevatar Codex Runner

This image is the one-shot workload used by Aevatar's managed `codex_exec` target. A trusted Aevatar infrastructure adapter creates the OpenSandbox sandbox, writes the workspace and prompt, starts the fixed Codex command, consumes JSONL, and kills the sandbox in `finally`.

The image contains no provider configuration or credentials. For public execution, the adapter must write a run-scoped Codex provider configuration whose credential is supplied through OpenSandbox Credential Vault. Never copy a local `~/.codex/auth.json` into this image.

## Contents

- Debian Bookworm-based Node.js 22 image pinned by multi-architecture digest
- `@openai/codex` pinned to `0.144.5`
- Git, Bash, Bubblewrap, CA certificates, and Tini
- non-root `codex` user with UID/GID `10001`
- writable `/workspace` and private `$CODEX_HOME`

The Codex version and invocation follow the official [non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode.md): `codex exec --ephemeral --json --sandbox workspace-write`.

## Build and smoke test

From the repository root:

```bash
bash containers/codex-runner/smoke.sh
```

The test builds `aevatar/codex-runner:0.144.5-r1-local`, starts the long-lived workload process expected by OpenSandbox, executes checks inside it, creates a deterministic Git baseline, and verifies that no provider/control-plane credential is present in the image configuration.

On an ARM64 workstation, validate the production-oriented AMD64 variant through buildx/QEMU with the same checks:

```bash
DOCKER_DEFAULT_PLATFORM=linux/amd64 \
CODEX_RUNNER_IMAGE=aevatar/codex-runner:0.144.5-r1-amd64-local \
bash containers/codex-runner/smoke.sh
```

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

Linux `workspace-write` requires Bubblewrap plus permission to create the namespaces and mounts used by it. The direct SDK smoke runs a fail-closed preflight before making a model request. The OpenSandbox runtime must permit that preflight for UID `10001`; running Codex as root or falling back to `danger-full-access` is not accepted. Runner pods using Credential Vault must not also receive a transparent service-mesh sidecar because both mechanisms intercept traffic in the same network namespace.

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
  --sandbox workspace-write - < /workspace/.aevatar/prompt.txt
```

A successful image smoke test does not prove the deployed isolation boundary. Before public rollout, the OpenSandbox environment must separately prove Credential Vault substitution, NyxID gateway-only egress, nested `workspace-write` enforcement, JSONL streaming, timeout/cancellation, and `KillAsync` cleanup.
