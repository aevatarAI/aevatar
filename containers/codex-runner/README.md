# Aevatar Codex Runner

This image is the one-shot workload used by Aevatar's managed `codex_exec` target. A trusted Aevatar infrastructure adapter creates the OpenSandbox sandbox, writes the workspace and prompt, starts the fixed Codex command, consumes JSONL, and kills the sandbox in `finally`.

> **gVisor variant.** This image implements option B of aevatarAI/aevatar#2921 (see #2922): Codex runs on a **gVisor** runtime with **no inner Codex sandbox** — the gVisor Sentry is the isolation boundary. It has no Bubblewrap, no `use_legacy_landlock`, and no Credential-Proxy MITM. The five-minute delegation token is injected directly at run time; there is no Credential Vault placeholder substitution. Do not deploy this variant against the runc + Landlock design — use the default runc image for that.

The image contains no provider configuration or credentials. For public execution, the adapter writes a run-scoped Codex provider configuration whose credential is the directly-injected short-lived NyxID delegation token. Never copy a local `~/.codex/auth.json` into this image.

## Contents

- Debian Bookworm-based Node.js 22 image pinned by multi-architecture digest
- `@openai/codex` pinned to `0.144.5`
- Git, Bash, CA certificates, and Tini
- non-root `codex` user with UID/GID `10001`
- writable `/workspace` and private `$CODEX_HOME`
- no Bubblewrap and no `SSL_CERT_FILE`: Codex runs no inner sandbox and reaches the NyxID gateway directly with the system CA bundle

The Codex version and invocation follow the official [non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode.md). Under gVisor, neither Codex inner-sandbox backend is available (gVisor's Sentry does not implement Landlock — `landlock_create_ruleset` returns `ENOSYS` — and Bubblewrap cannot initialize either), so Codex runs with its inner sandbox disabled and relies on the gVisor boundary.

## Build and smoke test

From the repository root:

```bash
bash containers/codex-runner/smoke.sh
```

The test builds `aevatar/codex-runner:0.144.5-gvisor-local`, starts the long-lived workload process expected by OpenSandbox, creates a deterministic Git baseline, verifies that no provider/control-plane credential is present in the image configuration, and confirms Codex starts with its inner sandbox disabled (reporting `danger-full-access`) without falling back to a runc-only Landlock/Bubblewrap backend. Isolation is a property of the deployed gVisor runtime, not the image, so there is no local Landlock probe.

On an ARM64 workstation, build and inspect the production-oriented AMD64 variant through emulation:

```bash
DOCKER_DEFAULT_PLATFORM=linux/amd64 \
CODEX_RUNNER_IMAGE=aevatar/codex-runner:0.144.5-gvisor-amd64-local \
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

Under the gVisor model the isolation boundary is the gVisor Sentry, so Codex runs with its inner sandbox disabled (`danger-full-access` from Codex's own perspective) — this is safe precisely because Codex is not the enforcing layer here; gVisor is. This is limited to an empty, ephemeral Git workspace and is not an approval for arbitrary repositories.

The deployed runtime must schedule runner pods under the `gvisor` RuntimeClass. Because there is no Credential Vault / Credential Proxy under gVisor, the short-lived NyxID delegation token is injected directly into the run-scoped Codex provider configuration; egress scoping is enforced at the platform layer (e.g. NetworkPolicy) rather than by an egress sidecar.

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

The runtime-written config disables the Codex inner sandbox (gVisor is the boundary); callers cannot select the profile, backend, image, or flags. A successful image smoke test does not prove the deployed isolation boundary. Before public rollout, the OpenSandbox environment must separately prove direct short-lived-token injection, NyxID gateway-only egress, the gVisor isolation boundary, JSONL streaming, timeout/cancellation, and `KillAsync` cleanup.
