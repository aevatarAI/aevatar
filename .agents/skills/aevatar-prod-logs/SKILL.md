---
name: aevatar-prod-logs
description: Pull and inspect Aevatar mainnet/prod Kubernetes logs with the scoped kubeconfig at ~/Code/aelf-shared-k8s-prod.yaml. Use when debugging Aevatar online logs, Lark bot replies, NyxID relay callbacks, AgentRun/ConversationGAgent turns, Ornn skill publish behavior, approval hallucinations, webhook delivery, or console-backend stdout in prod.
---

# Aevatar Prod Logs

Use this skill when the user asks to check Aevatar online/prod/mainnet logs. The kubeconfig is scoped and already exists at:

```bash
export KUBECONFIG=/Users/zhaoyiqi/Code/aelf-shared-k8s-prod.yaml
```

Do not print kubeconfig contents, tokens, or bearer headers. Do not modify `~/.kube/config`. Default to read-only commands: `get`, `describe`, `logs`, and `auth can-i`. Do not `exec`, `delete`, `apply`, or restart prod workloads unless the user explicitly asks.

This skill is evidence-only. For any user-authenticated Aevatar production API call, workflow run, canary, or reproduction, **REQUIRED SUB-SKILL:** use `aevatar-prod-verify`. Never obtain API credentials through browser automation or replace `nyxid proxy request` with direct `curl`.

## Known Scope

The token is namespace-scoped. Avoid cluster-wide discovery such as `kubectl get pods --all-namespaces`.

- `aismart-app-mainnet`: Aevatar mainnet apps. `aevatar-console-backend` lives here.
- `aismart-mainnet`: shared mainnet infra.
- `aismart-testnet`: testnet apps.
- `aismart-shared`: shared namespace.

Current mainnet backend target:

```bash
kubectl -n aismart-app-mainnet get pods -l app=aevatar-console-backend
kubectl -n aismart-app-mainnet logs -l app=aevatar-console-backend --tail=-1 --since=2h --timestamps --all-containers=true
```

Important: when using `kubectl logs -l ...`, include `--tail=-1`; otherwise kubectl may return only the last few lines per pod and hide the incident window.

## Investigation Patterns

For Lark/NyxID relay and AgentRun turns:

```bash
kubectl -n aismart-app-mainnet logs -l app=aevatar-console-backend --tail=-1 --since=2h --timestamps --all-containers=true \
  | rg -i 'lark|nyxid-relay|channel-relay|ConversationGAgent|AgentRun|NeedsLlmReply|ReplyProduced|ReplyDispatched|LlmReplyDelivered'
```

For Ornn publish and approval checks:

```bash
kubectl -n aismart-app-mainnet logs -l app=aevatar-console-backend --tail=-1 --since=4h --timestamps --all-containers=true \
  | rg -i 'ornn|ornn-api|proxy/s/ornn|api/v1/skills|skill-format|approval|RemoteToolApproval|ToolReceipt|tool_receipts|approval_required'
```

For one known run, actor, message, or correlation id:

```bash
kubectl -n aismart-app-mainnet logs -l app=aevatar-console-backend --tail=-1 --since=4h --timestamps --all-containers=true \
  | rg '<run-id-or-message-id-or-actor-id-or-correlation-id>'
```

For previous container logs after a restart, resolve the pod first; `--previous` is pod-specific:

```bash
POD=$(kubectl -n aismart-app-mainnet get pod -l app=aevatar-console-backend -o jsonpath='{.items[0].metadata.name}')
kubectl -n aismart-app-mainnet logs "$POD" --previous --tail=500 --timestamps
```

## Reporting

Kubernetes timestamps are UTC. Convert to the user's local time when matching screenshots or chat times. If logs show the AgentRun replied but no matching outbound Ornn/NyxID HTTP request, say that plainly and distinguish it from "the tool succeeded." Do not infer approval or publish completion from LLM prose; rely on HTTP request logs and typed tool receipts.
