---
title: Voice Router Integration - Policy-Aware WebSocket Boundary
status: Accepted
owner: eanzhao
---

# ADR-0025: Voice Router Integration - Policy-Aware WebSocket Boundary

> Superseded for target encoding by ADR-0026. Voice still resolves through
> `/ws/voice`, but the policy target is now `ForwardToModel` with
> `tool_set_ref = voice.realtime` and `tool_choice_hint =
> aevatar_invoke_gagent`, not a `ForwardToGAgent` wire action.

## Context

Issue #674 extends the chat route policy from ADR-0024 to voice. Voice in this
repository is not a dedicated `VoiceSessionGAgent` or `VoiceRouterGAgent`; it is
a `VoicePresence` EventModule capability attached to an existing actor. The
router decision therefore chooses which voice-enabled actor to attach to, while
`Aevatar.Foundation.VoicePresence` continues to own transport, session attach,
and provider state machines.

## Decision

- Normal client traffic uses `GET /ws/voice` without an `actorId`.
- The Host boundary builds a typed `ChatRouteInput` with
  `source_kind = VOICE`, `VoiceCodec`, `VoiceConversationMode`, `VadMode`, and
  optional `voice_module_name`.
- The Host boundary reads `ChatRoutePolicyCurrentStateDocument` through
  `IChatRoutePolicyQueryPort`, then calls the stateless `ChatRouteResolver`
  before WebSocket upgrade.
- ADR-0026 supersedes the old voice wire action. Current voice policy accepts
  `ForwardToModel` only when it carries
  `tool_set_ref = voice.realtime` and
  `tool_choice_hint.tool_name = aevatar_invoke_gagent`, with the actor target
  provided as typed prefilled arguments. Plain model voice routing and the
  `VoiceSessionActor` Stage 5 topology are not implemented in this milestone;
  unsupported `ForwardToModel` voice routes still fail before upgrade.
- Route policy and authorization remain separate. After policy resolves a
  target actor, the caller must still pass `IUserAgentCatalogQueryPort` attach
  permission.
- Explicit actorId bypass stays at `GET /ws/voice/{actorId}`, but Mainnet Host
  gates it with the `voice-dev` authorization policy (`voice:bypass` scope or
  admin/owner role).

## Boundaries

- No `VoiceRouterGAgent`, `VoiceSessionRouterGAgent`, or extra actor hop is
  introduced.
- `Aevatar.Foundation.VoicePresence` must not reference ChatRouting. Routing is
  composed only in Host/Application boundary code.
- Raw audio frames, WebSocket connection identifiers, session IDs, and client
  connection metadata remain transient. They must not be written to actor state,
  event store, projection documents, or read models.
- Rejections that can be known before upgrade use HTTP status codes
  (`403`, `404`, `501`, `503`). WebSocket close `1008` is reserved for failures
  discovered after upgrade.
- This ADR does not solve #560 stream-session robustness concerns such as
  cross-host reconnect, seq/ack, or replay.

## Verification

- `/ws/voice` default policy routes to a voice-enabled actor and attaches the
  selected module.
- A voice-specific rule such as `source=VOICE + channel=lark` overrides the
  default target.
- `/ws/voice/{actorId}` rejects callers without `voice:bypass` or admin/owner.
- Static checks show no ChatRouting dependency inside
  `Aevatar.Foundation.VoicePresence`.
