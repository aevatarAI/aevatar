---
title: Voice Edge Local Tools
status: Accepted
owner: eanzhao
---

# ADR-0031: Voice Edge Local Tools

## Context

Issue #2009 asks how a cloud-hosted Aevatar voice session should execute tools
that only exist on a user's LAN, such as Home Assistant controls, Frigate
snapshot capture, or ESP32 panel updates.

The active voice execution path is actor-side:

- `VoiceFunctionCallRequested` is emitted by the realtime provider.
- `VoicePresenceModule.ExecuteToolCallAsync(...)` calls `IVoiceToolInvoker`.
- `AgentToolVoiceInvoker` discovers `IAgentToolSource` tools and executes the
  selected tool.
- The actor sends the JSON result back to the realtime provider through
  `IRealtimeVoiceProvider.SendToolResultAsync(...)`.

The original transport contract was asymmetric for local tool execution. The
downstream realtime feed could carry `VoiceRealtimeFrame.function_call`, but
`VoiceControlFrame` had no typed `function_call_output` control frame that a
client could use to answer a model function call through the active voice
transport. Issue #2156 closes that protocol gap by adding typed ownership and a
typed uplink result.

NyxID already has a service and node proxy surface that reaches private-host
APIs without requiring Aevatar to add a new external feature:

- Aevatar's `NyxIdApiClient` calls `GET /api/v1/proxy/services`,
  `GET /api/v1/proxy/services/{service_id}/openapi.json`, and
  `/api/v1/proxy/s/{slug}/{path}`.
- `NyxIdConnectedServiceToolSource` turns `x-aevatar-tool` OpenAPI operations
  into `IAgentTool` instances and executes them through the NyxID proxy.
- NyxID's local node registers through `/api/v1/nodes/ws` and routes the same
  `/api/v1/proxy/s/{slug}/...` requests over an outbound WebSocket to a private
  host. Node routing is invisible to the caller.

## Decision

Use the existing NyxID service/node bridge as the short-term design direction
for LAN-only voice tools. The edge host exposes a narrow HTTP API for the local
actions, registers it in NyxID as a service bound to a NyxID node, and marks only
voice-safe operations with `x-aevatar-tool`. Aevatar invokes those operations
through NyxID's existing proxy and connected-service tool surfaces.

This is a design decision, not a new Aevatar protocol implementation. No new
NyxID endpoint, schema field, or node feature is required for issue #2009.

The first-class client-owned voice tool protocol is implemented as a typed voice
contract, not as a process-local registry:

- `VoiceFunctionCallOutput` carries `call_id`, `tool_name`, success
  `output_json`, or a typed `VoiceFunctionCallFailure`.
- `VoiceControlFrame.function_call_output` is the client-to-actor uplink.
- `VoiceToolDefinition.owner` marks actor-owned versus client-owned tools.
  Unspecified ownership remains actor-owned.
- `VoicePendingClientToolCall` lives in actor-owned
  `VoicePresenceRuntimeState`. The actor waits for a client output only by
  ending the current turn and resuming from a control-frame event or timeout
  event.
- `VoiceClientToolCallTimeoutExpired` aligns timeout semantics with the existing
  tool execution timeout and returns an honest JSON error to the provider when a
  client-owned output does not arrive.

Do not solve this gap with a Device GAgent outbound command queue in this slice.
Device command subscriptions may be useful for durable device management, but
they are a different product surface. They add a second command path for voice
tools, do not reuse the existing `IAgentToolSource` catalog, and would require a
new actor-owned command/readmodel design before they are safe.

## Current Activation Semantics

The NyxID bridge is immediately valid as an architecture because it uses
existing surfaces, but production activation must be honest about the current
voice path:

- Generic NyxID tools such as `nyxid_proxy` require
  `AgentToolRequestContext.NyxIdAccessToken`.
- `nyxid.connected_services` is intentionally opt-in through route policy for
  direct chat and is not part of `workspace.default`.
- The current `/ws/voice` host path resolves only a typed
  `voice_attach_target` and then attaches to an existing voice-enabled actor. It
  does not yet create a route-scoped `VoiceSessionActor` or stamp a per-request
  `AgentToolExecutionContext` for voice tool discovery and execution.

Therefore, the short-term bridge is appropriate when the attached actor/tool
configuration already has an explicit, host-owned credential path for the NyxID
service call. It must not be implemented by adding a hidden process-level map
from session, actor, or user IDs to NyxID tokens. If per-user connected-service
voice tools are required, the enabling work belongs with the ADR-0026
`VoiceSessionActor`/route-scoped tool execution path, or with a small explicit
actor-owned credential contract.

## Boundaries

