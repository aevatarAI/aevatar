# PRD — Delete registration also deprovisions the NyxID channel-bot

## Problem
`DELETE /api/channels/registrations/{id}` (the "删除接入" button on `/channels` → manage)
only tombstones the **local** registration mirror. Verified in source:
`HandleDeleteRegistrationAsync` → `ChannelRegistrationCommandFacade.UnregisterAsync`
→ dispatches `ChannelBotUnregisterCommand` → `ChannelBotRegistrationGAgent.HandleUnregister`,
which persists a `ChannelBotUnregisteredEvent` (tombstone) and makes **no NyxID call**.

The register path, by contrast, provisions on NyxID (relay api-key → channel-bot → route)
via `Nyx*ProvisioningService`, with `DeleteChannelBotAsync` already used as a provision
rollback (`NyxLarkProvisioningService.cs:208`). Delete has no symmetric teardown.

## Impact
- The NyxID **channel-bot + relay api-key + conversation route are orphaned** (persist on NyxID).
- NyxID enforces one *active* channel-bot per `app_id` (Lark) globally → re-registering the
  same app 502s with `nyx_status=409 already registered`. The UI's "删旧重建" can't fix it
  either (it also only calls the local DELETE). Cleanup currently requires manual
  `nyxid channel-bot delete <id>`.
- A still-alive orphaned bot keeps relaying inbound, but the tombstoned mirror no longer
  resolves its scope → 401 (silent half-dead state).

## Goal
Make registration deletion also tear down the NyxID side, so deleting a bot from the UI
leaves no orphaned NyxID resources and lets the same app be re-registered cleanly.

## Scope / decisions (defaults chosen — `补一下`, no further ask)
- **D1 — what to delete:** all three NyxID resources for the registration —
  conversation route, channel-bot, relay api-key (reverse of creation order). Client
  methods all exist (`DeleteConversationRouteAsync`, `DeleteChannelBotAsync`, `DeleteApiKeyAsync`).
- **D2 — failure semantics (honest, retryable):** NyxID teardown runs **before** the local
  tombstone. A **404 / not-found = already gone = success** (idempotent). The **channel-bot**
  delete is authoritative: if it hard-fails (non-404 error), return an error and **do NOT**
  tombstone the local mirror — the caller can retry, state stays consistent. Route + api-key
  deletes are **best-effort** (log a warning on residual failure but still proceed once the
  channel-bot is gone, since the local tombstone removes the mirror anyway).
- **D3 — platforms:** deprovision is **platform-neutral** (delete by NyxID id, no per-platform
  branching) → covers both Lark and Telegram (R5).
- **D4 — auth/token:** use the caller's bearer (`ResolveBearerAccessToken(http)`), same as the
  register path. NyxID channel-bot delete is owner-scoped. A platform admin deleting a
  *foreign* owner's registration cannot delete that owner's NyxID bot (NyxID owner-scoped) —
  document as a known limitation; the local tombstone still applies. (Consistent with the
  existing `foreign` status handling.)

## Out of scope
- No NyxID repo changes (only existing `NyxIdApiClient` REST surfaces — CLAUDE.md 外部仓库无改动权).
- No UI change required (the existing "删除接入" / "删旧重建" buttons keep working; behavior
  improves underneath). Optional: surface a residual-cleanup warning in the delete response.
- Backfill / bulk cleanup of already-orphaned bots is not in scope.

## Acceptance Criteria
- [x] `DELETE /api/channels/registrations/{id}` for an owned registration calls NyxID to delete
  the conversation route, channel-bot, and relay api-key (by the entry's stored ids), then
  tombstones the local mirror — verified by a unit test asserting the deprovision is invoked
  with the right ids before unregister.
- [x] A NyxID **404** on any resource is treated as success (idempotent re-delete / already gone).
- [x] A **hard channel-bot delete failure** returns a non-2xx and leaves the local registration
  **not** tombstoned (test).
- [x] Route + api-key residual failures do **not** block the local tombstone once the channel-bot
  is gone, and are reported as warnings (test).
- [x] Works for both `lark` and `telegram` registrations (platform-neutral path).
- [x] `dotnet build` of the channels projects = 0 errors; channel tests pass;
  `bash tools/ci/architecture_guards.sh` + `bash tools/ci/test_stability_guards.sh` green.
