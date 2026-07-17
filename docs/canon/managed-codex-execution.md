---
title: "Managed Codex Execution"
status: active
owner: eanzhao
---

# Managed Codex Execution

This document defines the authoritative Aevatar contract for `codex_exec`. The tool has one business entry and two infrastructure targets:

- `private_ssh`: execute a fixed Codex stdin command through a caller-owned NyxID SSH service.
- `managed_sandbox`: execute a fixed Codex JSONL command in an operator-owned OpenSandbox tenant.

The targets share parsing, lifecycle events, terminal result semantics, and the workflow run authority. They do not share transport, credentials, or isolation configuration.

## Layering

`Aevatar.AI.Abstractions` owns the Protobuf target/workspace contracts and `ICodexExecutionPort`. `Aevatar.AI.ToolProviders.NyxId` owns tool argument admission and target selection. Target adapters own only infrastructure behavior:

- `PrivateSshCodexExecutionAdapter` maps `private_ssh` to the typed NyxID SSH executor.
- `OpenSandboxCodexExecutionAdapter` maps `managed_sandbox` to `Alibaba.OpenSandbox`.

The workflow run actor remains the authority for step lifecycle and terminal state. The OpenSandbox adapter does not add a run registry or durable execution state. Its process-local semaphore is only a P0 admission guard and is not a cross-node capacity fact.

## Typed request contract

`CodexExecutionTarget` is a Protobuf `oneof` containing `private_ssh` or `managed_sandbox`. `CodexExecutionWorkspace` is a separate `oneof`; P0 managed execution accepts only `empty_git`, while private SSH accepts no caller-selected workspace.

Mixed payloads fail closed. Callers cannot select:

- runner image or architecture
- provider URL, model flags, or credentials
- command, shell fragment, approval policy, or sandbox flags
- arbitrary repository or persistent session

The prompt is capped at 6000 UTF-8 bytes. Managed execution writes it to `/workspace/.aevatar/prompt.txt`; private SSH base64-encodes it before the fixed forced-command boundary. Neither path interpolates prompt text into a shell command.

## Managed credential boundary

Authenticated workflow ingress records a typed NyxID authority (`platform`, `tenant`, `external_user_id`) separately from the caller bearer. The managed adapter ignores the reusable caller bearer and asks `INyxIdCapabilityBroker` for a new short-lived capability with exactly `llm:proxy`.

The broker exchange requires the first-party NyxID user's Aevatar binding. Studio login finalization creates that binding with `platform=nyxid` and `external_user_id=sub`. Existing users whose binding predates the `llm:proxy` OAuth scope must consent again.

Only the short-lived capability enters OpenSandbox Credential Vault. The runner receives the public placeholder `credential-vault-placeholder`; Credential Proxy replaces it only for HTTPS `POST` requests matching the configured NyxID gateway host and path. The original caller token, delegated token, OpenSandbox API key, and provider credentials must not appear in image layers, workspace files, command output, tool results, or logs.

## Managed runtime policy

The runner is immutable and non-root:

```text
ghcr.io/aevatarai/codex-runner@sha256:bad7537bc07e8e151bcbaf7fea5e334db8569d71adb563998be6f5f762e42417
uid/gid: 10001
Codex CLI: 0.144.5
platforms: linux/amd64, linux/arm64
```

The adapter creates a default-deny network policy with only the NyxID gateway host allowed, enables Credential Proxy, initializes a deterministic Git repository, and runs a fail-closed Landlock preflight. The preflight must prove that `/workspace` and `.git` are writable while `/opt/aevatar-sandbox-probe` is not writable.

The fixed model command is:

```bash
codex --ask-for-approval never exec --ephemeral --json \
  - < /workspace/.aevatar/prompt.txt
```

The runtime-written Codex config selects `default_permissions = "aevatar-landlock"` and `features.use_legacy_landlock = true`. Bubblewrap and `danger-full-access` fallback are not accepted in the deployed GKE runc tenant.

Stdout and stderr are consumed through bounded SDK callbacks. A successful result requires valid JSONL, an `item.completed` agent message, and `turn.completed`. Nonzero exit, malformed JSONL, missing terminal events, timeout, cancellation, and cleanup failures map to typed failures without returning infrastructure exception text.

## Lifecycle and cleanup

Every post-create outcome attempts `KillAsync`, verifies that `GetInfoAsync` returns 404, and disposes the SDK session. Cleanup failure overrides an otherwise successful model result because the system cannot claim isolation completion while the sandbox may still exist.

P0 admission is deliberately narrow:

- disabled by default
- explicit NyxID user allowlist
- one immutable image digest
- one configured model and gateway
- maximum 180-second execution
- bounded output and process-local concurrency cap
- no persistent sessions, arbitrary repositories, or caller-funded/public queue semantics

Production rollout requirements are maintained in `docs/operations/2026-07-16-managed-codex-exec-rollout.md`.