- Aevatar does not call the LAN service directly. Calls go through NyxID proxy so
  credential injection, audit, approval, node routing, and delegation remain
  owned by NyxID.
- Aevatar does not request a NyxID feature change. The bridge uses the current
  `/proxy/services`, `/proxy/services/{service_id}/openapi.json`, and
  `/proxy/s/{slug}/...` surfaces.
- Aevatar does not keep a process-local service catalog, endpoint catalog, or
  `actorId -> edge context` registry. NyxID remains the live source for
  connected services and OpenAPI specs.
- Stable voice protocol semantics are protobuf fields. A future
  client-owned tool semantic must continue to be a typed control-frame or
  state/message field, not JSON hidden in an existing field.
- The voice actor remains the authority for the voice session, call correlation,
  timeout, and provider result submission. The edge client owns only the LAN
  side effect and the tool output it returns.
- Device GAgent outbound commands are not introduced as a shortcut for this
  design issue.

## Operational Shape

Short-term setup for a home edge server:

1. Run the local edge HTTP API on the private host or LAN.
2. Register a NyxID node on the private host and keep its outbound WebSocket
   connected.
3. Register the edge API as a NyxID custom service with `--via-node`, using an
   endpoint URL that the node can reach, such as `http://localhost:3000`.
4. Publish a narrow OpenAPI document for the local tool API and mark only
   admitted operations with `x-aevatar-tool`.
5. Enable the relevant Aevatar tool set or actor-owned tool configuration so the
   model can call either the typed connected-service tool or, where appropriate,
   the generic `nyxid_proxy` tool.

The expected runtime path is:

```text
Voice model function call
  -> VoicePresenceModule
  -> IVoiceToolInvoker / IAgentToolSource
  -> NyxID proxy tool
  -> /api/v1/proxy/s/{edge-service-slug}/{path}
  -> NyxID node outbound WebSocket
  -> edge HTTP API
  -> LAN device
```

## Consequences

- LAN reachability is solved without changing Aevatar's voice protobuf or NyxID.
- The first deployable path inherits NyxID approval, audit, node routing, and
  credential isolation.
- Tool latency includes the NyxID proxy and node hops. The voice session must
  treat this as ordinary tool latency and use existing timeout behavior.
- Per-user dynamic connected-service voice tools are not claimed as already
  solved by this ADR. They require route-scoped voice tool context before they
  are generally safe.
- The long-term transport callback protocol remains available for low-latency
  client-owned tools and for tools that should never be exposed as HTTP services.
  Its implemented form is the typed `function_call_output` uplink guarded by
  actor-owned pending call state and timeout self-signals.

## Verification

Repository evidence:

- `src/Aevatar.Foundation.VoicePresence.Abstractions/Protos/voice_presence.proto`
  defines `VoiceRealtimeFrame.function_call` and a `VoiceControlFrame` oneof
  that currently contains only `drain_acknowledged`.
- `src/Aevatar.Foundation.VoicePresence/Modules/VoicePresenceModule.cs`
  executes provider function calls through `IVoiceToolInvoker` and sends the
  result back to the provider.
- `src/Aevatar.AI.Core/Voice/AgentToolVoiceInvoker.cs` adapts
  `IAgentToolSource` to voice tool execution.
- `docs/canon/nyxid-connected-service-tools.md` documents the current
  Aevatar-connected-service tool contract and its no-local-catalog boundary.
- `src/Aevatar.AI.ToolProviders.NyxId/NyxIdConnectedServiceToolSource.cs` and
  `ConnectedServiceProxyTool.cs` discover and execute marked OpenAPI operations
  through NyxID proxy.
- `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs` exposes the proxy
  services, proxy OpenAPI, and proxy request calls.

External source read-only evidence from `/Users/chronoai/Code/NyxID`:

- `docs/quickstarts/node-proxy.md` documents the outbound node proxy path for a
  private-host API and the `/api/v1/proxy/s/<slug>/...` caller contract.
- `docs/API_DISCOVERY.md` documents `/api/v1/proxy/services` and
  `/api/v1/proxy/services/{service_id}/openapi.json`.
- `backend/src/routes.rs` registers `/api/v1/nodes/ws`,
  `/proxy/services`, `/proxy/services/{service_id}/openapi.json`, and
  `/proxy/s/{slug}/{*path}`.

Validation for this ADR change:

- `bash tools/docs/lint.sh`
- `git diff --check`

## References

- Issue #2009
- ADR-0025: Voice Router Integration - Policy-Aware WebSocket Boundary
- ADR-0026: Tool-First Chat Ingress - Collapse Forward Actions to Model + Tools
- `docs/canon/nyxid-connected-service-tools.md`
