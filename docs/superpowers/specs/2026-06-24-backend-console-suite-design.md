# Backend Console Suite — Design Spec

- **Date:** 2026-06-24
- **Status:** Draft (awaiting user review)
- **Scope:** Assemble five standalone, backend-hosted console pages into one cohesive suite served by `https://aevatar-console-backend-api.aevatar.ai/`, sharing real login, shell/nav, design assets, and API wiring. Explicitly **decoupled from the Umi frontend** (`apps/aevatar-console-web`).

The five pages and their canonical paths:

| Path | Source today | State |
|---|---|---|
| `/workflow/observatory` | `~/Code/observatory.html` | visual mock |
| `/workflow/studio` | `~/Code/studio.html` | visual mock |
| `/schedules` | **missing** | to be created |
| `/channels` | `~/Code/channels.html` (+ `channels-built.html`) | visual mock |
| `/voice` | `~/Code/voice.html` | visual mock |

---

## 1. Context & current state

- `~/Code/*.html` already holds a same-design-language set: `observatory.html`, `studio.html`, `channels.html` (+ a built variant `channels-built.html`), `voice.html`, and `aevatar-map.html` (out of scope here). They are **framework-agnostic single-file pages** (semantic HTML + CSS variables + vanilla JS) and already share design tokens/components and partial cross-linking (e.g. `voice.html` links `href="/workflow/studio"`).
- They are currently **visual mocks**: `observatory.html` states the login is simulated and "视觉层不处理鉴权"; `studio.html`/`channels.html` hardcode `scopeId:"scope_alice_personal"`; data is fixture. `channels.html` hardcodes `HOST = "https://aevatar-console-backend-api.aevatar.ai"`.
- Backend serving: `Aevatar.Mainnet.Host.Api` uses `app.UseDefaultFiles(); app.UseStaticFiles();` (see `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs:348-349`) — static files from `wwwroot`, no explicit SPA catch-all found.
- Existing voice admin HTTP surface is minimal: only `POST /api/scopes/{scopeId}/gagent-actors/{actorId}/voice-presence/enable` (`src/Aevatar.Mainnet.Host.Api/Voice/VoicePresenceCapabilityAdminEndpoints.cs`). No list/get/session GETs yet.
- The Umi console (`apps/aevatar-console-web`) is a separate React/Umi SPA with NyxID OAuth (session in `localStorage` key `aevatar-console:nyxid:session`, `authFetch()` attaches Bearer, `AuthSessionBootstrap` guards routes). This suite must **reuse that login**, not merge into the SPA.

### Relationship to the larger effort
Making aevatar a generic voice service was decomposed into: ① `/voice` console (this suite member), ② backend WHIP/WebRTC ingress, ③ `vp-control` display side-channel, ④ tool/sink mapping + retire voice-presence. **This spec covers the console suite assembly only** (including `/voice` as a member). WHIP-dependent `/voice` panels stay honest stubs until ②③.

---

## 2. Goal & non-goals

**Goal:** five pages served from the backend host at their canonical paths, behaving as one console — shared real NyxID login (one login covers all), a shared header/nav to move between them, shared design assets (de-duplicated), and real API wiring (no mock data).

**Non-goals:**
- No integration into the Umi `aevatar-console-web` SPA.
- No `/voice` WHIP/display backend (sub-projects ②③④).
- No visual redesign — pages already match the shared design tokens.
- No new auth system — reuse the existing NyxID OAuth.

---

## 3. Decisions (set to recommended defaults — confirm or change on review)

1. **Source location:** ✅ Move page sources into the aevatar repo at `apps/console-pages/` (versioned, built, deployed with the backend). _(Alt: keep authoring in `~/Code` + a copy script — rejected: drifts, unversioned.)_
2. **Sharing mechanism:** ✅ Extract shared `assets/` (`shell.css`, `shell.js`, `auth.js`, `api.js`); each page references them. _(Alt: keep single-file pages, inline shared parts at build — rejected: duplication, drift.)_
3. **Login:** ✅ Reuse the Umi console's NyxID OAuth client + the **same `localStorage` session key** (`aevatar-console:nyxid:session`), so one login spans the SPA and the suite. _(Alt: independent login per suite — rejected: double login.)_
4. **`/schedules`:** ✅ Create a new page now, consistent with the suite shell/tokens. _(Alt: ship a placeholder — rejected: it is one of the five required paths.)_

---

## 4. Architecture

### 4.1 Source layout (in aevatar repo)
```
apps/console-pages/
  shared/
    shell.css        # design tokens + layout primitives (extracted, single source)
    shell.js         # top bar + cross-page nav (the 5 links + active state) + theme toggle
    auth.js          # NyxID OAuth (PKCE), shared session, redirect guard, logout
    api.js           # apiFetch() with Bearer; scope resolve (/api/studio/context)
    icons.js         # shared icon set
  observatory/index.html
  studio/index.html
  schedules/index.html      # new
  channels/index.html
  voice/index.html
  build.mjs          # produces wwwroot tree (see 4.4)
```
Each page keeps its own view logic but `<link>`/`<script>`s the shared assets instead of inlining tokens/auth/nav.

