---
title: "Agent Profile Owner Switch Performance"
date: 2026-08-01
status: approved
---

# Agent Profile Owner Switch Performance

## Goal

Make switching between `我的 Profile` and `system/` feel immediate without weakening the actor-backed read-model semantics or changing any API contract.

## Root Cause

The current client discards the active owner view, then serially waits for the owner list, personal binding, Admin system binding, and first Profile detail before rendering. Production evidence shows the individual server handlers are normally fast, while four authenticated proxy round trips make the combined switch take roughly two seconds. Switching back repeats the same waterfall.

## Design

Keep one browser-local snapshot per owner. A snapshot contains only already-rendered query results: list items, selected slug, selected detail, ETag, and owner-relevant bindings. It is a display cache, never an authority source, and is replaced by every successful background read.

When the owner changes:

1. Save the current owner's completed snapshot.
2. Restore the target owner's snapshot synchronously when one exists; otherwise show its loading state.
3. Render the target owner immediately.
4. Start a background refresh. Fetch the list and binding reads concurrently.
5. Render the refreshed list as soon as it is available, then fetch the selected Profile detail without blocking the binding reads.
6. Ignore every response whose request generation or owner no longer matches the active view.

The target owner remains visible while refreshing. Existing mutation polling continues to force authoritative reads and update the active owner's snapshot. Failed background refreshes keep a usable cached view and display the existing error state; a first-load failure keeps the existing empty/error behavior.

## Boundaries

- Change only the embedded Backend Console asset and its focused behavior test.
- Add no dependency, API, server cache, new state service, or persistence.
- Do not cache dirty drafts, pending mutations, modal state, or skill proofs across owners.
- Preserve ETag, idempotency, accepted-receipt polling, authorization, and explicit binding semantics.

## Acceptance

- Returning to an already visited owner renders its completed snapshot before network reads settle.
- List and binding reads begin in the same turn instead of serially.
- The selected detail starts after the refreshed list chooses a valid slug; it does not wait for bindings.
- A stale request cannot overwrite the owner selected after it.
- Focused Capabilities tests, test stability guard, docs lint, architecture guards, build, and full tests pass before push.
