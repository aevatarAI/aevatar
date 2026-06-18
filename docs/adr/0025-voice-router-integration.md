---
title: Voice Router Integration - Policy-Aware WebSocket Boundary
status: Accepted
owner: eanzhao
---

# ADR-0025: Voice Router Integration - Policy-Aware WebSocket Boundary

> Superseded for target encoding by ADR-0026 and narrowed by issues #1321 and
> #674. Voice still resolves through `/ws/voice`; attach targets are expressed
> only by `ForwardToModel.tool_choice_hint.voice_attach_target`, not by tool
> prefilled argument bags. Narrowed again by issue #2158: Mainnet no longer
> maps `/ws/voice/{actorId}`.

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
- ADR-0026 supersedes the old voice wire action, and issue #1321 removes the
  temporary compatibility path that read `actor_id` or `voice_module_name` from
  `ForwardToModel.tool_choice_hint.prefilled_arguments`. Issue #674 restores
  ordinary `/ws/voice` attach by adding typed
  `ChatRouteToolChoiceHint.voice_attach_target { actor_id, voice_module_name }`.
  A `ForwardToModel` without that typed target remains model forwarding and
  still fails closed before WebSocket accept.
- Route policy and authorization remain separate. Ordinary `/ws/voice` no
  longer derives a target actor from tool prefill; it attaches only when the
  policy carries the typed voice attach target. Mainnet does not expose an
  actor-id path, so there is no second URL actor identity channel.
- `Aevatar.Foundation.VoicePresence.Hosting.MapVoicePresenceWebSocket` remains
  a low-level mapper for non-Mainnet or test hosts that provide their own
  admission and credential wrapper. It is not a Mainnet production ingress.

## Boundaries

- No `VoiceRouterGAgent`, `VoiceSessionRouterGAgent`, or extra actor hop is
  introduced.
- `Aevatar.Foundation.VoicePresence` must not reference ChatRouting. Routing is
  composed only in Host/Application boundary code.
- Raw audio frames, WebSocket connection identifiers, session IDs, and client
  connection metadata remain transient. They must not be written to actor state,
  event store, projection documents, or read models.
- Raw PCM has exactly one runtime path: the active transport lease's
  `IVoiceVolatileMediaStreamPort` volatile relay forwards media between the
  `IVoiceTransport` and `IRealtimeVoiceProvider` session. It must not be
  carried by actor-facing protobuf signals or packed into `EventEnvelope`.
- Voice control and transcript protobuf frames are not raw audio. They use the
  shared `IRealtimeSession` lifecycle and projection-backed realtime stream as
  `VoiceRealtimeFrame`; that frame must not carry PCM/audio bytes.
- Rejections that can be known before upgrade use HTTP status codes
  (`403`, `404`, `501`, `503`). WebSocket close `1008` is reserved for failures
  discovered after upgrade.
- This ADR does not solve #560 stream-session robustness concerns such as
  cross-host reconnect, seq/ack, or replay.

## Verification

- `/ws/voice` attaches to `voice_attach_target.actor_id` with optional
  `voice_attach_target.voice_module_name` when the resolved `ForwardToModel`
  carries that typed target.
- `/ws/voice` returns `501` before WebSocket accept for `ForwardToModel`
  decisions that do not carry a typed voice attach target, including decisions
  that carry only `tool_choice_hint.prefilled_arguments`.
- Mainnet route tables contain `/ws/voice` and do not contain
  `/ws/voice/{actorId}`.
- Static checks show no ChatRouting dependency inside
  `Aevatar.Foundation.VoicePresence`.
