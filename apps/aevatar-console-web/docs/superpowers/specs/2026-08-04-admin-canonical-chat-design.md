# Admin Canonical Chat Design

## Goal

Migrate the existing `/chat` editing workbench from legacy Workflow Chat to the canonical NyxID Assistant surface used by `~/Code/nyxid-chat`, while preserving the current three-column workbench and existing AG-UI message presentation.

## Root cause

The page already posts to `/api/chat`, but its JSON body omits `type` and sends `sessionId` plus `workflow: "studio"`. Mainnet therefore classifies it as Workflow Chat. Canonical Assistant requests are discriminated typed commands and use server-owned conversation and turn identities.

## Authoritative data flow

1. A first user turn sends one `POST /api/chat` with `type: "text"`, a stable `clientRequestId`, and no `conversationId`.
2. `RUN_STARTED` supplies the authoritative `conversationId` and `turnId`.
3. Later text and control commands send that exact `conversationId`; no browser identity rule or route string derives another identity.
4. Conversation list, transcript, current state, and delete use `/api/chat/conversations/**` without a body or path `scopeId`. Authentication supplies the scope authority.
5. AG-UI continues to drive live text, reasoning, tool, and terminal rendering. Actor custom frames and current state drive task, control, input, approval, and action UI.

## Client boundaries

`chatApi.ts` owns canonical command serialization and SSE or accepted-response handling. It sets `Idempotency-Key` from `clientRequestId` and never sends `sessionId`, `workflow`, or `scopeId`.

`chatHistoryApi.ts` owns canonical conversation resources and strict response decoding. It exposes state freshness honestly: `current`, `not_modified`, `reload_required`, and `not_found`.

`chatActorState.ts` owns strict schema-v4 action validation, actor-frame reduction, and current-state decoding. It accepts only monotonic actor sequence and state version, keeps all identity domains distinct, and never derives control availability from status strings.

`nyxIdServiceApi.ts` is the browser-to-NyxID adapter. It reuses the existing NyxID OAuth session and configured authority to read `/api/v1/keys` and `/api/v1/catalog`, and to create a catalog key through `POST /api/v1/keys`. A created action resource is the response's top-level UserService `id`; `api_key_id` is never substituted.

`ChatActorControls.tsx` renders pending input, approval, actor-authored task controls, and `service.connect` action cards. The route page owns command dispatch and state refresh, keeping the component presentational.

## Browser action safety

- Only schema version 4 and action `service.connect` are executable.
- Exactly one of `catalogService` or `customService` is accepted.
- Custom endpoints must be absolute HTTPS URLs without userinfo, query, or fragment. Unknown fields, invalid identities, and secret-shaped keys fail closed.
- Full live action params are cached only in `sessionStorage` under `nyxid-chat:v4-action:{conversationId}:{actionRequestId}`. Current-state summaries are executable only when an exact cached frame matches all five identities.
- Opening NyxID first records the exact matching UserService ID baseline. A refresh reports `completed` only when exactly one new matching UserService ID exists. Zero or multiple candidates remain unresolved.
- Credentials exist only in the password input and the direct NyxID request, and are cleared when that request settles. They never enter Aevatar commands, state, cache, logs, or error text.
- Browser completion remains provisional until actor current state carries a matching verified postcondition or a matching postcondition step with `externalEffect: "confirmed"`.

## Controls and recovery

Input, approval, stop, steering, retry, and skip submit typed commands with the exact actor-owned identities and observed `stateVersion`. HTTP 202 is displayed as accepted, not committed. The page refreshes current state once and provides an explicit refresh action; it does not poll or invent completion.

Refreshing or reopening a conversation loads its transcript and actor current state. `reload_required` causes one uncursored state read. `not_modified` preserves the local projection, and `not_found` clears actor controls without deleting the transcript.

The existing confirmation-keyword heuristic and Workflow create-recovery polling are deleted. Pending input and approval come only from actor facts.

## UI and testing

The existing history rail, conversation pane, workflow target pane, message bubbles, and composer remain. Actor attention appears below the message list. Controls use accessible labels, show accepted or failed submission state, and remain disabled unless the actor explicitly authorizes them.

Focused adapter tests protect exact request bodies, canonical paths, strict state decoding, monotonic reduction, schema-v4 validation, secret rejection, and UserService identity selection. A route integration test proves that sending from the workbench reaches typed `/api/chat` and renders actor controls. Verification includes focused Jest files, TypeScript, affected-file Biome lint, build, frontend test-stability guard, documentation lint, and `git diff --check`.
