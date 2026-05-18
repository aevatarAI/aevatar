---
title: LLM session readable actor id
status: accepted
owner: refactor/2026-05-18_zoneE-gagentservice
---

# Context

`LlmSessionGAgent` actors were allocated with `response-session-` plus a random GUID. The actor state and read model are keyed by `response_id`, but the actor id itself did not expose that stable business identity. Operators inspecting logs, projection leases, or runtime topology could not connect a session actor back to its response without reading the materialized document.

# Decision

Build LLM session actor ids deterministically from `response_id`:

```text
response-sessions/response:{percent_encoded_response_id}
```

The response id segment uses the same RFC 3986 unreserved subset as the Responses Agent Tool actor id scheme: ASCII alphanumerics plus `-_.` are left as-is; other UTF-8 bytes are encoded as `%HH`.

Actor ids are capped at 512 characters. When the encoded response id exceeds the cap, it is truncated at a percent-triplet boundary and receives a `~{16 lowercase hex chars}` SHA-256 tail derived from the normalized response id.

# Consequences

The actor id is now readable, deterministic, and aligned with the session read model key. Re-registering the same response targets the same actor, letting the actor's existing idempotency checks enforce that the stored session record matches the requested one.

Very long response ids remain bounded and deterministic, but the truncated actor id cannot fully round-trip to the original response id. The authoritative full `response_id` remains in actor state and the current-state read model.
