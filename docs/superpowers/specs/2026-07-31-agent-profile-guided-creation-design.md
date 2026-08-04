---
title: "Agent Profile Guided Creation"
date: 2026-07-31
status: approved
---

# Agent Profile Guided Creation

## Goal

Make personal and `system/` Agent Profiles usable without carrying a complete configuration file across restarts. Issue #3077 already makes definitions actor-owned and queryable through read models. This change closes the remaining product gap: the Web API exposes enough exact Ornn evidence for a valid draft, and the Admin Console guides users from intent to a reviewable Profile.

The canonical Admin route remains `#/agent-profiles`; `#/agentProfiles` is not restored.

## Ownership And Authorization

- `mine/` Profiles belong to the authenticated Aevatar scope and use `/api/scopes/{scopeId}/agent-profiles`.
- `system/` Profiles are shared. Everyone may read published summaries; only an Aevatar Admin may mutate them through `/api/admin/agent-profiles`.

The browser obtains `scopeId` from authenticated server context and never derives it from a NyxID subject or UUID shape. Server authorization remains authoritative. `profileSlug`, Ornn skill GUID, and runtime `agentKind` remain separate identities.

## Exact Skill API

The existing `GET /api/workflow/skills/{guid}/exact?literalVersion={major.minor}` response gains typed `declaredToolNames: string[]`. The query reads exact detail and exact skill JSON for the same GUID/version, requires matching identities plus publisher/hash evidence, and returns normalized declared tool names: trimmed, non-empty, ordinally deduplicated and sorted. It does not expose raw skill JSON or an open metadata bag.

List summaries still do not imply publisher authority. A candidate becomes selected only after the exact endpoint returns publisher, version, hash, and declared tools.

## Selected UX

Keep the existing list/editor workspace. Replace the toolbar slug field with one `新建 Profile` action that opens a local creation mode on the right; opening it performs no mutation.

1. **定义职责** — owner, display name, editable suggested slug, purpose, and instructions.
2. **选择能力** — search visible Ornn skills and add one exact skill per intent. Show name, publisher, version, shortened hash, routing description, aliases, and side-effect class.
3. **检查并创建** — summarize owner, activation, members, declared tools, and impact. One `创建草稿` action runs the existing asynchronous resource steps.

The active owner tab determines ownership. `system/` creation is shown only to an authenticated Admin.

## Tool Policy Assistance

Selecting an exact skill unions its `declaredToolNames` into the member task policy and Profile maximum policy while preserving existing entries. Replacing a skill or deleting a member does not silently remove maximum-policy entries because the draft cannot identify which entries were manual. The server sealer remains final authority.

Normal flow renders tools as chips. Manual tool/tool-set policy, recovery policy, and fixed runtime parameters move under native `details`; fixed `nyxid.chat` parameters remain read-only and server-defined.

## Mutation And Failure Semantics

`创建草稿` keeps the existing honest sequence:

1. POST the slug with `Idempotency-Key`.
2. Treat 202 only as accepted; poll the canonical list to a terminal catalog outcome.
3. Read detail and ETag.
4. PUT the complete draft with `If-Match` and a new idempotency key.
5. Poll canonical state to a terminal draft outcome, then open the ordinary editor.

The UI shows `创建资源`, `等待投影`, and `保存草稿`, and disables duplicates. Ambiguous mutation failure is read back before another mutation. If the shell exists but draft save fails, select that shell and retain the assembled draft for explicit save. Validation and publication stay separate. Publication is complete only when `PROFILE_PUBLISHED` and `executionAvailable == true` are visible.

Creating or publishing never changes the caller's `nyxid.chat` default binding; binding remains explicit.

### NyxID-safe mutation transport

Production API verification and automation use `nyxid proxy request aevatar`. NyxID currently forwards neither the standard `Idempotency-Key` nor `If-Match` request header, so Agent Profile mutations also accept the same values as typed body fields without weakening the Actor contract:

- `idempotencyKey` is optional in the create, draft, publish, set-binding, and clear-binding request bodies. The standard header remains supported and preferred for direct/browser clients.
- `expectedVersion` is an optional non-negative authority-state version in draft, publish, set-binding, and clear-binding request bodies. The standard strong `If-Match` header remains supported and preferred for direct/browser clients.
- At least one representation is required. If header and body are both supplied, their normalized values must agree exactly. Missing values retain the existing `400`/`428` behavior; malformed, negative, conflicting, or stale values are rejected before Actor dispatch.
- The body fallback is a Host transport adaptation only. Application requests and Actor commands still receive one resolved idempotency key and one resolved expected authority version.

## Editor And Accessibility

The normal editor uses the same hierarchy: primary identity and status, compact exact-member cards, then collapsed advanced policy/runtime controls. Save/validate/publish share a stable action area; system rollout appears only for editable `system/`. Status uses text plus color and `aria-live`; controls are keyboard reachable. At the existing mobile breakpoint, list/workspace stack and evidence/actions wrap. Reuse existing Console tokens and add no UI dependency.

## Production Acceptance

After deployment, use signed-in `nyxid proxy request aevatar` only to create personal `aevatar-operator` (`Aevatar Operator`, `nyxid.chat`, `ENFORCED`). Read one fixed `aevatar-platform` skillset revision and its closure, then resolve every closure member by its exact GUID and literal version. Require the closure and exact API to agree on GUID, name, version, and hash; persist the stable publisher ID returned by the exact API. The skillset name is not a publisher identity. Instructions require explicit confirmation before destructive, authorization-changing, or externally visible operations and treat typed receipts/read models—not prose or 202—as completion evidence.

Create, save, validate, publish, and read back each terminal outcome. Do not bind as personal default. Browser acceptance uses canonical `#/agent-profiles` for desktop/mobile, keyboard, pending, errors, and published state; browser credentials never perform API mutations.

## Verification

Focused tests prove exact normalized tools and identity mismatch handling; tool-policy union; local creation before mutation; canonical terminal polling and draft preservation; existing multi-member/system/ETag/publish semantics; canonical route, responsiveness, keyboard reachability, and honest states. Run focused Capabilities/Ornn tests, test-stability and relevant architecture/projection guards, docs lint, full build, and full tests before push.

## Non-Goals

- No composite backend mutation or second state machine.
- No global NyxID proxy-header workaround and no reuse of unrelated `X-Request-Id`/`X-Correlation-Id` semantics.
- No publisher guessing or raw Ornn package exposure.
- No old hash-route compatibility entry.
- No auto-publish, auto-bind, runtime hot update, or redesign of #3077 actor/projection architecture.
