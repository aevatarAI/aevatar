# NyxID Assistant Actions Startup Gate Design

## Problem

`AddNyxIdChat` currently registers `NyxIdAssistantActionRegistryStartupService`
unconditionally. The service fetches `GET /api/v1/assistant/actions` before the
Host accepts work and aborts Host startup when that route is unavailable.

On July 25, 2026, both configured NyxID public origins return `404` for that
route. The `aevatar-console-backend:2816202b` rollout therefore enters
`CrashLoopBackOff`, even though the browser-action handoff is unrelated to
managed `codex_exec`. This prevents the credential-selection fix from reaching
production.

## Decision

NyxID Assistant browser actions become an explicitly enabled capability:

```json
{
  "Aevatar": {
    "NyxId": {
      "AssistantActions": {
        "Enabled": false
      }
    }
  }
}
```

`Enabled` defaults to `false`.

- When disabled, Aevatar performs no Assistant action-registry HTTP request.
  Dependency injection supplies an immutable registry with no executable
  actions. Any browser-action request therefore fails closed with the existing
  `NYXID_ACTION_UNSUPPORTED` contract.
- When enabled, the current strict startup behavior remains authoritative:
  Aevatar fetches the public registry once, pins schema version `4` and revision
  `nyxid-assistant-actions.v4`, and fails Host startup for an unavailable,
  malformed, or incompatible registry.

The disabled registry is process configuration, not workflow, actor, session,
or user fact state. It contains no mutable runtime registry and does not create
a second business pipeline.

## Boundaries

- This change does not alter managed Codex eligibility, credential lifecycle,
  `ISecretVault`, Agent Key use, chrono-sandbox transport, or runner delegation.
- This change does not add a fallback manifest. NyxID remains authoritative
  whenever Assistant actions are enabled.
- This change does not silently downgrade an explicitly enabled deployment.
  Enabled deployments still fail fast when their required NyxID contract is
  unavailable.
- Operations may enable the capability only after the NyxID registry endpoint
  is deployed and returns the supported schema and revision.

## Components

### `NyxIdAssistantActionsOptions`

A typed configuration object owns the `Enabled` flag under
`Aevatar:NyxId:AssistantActions`.

### `NyxIdAssistantActionRegistry`

An internal factory creates the immutable disabled registry using the supported
schema/revision and an empty executable-action map. Existing validation APIs
therefore reject all actions without introducing nullable service resolution.

### `AddNyxIdChat`

Composition binds the typed option once:

- disabled: register only the disabled registry;
- enabled: register the snapshot, HTTP source, registry factory, and startup
  hosted service.

## Verification

Tests must prove:

1. Empty/default configuration registers no Assistant registry startup hosted
   service and resolves a registry that rejects `service.connect`.
2. `AssistantActions:Enabled=true` registers the strict startup hosted service.
3. Existing registry parsing and HTTP-route tests remain green.
4. Mainnet Host composition starts with the default disabled configuration.
5. Workflow Host tests, architecture guards, and the full solution build remain
   green.

