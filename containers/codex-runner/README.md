# Chrono-Sandbox Codex Runner

This immutable image is consumed by chrono-sandbox for the managed `codex_exec` target. Aevatar does not create sandboxes directly. Aevatar calls the user's `chrono-sandbox` service through NyxID; chrono-sandbox owns sandbox creation, workspace setup, the fixed Codex command, output parsing, cancellation, and cleanup.

Per the #2921 decision, the managed runtime is a **gVisor** tenant and Codex runs with **no inner sandbox** — the gVisor Sentry is the isolation boundary. Neither Codex inner-sandbox backend is available under gVisor (the Sentry does not implement Landlock — `landlock_create_ruleset` returns `ENOSYS` — and Bubblewrap cannot initialize either), so the image carries no Bubblewrap, no `use_legacy_landlock` selection, no fail-closed Landlock preflight, and no Credential-Proxy MITM CA. The full architecture boundary lives in `docs/canon/managed-codex-execution.md`.

The image contains no provider or control-plane configuration and no credentials. Aevatar sends the persistent per-user invocation key only to NyxID and does not place it in the chrono request body. The internal rollout relies on the mutable NyxID UserService policy keeping `forward_access_token=false`; #2899's remaining scope makes caller-credential non-forwarding immutable or request-level fail-closed before broad rollout. Under that policy, chrono-sandbox passes only NyxID's five-minute delegation token as request-local `NYXID_LLM_TOKEN` through execd's native environment map. Direct injection of that short-lived token is the decided credential model: there is no Credential Vault placeholder substitution and no egress-sidecar interception. The token must never be baked into the image, interpolated into a shell command, written to the workspace, persisted, logged, or returned. Never copy a local `~/.codex/auth.json` into this image.

## Contents

- Debian Bookworm-based Node.js 22 image pinned by multi-architecture digest
- `@openai/codex` pinned to `0.144.5`
- Git, Bash, CA certificates, and Tini
- non-root `codex` user with UID/GID `10001`
- writable `/workspace` and private `$CODEX_HOME`
- no Bubblewrap and no `SSL_CERT_FILE`: Codex runs no inner sandbox and reaches the NyxID gateway directly with the system CA bundle

The Codex version and invocation follow the official [non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode.md).

## Build and image smoke

From the repository root:

```bash
bash containers/codex-runner/smoke.sh
```

The test builds `aevatar/codex-runner:0.144.5-gvisor-local`, starts the long-lived workload process expected by OpenSandbox, creates a deterministic Git baseline, verifies that no provider/control-plane credential and no credential-proxy CA trust is present in the image configuration, and confirms Codex starts with its inner sandbox disabled (reporting `danger-full-access`) without falling back to a runc-only Landlock/Bubblewrap backend. Isolation is a property of the deployed gVisor runtime, not the image, so there is no local Landlock probe.

On an ARM64 workstation, build and inspect the production-oriented AMD64 variant through emulation:

```bash
DOCKER_DEFAULT_PLATFORM=linux/amd64 \
CODEX_RUNNER_IMAGE=aevatar/codex-runner:0.144.5-gvisor-amd64-local \
bash containers/codex-runner/smoke.sh
```

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

The deployed runtime must schedule runner pods under the `gvisor` RuntimeClass. Codex runs as the non-root `codex` user with its inner sandbox disabled (`danger-full-access` from Codex's own perspective); this is accepted precisely because Codex is not the enforcing layer here — gVisor is. It remains limited to an empty, ephemeral Git workspace and is not an approval for arbitrary repositories. Running Codex as root is still not accepted.

Egress scoping is enforced at the platform layer (Kubernetes NetworkPolicy, IP-level and coarser than an FQDN allow-list) rather than by an egress sidecar. The sandbox create call requests no `networkPolicy` and no `credentialProxy`.

## Chrono-sandbox execution contract

Chrono-sandbox writes the prompt to `/workspace/.aevatar/prompt.txt` without shell interpolation, then executes:

```bash
codex --ask-for-approval never exec --ephemeral --json \
  - < /workspace/.aevatar/prompt.txt
```

The runtime-written config disables the Codex inner sandbox (gVisor is the boundary); callers cannot select the profile, backend, image, provider, or flags.

An image smoke does not prove the deployed boundary. Before rollout, chrono-sandbox must separately prove delegation-token validation and redaction, the applied egress NetworkPolicy and `gvisor` RuntimeClass on the codex tenant, bounded JSONL parsing, timeout/cancellation, and verified OpenSandbox cleanup. Record only sanitized execution IDs and never the delegation token.
