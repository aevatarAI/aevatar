---
title: "Channel Bot Callback Architecture — Lessons from Lark Integration"
status: accepted
owner: eanzhao
---

# ADR-0009: Channel Bot Callback Architecture

> Historical note: the `ChannelUserGAgent` continuation model discussed below has since been removed from the supported runtime path. Current production ingress is the Nyx relay contract described in ADR-0011 / ADR-0012.

## Context

Integrating Lark (飞书) bot with Aevatar required a webhook callback flow:
Lark event → HTTP endpoint → actor processing → LLM chat → reply via NyxID proxy → Lark API.

Multiple architectural issues were discovered during implementation (2026-04-11).

## Decisions & Lessons

### 1. Platform webhook response codes are strict

**Lark requires exactly HTTP 200.** Any other status code — including 202 Accepted — is treated as a push failure, triggering up to 5 retries.

**Rule:** Check each platform's webhook specification. Telegram accepts any 2xx; Lark requires exactly 200.

### 2. Never subscribe to another actor's stream within a grain turn

**Problem (legacy path):** `ChannelUserGAgent` subscribed to `NyxIdChatGAgent`'s Orleans stream and awaited `responseTcs.Task` in the same grain turn. Orleans delivers explicit stream subscription callbacks as turns on the subscribing grain. The grain was blocked awaiting the response, so the callback turn could never execute — **deadlock**.

**Rule (CLAUDE.md):** Cross-actor waiting must use the continuation pattern:
1. Send request → end current turn
2. Reply/timeout event awakens and continues in a new turn

**Current workaround:** Chat coordination (subscribe + wait + reply) runs in the HTTP/ThreadPool context, outside any grain. This matches the proven `NyxIdChatEndpoints` pattern. The grain only handles stateful identity tracking.

**Future:** Continuation pattern via stream forwarding + event handlers (see issue #175).

### 3. Long-running work must not block webhook responses

**Problem:** Lark requires 200 within 3 seconds. The full LLM chat pipeline takes 10-120 seconds. Awaiting it before returning 200 causes Lark to timeout and retry.

**Rule:** Split into two phases:
- **Phase 1 (sync, < 1s):** Identity tracking + dedup → return 200
- **Phase 2 (fire-and-forget):** Chat + LLM + reply → runs in background

### 4. Never pass request-scoped IServiceProvider to background tasks

**Problem:** `http.RequestServices` is scoped to the HTTP request. After returning 200, ASP.NET Core disposes the scope. Background tasks calling `services.GetService<>()` get `ObjectDisposedException` — silently swallowed since the task is unobserved.

**Rule:** Pre-resolve all needed singleton services into a concrete dependency object (e.g., `ChannelChatDeps`) while the request scope is alive. Pass resolved instances, never `IServiceProvider`.

### 5. Always check proxy API results — don't assume no-throw means success

**Problem:** `NyxIdApiClient.ProxyRequestAsync` returns error JSON (`{"error": true, ...}`) on 4xx/5xx instead of throwing. `LarkPlatformAdapter.SendReplyAsync` called it and logged "sent" without checking the result. The Lark API was returning error 230002 ("Bot not in chat") for every message — completely invisible.

**Rule:** For proxy/gateway API calls that don't throw on failure:
- Parse the response for error indicators
- Surface errors explicitly (throw, return result type, or log at warning level)
- `PlatformReplyDeliveryResult` pattern: return `(bool Succeeded, string Detail)` instead of void

### 6. Add diagnostic observability for fire-and-forget flows

**Problem:** Fire-and-forget tasks swallow exceptions. Without log access, failures were invisible.

**Solution:** expose a debug-only in-memory diagnostic sink behind `GET /api/channels/diagnostics/errors`. It records stage transitions (for example `Callback:accepted`, `Chat:start`, `Reply:error`) with bounded retention (last 50 entries / 1 hour). This sink is explicitly non-authoritative and exists only for operational debugging; no business flow depends on it and no PII is recorded.

## Known Limitations

**At-most-once delivery (legacy path):** If the process crashes after returning 200 but before the fire-and-forget task completes, the message is permanently lost. The durable fix was to persist the requested chat turn to durable actor state before returning 200, then process via event replay on restart.
