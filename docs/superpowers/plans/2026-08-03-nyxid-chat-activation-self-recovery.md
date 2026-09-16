# NyxID Chat Activation Recovery Implementation Plan

**Goal:** Remove the activation-time self-wait while preserving every NyxID Chat
recovery, history, API, observation, identity, and security feature.

**Final architecture:** Keep the shared Orleans self-dispatch guard, use durable
callbacks to sequence Conversation activation recovery, and use the existing
actor self-publication helpers for current-actor continuations. External actor
delivery remains on `IActorDispatchPort`.

## Completed

- [x] Reproduce `/api/chat` stalling and trace it to activation-time
  `DispatchAsync(Id, ...)` waiting on `IsInitializedAsync(self)`.
- [x] Identify `1458a5bdbda5aefd17f8b5a0c43efee1618970f1` as the introducing
  commit and preserve its postcondition, interrupted-operation, uncertainty,
  delivery-loss, version-check, and secret-isolation behavior.
- [x] Retain the shared Orleans self-dispatch guard from `f5153c900`.
- [x] Retain the durable callback and history-reservation sequencing from
  `d707b0c0f` and `d16945419`.
- [x] Route remaining NyxID current-actor continuations through existing
  `PublishAsync` or `SendToAsync` helpers; keep cross-actor delivery on the
  dispatch port.
- [x] Allow the pending-input handler to consume its direct self message.
- [x] Preserve correlation and stable delivery operation IDs on Conversation
  activation callbacks.
- [x] Add unit and real-Orleans regression coverage without polling or arbitrary
  `Task.Delay`.

## Final Gates

- [ ] Run focused Conversation, Turn, recovery/security, and real Orleans tests.
- [ ] Run the NyxID semantics, test-stability, and architecture guards.
- [ ] Run the full solution build and test suite; classify only independently
  reproduced environment or unrelated baseline failures as pre-existing.
- [ ] Fetch and rebase onto the latest `origin/feature/integrate`; rerun every
  gate affected by overlapping upstream changes.
- [ ] Push with a normal fast-forward update and read back the remote SHA.
- [ ] Invoke production `/api/chat` through `nyxid proxy request aevatar`, then
  correlate the unique request in read-only production logs. If deployment has
  not yet picked up the pushed commit, report that boundary instead of claiming
  a deployed fix.

## Publish Rule

Never force-push. Any movement of `origin/feature/integrate` requires another
fetch/rebase and overlap-aware verification before publishing.