### 4.2 Serving (no new backend code required)
Build output lands under the backend's static root so existing middleware serves it:
```
wwwroot/workflow/observatory/index.html   -> /workflow/observatory
wwwroot/workflow/studio/index.html        -> /workflow/studio
wwwroot/schedules/index.html              -> /schedules
wwwroot/channels/index.html               -> /channels
wwwroot/voice/index.html                  -> /voice
wwwroot/assets/console/*                  -> shared assets
```
`UseDefaultFiles()` serves `index.html` per directory; `UseStaticFiles()` serves assets. **Risk to verify (impl):** confirm nothing else in `wwwroot` (e.g. the Umi SPA dist or a fallback) shadows these paths; if the Umi app and this suite are deployed to the same host, define a non-colliding `wwwroot` layout (these explicit dir paths take precedence over a default-document fallback, but confirm on the deployed host).

### 4.3 Shared auth (`auth.js`)
- Config comes from the Studio backend `/api/auth/nyxid/config`: OAuth authority, client id, and scope remain backend-owned. NyxID Consent owns the user's service grant, so this endpoint does not expose RFC 8707 resources and the browser does not send them. Only `NYXID_REDIRECT_URI` is an optional frontend callback override.
- On load: read session from `localStorage['aevatar-console:nyxid:session']`; if absent/expired and not restorable → `loginWithRedirect()` to NyxID, return via `/auth/callback` (the suite must serve or share a callback handler — reuse the SPA's `/auth/callback` if same host, else add a minimal callback page).
- `api.js#apiFetch` attaches `Authorization: Bearer <accessToken>`; on 401 → re-auth.
- Logout clears the shared key (affects SPA too — intended: single session).

### 4.4 Build & deploy
- `build.mjs`: copy each `<page>/index.html` + `shared/assets` into the `wwwroot` tree above; minify optional; this is the productionized form of today's `channels-built.html` convention.
- Deploy: ship the `wwwroot` tree with the backend host via the existing deploy path (`deploy-aevatar-app` / `deploy-eanzhao-sites` skill — confirm which targets `aevatar-console-backend-api.aevatar.ai`).

---

## 5. Per-page work (replace mock → real)

| Page | Remove (mock) | Wire (real) | Notes |
|---|---|---|---|
| observatory | simulated login, fixture runs | run/observatory APIs (`/api/workflow/observatory/*` — confirm), scope via `/api/studio/context` | platform-admin authorizer exists (MainnetHostBuilderExtensions ~L247) |
| studio | `scope_alice_personal`, fixture chat | studio/chat APIs (confirm), real scope | largest page |
| channels | fixture registrations | channels facade registrations (`POST /api/channels/registrations` per channels domain), real scope | `HOST` already set |
| voice | fixture agents/sessions | Approach A: `voice-presence/enable` (exists) + a few new read GETs (voice agents+defaults, live sessions, providers/modules); WHIP/Channels/Display/realtime panels = honest "待后端 ②③" disabled states | no fake data |
| schedules | — (new) | `/api/schedules` (list/preview/enable/disable/run-now) | build new page from shell |

Exact endpoint names to be confirmed against the backend during implementation; this spec fixes the pattern (shared `apiFetch`, scope resolve, no fixtures).

---

## 6. States & error handling
Per page, via shared helpers: loading, empty, error (surface backend error detail), `401 → login`, `503 voice_not_configured` (voice enable) → guidance, and the `/voice` host-gated publish action shown disabled with explanation. The "待后端 ②③" stubs use a consistent banner+badge.

---

## 7. Implementation order (for a fresh session)
1. Scaffold `apps/console-pages/` + extract `shared/` (shell.css, shell.js, auth.js, api.js, icons.js) from one page (voice or channels).
2. Implement real NyxID auth in `auth.js`; verify login round-trip on ONE page against the backend (shared session with the SPA).
3. Convert pages to use shared assets + remove mocks + wire real endpoints, in order: observatory → studio → channels → voice → schedules (new).
4. `build.mjs` → `wwwroot` tree; deploy to the backend host.
5. Smoke test each path on the deployed host: login once, navigate across all five, verify real data + auth.

---

## 8. Open questions / risks
- Exact existing API endpoints per page (observatory/studio/schedules) — confirm against backend in impl.
- `wwwroot` coexistence with the Umi SPA on the deployed host (path precedence / fallback).
- NyxID OAuth client: confirm the backend-provisioned client allows these redirect URIs / this origin.
- `/auth/callback` ownership when suite + SPA share a host.
- `/voice` realtime/WHIP/display panels remain stubbed until sub-projects ②③.

---

## 9. Testing
- Build of the page bundle must succeed; basic HTML/JS lint.
- Auth round-trip test (login → token in shared key → `apiFetch` 200).
- Per-page manual smoke against the backend (real data renders; no fixtures; cross-nav works; one login spans all).
