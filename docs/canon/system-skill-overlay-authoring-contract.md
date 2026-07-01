---
title: "System Skill Overlay Authoring Contract"
status: active
owner: eanzhao
---

# System Skill Overlay Authoring Contract

This document records the authoring, sourcing, and rollout contract for the system
skill overlay. The runtime is implemented by `OrnnSystemSkillOverlayProvider`
(source + cache) and `SystemSkillOverlayDefaultProvider` (built-in fallback); this
page is the governance reference for authors, operators, and review.

## Source: a public, org-owned Ornn skillset

The overlay is sourced from a **public, org-owned Ornn skillset** whose non-secret
name is host configuration (`Aevatar:SystemSkills:SetName`, e.g. `aevatar-system`).
There is **no organization service token secret** (issue #2498):

- **Trust anchor is set membership.** Only the owning org can add members, so members
  can be public without a tag-squat injection vector — the overlay reads by *set
  membership*, not a squattable tag.
- **No secret.** Public read reuses aevatar's existing `ornn-api` NyxID proxy access
  with the per-turn user token; the only host fact is the non-secret set name.
- **Anti-squat.** The set name is resolved to a stable `guid` once and then read by
  that guid. On a pinned-guid miss the provider keeps last-known-good and never
  silently re-resolves by name.

## Authoring an overlay member

A member is a normal Ornn skill added to the set. Two things make it an overlay member:

| Aspect | Contract |
|---|---|
| Set membership | The skill is a member of the org-owned set (the trust anchor). |
| Scope tag | The skill carries exactly one platform-scope tag: `overlay-scope-global` (cross-platform, always injected) or `overlay-scope-<platform>` (e.g. `overlay-scope-lark`), injected only when the turn's channel platform matches. |
| Body | The overlay content is the skill's `SKILL.md` body. Any leading YAML frontmatter (name/description/version/metadata) is stripped and never enters the prompt. |

A member **without** an `overlay-scope-*` tag is skipped (not injected) — scoping is
fail-closed, so a mistagged member never bleeds into the wrong platform.

## Context-aware injection and budget

The provider pre-renders one overlay variant per platform seen in the set:

- `overlay-scope-global` members are included in **every** variant.
- `overlay-scope-<platform>` members are included **only** in that platform's variant.
- Direct chat is inherently a `dm` turn → it resolves the global-only variant.

Each variant is rendered within `MaxSkills`/`MaxBytes` (full bodies first, degrading
to catalog lines, then a catalog-only block). A deterministic `source_watermark`
(SHA-256 over every member's name, description, body, and scope) is the operational
provenance handle for a turn; it changes whenever any member is edited or retagged.

## Injection seams and the built-in default

The kernel (`system-prompt.md`) carries only invariants, runtime read contracts, the
skill extension mechanism, a one-line internal tool index, and the cross-platform
grant-before-link principle. Per-domain capability how-to lives in the overlay and is
force-injected on **both** reply seams from the **same host-level source**
(`ISystemSkillOverlayProvider`):

- Direct chat: `RoleGAgent.DecorateSystemPrompt` resolves `GetCurrent` for a `dm` turn.
- Channel/relay: `NyxIdConversationReplyGenerator.BuildSystemPrompt` resolves
  `GetCurrent` for the turn's `channel.platform`, after the kernel and before the
  channel runtime facts (`kernel > overlay > runtime facts`).

`GetCurrent(request)` is a **synchronous cached read** — never a query-time fetch.
Staleness triggers a single-flight, out-of-band background refresh using the per-turn
token supplied by the seam; the token is used only to read the public set and is never
persisted or logged. When the set is unreachable or empty, the provider degrades to the
built-in default overlay (`SystemSkillOverlayDefaultProvider`, exposed as
`ISystemSkillOverlayFallback`) — the no-regression floor (coarse, platform-agnostic).

These invariants are enforced by `check_system_skill_overlay_dual_seam_injection`
(both seams inject via a real provider + DI registration),
`check_system_skill_overlay_set_source` (non-secret `SetName`, no `OrgServiceToken`,
skillset source, synchronous `GetCurrent`), and
`check_system_skill_overlay_eval_gate_present`.

## Rollout Policy

System skill overlays roll out in stages:

1. Staging: validate prompt ordering, per-platform scoping, and eval golden-task
   results against staging hosts.
2. Canary: enable one selected agent or channel path and inspect sampled overlay
   watermark logs for unexpected prompt growth or missing injection.
3. Fleet: expand only after staging and canary have clean eval, CI, and observability
   signals.

Instant fleet rollout is forbidden. Rollback disables the host configuration
(`Aevatar:SystemSkills:Enabled`) or removes the offending member from the set; do not
patch around the sourcing contract in runtime code.
