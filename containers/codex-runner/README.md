# Chrono-Sandbox Codex Runner

This immutable image is consumed by chrono-sandbox for the managed `codex_exec` target. Aevatar does not create OpenSandbox sessions directly. Aevatar calls the user's `chrono-sandbox` service through NyxID; chrono-sandbox owns session creation, workspace setup, the fixed Codex command/profile, output parsing, cancellation, and cleanup.

The image contains no provider or control-plane configuration and no credentials. Aevatar sends the persistent per-user invocation key only to NyxID and does not place it in the chrono request body. The internal P0 relies on the mutable NyxID UserService policy keeping `forward_access_token=false`; #2899 must make caller-credential non-forwarding immutable or request-level fail-closed before broad rollout. Under that policy, chrono-sandbox passes only NyxID's five-minute delegation token as request-local `NYXID_LLM_TOKEN` through execd's native environment map. It must never be baked into the image, interpolated into a shell command, written to the workspace, persisted, logged, or returned. Never copy a local `~/.codex/auth.json` into this image.

## Contents

- Debian Bookworm-based Node.js 22 image pinned by multi-architecture digest
- `@openai/codex` pinned to `0.144.5`
- Git, Bash, Bubblewrap, CA certificates, and Tini
- non-root `codex` user with UID/GID `10001`
- writable `/workspace` and private `$CODEX_HOME`
- `SSL_CERT_FILE=/opt/opensandbox/mitmproxy-ca-cert.pem` for Credential Proxy TLS interception

The Codex version and invocation follow the official [non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode.md). This baseline explicitly selects Codex 0.144.5's legacy Landlock backend because Bubblewrap cannot create its required mounts in the deployed GKE runc tenant.

## Build and image smoke

From the repository root:

```bash
bash containers/codex-runner/smoke.sh
```

The test builds `aevatar/codex-runner:0.144.5-r2-local`, starts the long-lived workload process expected by OpenSandbox, creates a deterministic Git baseline, verifies that no provider/control-plane credential is present in image configuration, and proves that legacy Landlock permits writes inside `/workspace` while denying writes outside it.

On an ARM64 workstation, inspect the AMD64 variant through emulation:

```bash
DOCKER_DEFAULT_PLATFORM=linux/amd64 \
CODEX_RUNNER_IMAGE=aevatar/codex-runner:0.144.5-r2-amd64-local \
SKIP_CODEX_RUNNER_LANDLOCK_PROBE=1 \
bash containers/codex-runner/smoke.sh
```

QEMU/Rosetta user-mode emulation does not reliably forward Landlock enforcement. The explicit skip verifies image structure but does not prove isolation. Native AMD64 CI and chrono-sandbox's deployed runtime smoke must run without this override.

To test a prebuilt image without rebuilding:

```bash
CODEX_RUNNER_IMAGE=example/codex-runner@sha256:... \
SKIP_CODEX_RUNNER_BUILD=1 \
bash containers/codex-runner/smoke.sh
```

## Runtime baseline

The initial chrono-sandbox profile uses:

- platforms `linux/amd64` and `linux/arm64`
- `250m` CPU and `512Mi` memory requests
- `1` CPU and `2Gi` memory limits
- PID limit `128`
- `2Gi` ephemeral workspace
- 180-second Codex command timeout
- server TTL equal to command timeout plus a 60-second cleanup guard
- 1 MiB bounded combined command output

These values cover the `empty_git` readiness workflow, not arbitrary repository builds. Broader workloads need separately measured profiles and must not silently inherit this baseline.

Chrono-sandbox must run a fail-closed legacy Landlock preflight before making a model request. The profile grants full-filesystem read access and write access only to the workspace. It explicitly permits `.git`, `.agents`, and `.codex` under the workspace because Codex 0.144.5's legacy model cannot preserve the built-in `workspace-write` profile's nested read-only carve-outs. Running Codex as root or falling back to `danger-full-access` is not accepted.

The deployed OpenSandbox runtime must allow Landlock and `PR_SET_NO_NEW_PRIVS` for UID `10001`. Runner pods using Credential Vault must not also receive a transparent service-mesh sidecar because both mechanisms intercept traffic in the same network namespace.

## Chrono-sandbox execution contract

Chrono-sandbox writes the prompt to `/workspace/.aevatar/prompt.txt` without shell interpolation, then executes:

```bash
codex --ask-for-approval never exec --ephemeral --json \
  - < /workspace/.aevatar/prompt.txt
```

The runtime-written config selects `default_permissions = "aevatar-landlock"` and `features.use_legacy_landlock = true`; callers cannot select the profile, backend, image, provider, or flags.

An image smoke does not prove the deployed boundary. Before rollout, chrono-sandbox must separately prove delegation-token validation and redaction, NyxID gateway-only egress, the Landlock workspace boundary, bounded JSONL parsing, timeout/cancellation, and verified OpenSandbox cleanup. Record only sanitized execution IDs and never the delegation token. Issue #2899 replaces the P0 process-readable environment token with OpenSandbox Credential Vault substitution before public rollout.
