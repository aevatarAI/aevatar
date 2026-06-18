---
title: "Workflow External Approval Continuation Callbacks"
status: accepted
owner: workflow
---

# Workflow External Approval Continuation Callbacks

External approval callbacks are continuation callbacks for an active `wait_signal` step. They are not start-run webhooks and do not execute provider side effects directly.

## Contract

- A workflow registers an active external approval waiter by using typed `wait_signal.external_approval` options.
- The workflow run owns the waiter facts. Registration and clearing are committed as workflow events.
- Projection materializes the active continuation read model from committed register and clear events only.
- Callback lookup uses the active continuation read model by exact normalized `source_id + external_id_kind + external_id`.
- Callback handling does not query actor state, replay events, prime projection, or use actor request-reply queries.
- Before the active continuation is visible, the callback returns `425 Too Early` with `Retry-After` and does not admit replay or dispatch a signal.
- If continuation lookup or replay admission infrastructure is unavailable, the callback returns `503 Service Unavailable` with `Retry-After`.
- After lookup succeeds, replay admission uses `external-approval:{source_id}:{external_id_kind}:{external_id}` as the canonical identity and fingerprints the normalized terminal status.
- Same canonical identity plus same terminal status returns duplicate `202 Accepted` without redispatch.
- Same canonical identity plus a different terminal status returns `409 Conflict` without dispatch.
- First admission dispatches one `WorkflowSignalCommand` to the looked-up run, step, and signal identity.
- `202 Accepted` means the callback was admitted and accepted for signal dispatch, or was already admitted as a duplicate. It does not mean signal consumption, branch completion, side-effect completion, compensation settlement, or read-model observation.

## Terminal Payload

The signal payload carries typed external approval terminal fields:

- `source_id`
- `external_id_kind`
- `external_id`
- `instance_code`
- `request_id`
- normalized `terminal_status`: `APPROVED`, `REJECTED`, or `CANCELED`
- `callback_idempotency_key`
- provider delivery evidence

Commit or release remains normal workflow behavior after the signal is consumed. The callback endpoint must not execute those side effects.
