# OpenSandbox Codex Runner Smoke

This .NET 10 tool is the direct `Alibaba.OpenSandbox` SDK proof for the managed `codex_exec` runner. It creates one sandbox, enables default-deny egress and Credential Vault, writes an empty Git workspace and runtime Codex provider configuration, streams bounded JSONL, requires exact `CODEX_EXEC_READY`, calls `KillAsync` on every post-create outcome, and requires the lifecycle API to report the sandbox absent before returning success.

Secrets are accepted only through environment variables. Do not pass them as command-line arguments or write them to checked-in files.

## Offline verification

```bash
dotnet build tools/opensandbox-codex-smoke/Aevatar.OpenSandbox.CodexRunner.Smoke.csproj --configuration Release
dotnet run --project tools/opensandbox-codex-smoke/Aevatar.OpenSandbox.CodexRunner.Smoke.csproj --configuration Release --no-build -- --self-test
```

## Live proof

```bash
export OPEN_SANDBOX_DOMAIN="opensandbox.example.internal"
export OPEN_SANDBOX_PROTOCOL="https"
export OPEN_SANDBOX_API_KEY="<secret>"
export OPEN_SANDBOX_USE_SERVER_PROXY="true"
export CODEX_RUNNER_IMAGE="ghcr.io/aevatarai/codex-runner@sha256:<digest>"
export CODEX_RUNNER_ARCH="amd64"
export NYXID_LLM_GATEWAY_URL="https://nyx.example.com/api/v1/llm/gateway/v1"
export NYXID_LLM_DELEGATION_TOKEN="<five-minute-llm-proxy-token>"
export CODEX_MODEL="gpt-5.4"

dotnet run --project tools/opensandbox-codex-smoke/Aevatar.OpenSandbox.CodexRunner.Smoke.csproj --configuration Release
```

`CODEX_RUNNER_IMAGE` must be a registry digest, not a mutable tag. `OPEN_SANDBOX_USE_SERVER_PROXY` depends on whether the Aevatar host can reach sandbox endpoints directly; operations must provide the correct value.

Before the model call, the tool runs a nested sandbox preflight as UID/GID `10001`. It must write inside `/workspace` and must fail to write to the otherwise writable `/opt/aevatar-sandbox-probe`. Missing Bubblewrap/user-namespace/mount support therefore fails the proof instead of falling back to `danger-full-access`.

Success emits sanitized JSON containing the OpenSandbox sandbox/execution diagnostic IDs, exact output, image digest, architecture, elapsed time, and `cleanup=sandbox_absent`. It never prints either API key or the delegated NyxID token.

## Ownership boundary

Aevatar owns and verifies the runner Dockerfile, fixed command, runtime-written Codex config, bounded JSONL parser, direct SDK lifecycle, fake-token contract, default-deny egress request, resource request/limits, and cleanup confirmation in this repository.

Operations must provide an OpenSandbox deployment with server `>= 0.2.0`, egress `>= 1.1.1`, Credential Proxy in `dns+nft` mode, no injected service-mesh sidecar on runner pods, non-root Bubblewrap-compatible namespace/mount policy, registry access to the pinned image digest, PID and concurrency limits, and private control-plane connectivity. The proof is not complete until this command succeeds against that deployed environment and the platform confirms the deleted sandbox pod/process tree is gone.
