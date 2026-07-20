---
title: "NyxIdChat API Identity And Streaming Contract"
status: active
owner: eanzhao
---

# NyxIdChat API Identity And Streaming Contract

NyxIdChat uses one durable conversation actor and a distinct server-owned identity for every submitted turn. Clients must not reuse a conversation-level value as the RoleGAgent replay key.

## Identity model

| Identity | Owner and lifetime | Purpose |
|---|---|---|
| `actorId` | Server-created, conversation lifetime | Durable conversation identity and actor address. Reuse it for every turn in one conversation. |
| `turnId` | Server-created, one user submission or approval continuation | RoleGAgent replay identity, projection session identity, message identity prefix, and SSE run identity for that turn. |
| `clientRequestId` | Optional caller-created transport retry identity | Lets the server derive the same actor-scoped `turnId` for an identical retry. It is not an actor or turn identity. |
| `commandId` | Command pipeline, one dispatch | Tracks dispatch admission. It is independent from `actorId` and `turnId`. |
| `correlationId` | Command pipeline, one trace chain | Correlates transport and observation. It is independent from all resource identities. |
| approval `requestId` | RoleGAgent pending approval state | Selects the pending approval continuation. It is never replaced by `turnId`. |

Internally, `ChatRequestEvent.SessionId` carries `turnId` because RoleGAgent owns that existing protobuf field as its per-turn replay key. This internal field does not make `sessionId` a public conversation identity.

## Submit a turn

```http
POST /api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:stream
Authorization: Bearer <nyxid-access-token>
Content-Type: application/json
Idempotency-Key: client-request-42
```

```json
{
  "prompt": "Summarize the connected repository",
  "clientRequestId": "client-request-42"
}
```

`clientRequestId` is optional and can be sent in the JSON body or as `Idempotency-Key`. The body field takes precedence. Without it, every accepted submission receives a fresh random `turnId`. With it, the server derives a stable `turnId` from `actorId + clientRequestId`:

- same actor, same key, same input: RoleGAgent replays the committed result without executing the LLM again;
- same actor, same key, different prompt or input parts: the stream ends with `RUN_ERROR`, code `IDEMPOTENCY_CONFLICT`;
- same key under a different actor: a different `turnId` is derived.

The legacy JSON field `sessionId` is deprecated and ignored. It must not be used for retry idempotency and never controls the internal turn identity.

## SSE terminal contract

The first stream frame exposes both identities:

```json
{
  "type": "RUN_STARTED",
  "actorId": "nyxid-chat-actor-1",
  "turnId": "turn-...",
  "runStarted": {
    "threadId": "nyxid-chat-actor-1",
    "runId": "turn-..."
  }
}
```

Every started stream ends with exactly one typed terminal frame:

- success: `RUN_FINISHED` with `runFinished.runId = turnId` and `status = completed`;
- authorization blocker: `nyxid.authorization.required`, then `RUN_FINISHED` with the same `turnId` and `status = blocked`;
- dispatch, actor, projection, idempotency, or streaming failure: `RUN_ERROR` with the same `turnId`, a stable code, and a safe message.

Heartbeat output stops before a terminal frame is written and remains stopped after failure, completion, or cancellation. Client-facing frames never include raw internal exceptions, upstream error bodies, access tokens, credentials, or URI query/fragment secrets.
Heartbeat payloads expose `actorId` and `turnId`; they do not expose the deprecated `sessionId` field.
If the projection never produces a terminal fact, the server closes the stream with a safe `RUN_ERROR` after its bounded terminal deadline instead of leaving a keepalive-only connection open indefinitely.

## Authorization required

NyxID proxy responses are classified only from the structured HTTP status, `error`, and `error_code` contract. Only the exact invalid-credential tuple HTTP `401` + `unauthorized` + `1001` is a proxy authorization failure. Aevatar maps that confirmed failure to `NyxIdAuthorizationRequiredEvent` and emits:

```json
{
  "type": "CUSTOM",
  "custom": {
    "name": "nyxid.authorization.required",
    "payload": {
      "serviceSlug": "api-github",
      "serviceLabel": "GitHub",
      "resourceUri": "/repos/private",
      "reasonCode": "NYXID_UNAUTHORIZED",
      "safeMessage": "Connect or reauthorize api-github to continue."
    }
  }
}
```

HTTP `403` / `forbidden` / `1002` is not sufficient evidence that reconnecting can resolve the failure. Approval-policy denial, approval timeout, scoped permission denial, and ordinary upstream `403` responses remain safe typed tool failures and do not emit `nyxid.authorization.required`.

If the required service is absent from connected services and no operation tool exists, the model must call `nyxid_require_service`. That positive Aevatar-owned discovery result produces the typed connection blocker without fabricating a tool call to a missing service. Classification never inspects unstable human-readable messages. Failed, denied, or blocked calls retain only safe typed results: their raw proxy error bodies and secret-bearing arguments do not enter receipts, actor state, history, SSE frames, or logs, and resource URIs omit query/fragment values.

Authorization blocks only the current turn. It does not deactivate the conversation actor, create pending tool approval, or schedule automatic replay. After connecting the service, submit a new turn on the same `actorId` to retry the request explicitly.

## Approval continuation

```http
POST /api/scopes/{scopeId}/nyxid-chat/conversations/{actorId}:approve
Authorization: Bearer <nyxid-access-token>
Content-Type: application/json
```

```json
{
  "requestId": "pending-approval-1",
  "approved": true,
  "reason": "approved"
}
```

`requestId` must match the actor-owned `PendingToolApprovalState`. The server creates a new continuation `turnId` for observation and dispatch. Approval does not reuse the original failed turn and does not use a caller-supplied turn identity. The legacy approval `sessionId` field is deprecated and ignored.

`:approve` is valid only for a real pending approval. An unknown or stale `requestId` produces a typed `RUN_ERROR` with code `APPROVAL_REQUEST_NOT_PENDING` for the new continuation turn and does not modify an unrelated pending request. Authorization-required blockers are not approvals and cannot be continued through this endpoint.

## Conversation history

All turns sent to the same `actorId` share the actor's conversation history, including after actor passivation and reactivation. The runtime transcript is rebuilt from committed per-turn session facts rather than process memory. Each archived user and assistant message carries its typed `turnId`, and its message ID is derived from that turn. A blocked turn is archived with terminal status `blocked` and a safe blocker summary. It remains part of the conversation transcript and does not prevent the next turn.

RoleGAgent assigns the terminal timestamp once when it commits the authoritative completion and persists that typed value with the session state. A completed-session replay republishes the same timestamp, so a retry with the same `actorId + clientRequestId` still receives terminal SSE output while the history actor sees an identical duplicate append. The provider and tools are not re-executed, message counts do not change, and no history conflict is committed. A new `clientRequestId` creates a normal continuation turn over the existing conversation history.

## Caller migration

Callers that currently keep one `sessionId` for the whole conversation must:

1. keep and reuse only `actorId` as conversation identity;
2. stop sending `sessionId` for new turns and approvals;
3. optionally send a unique `clientRequestId` only when transport retry replay is required;
4. read `turnId` from `RUN_STARTED` and use terminal `runId` only to correlate that turn;
5. retain approval `requestId` from `TOOL_APPROVAL_REQUEST` and return it unchanged to `:approve`.

No migration is required for `commandId` or `correlationId`; their semantics are unchanged.
