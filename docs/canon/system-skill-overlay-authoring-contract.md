---
title: "System Skill Overlay Authoring Contract"
status: active
owner: eanzhao
---

# System Skill Overlay Authoring Contract

This document records the authoring, sourcing, composition, and rollout contract for
system prompt skill content. `BuiltInPromptFloorProvider` owns the mandatory built-in
floor. `OrnnSystemSkillOverlayProvider` owns only the optional remote global layer.
`SystemPromptLayerComposer` is the single composition authority used by both reply
paths. This page is the governance reference for authors, operators, and review.

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
- Direct chat is inherently a `dm` turn → it resolves the `dm` platform like any other
  (an `overlay-scope-dm` member targets direct chat); with no `dm`-scoped members that
  is exactly the global-only variant.
- Channel platform resolution is **typed-context first**: the seam reads
  `AgentToolExecutionContext.Channel.Platform` and only falls back to the
  `channel.platform` metadata key, because the per-step plan path strips owned control
  keys from metadata before prompt construction.

Each variant is rendered within `MaxSkills`/`MaxBytes` (full bodies first, degrading
to catalog lines, then a catalog-only block). The provider returns a typed
`GlobalSystemSkillPromptLayer` with that byte bound and a deterministic
`source_watermark` provenance value (SHA-256 over every member's name, description,
body, and scope). The watermark changes whenever a member is edited or retagged.

## Fixed prompt composition

`SystemPromptLayerComposer` accepts exactly seven named typed layers and writes them
in this fixed order:

1. `KernelPromptLayer`
2. `BuiltInPromptFloorLayer`
3. `GlobalSystemSkillPromptLayer`
4. `ProfileRoutingPromptLayer`
5. `SelectedSkillPromptLayer`
6. `RuntimeFactsPromptLayer`
7. `ConversationContextPromptLayer`

Kernel and built-in floor are mandatory. Missing, blank, or over-budget content in
either layer fails composition. The five remaining layers are optional; an
over-budget optional layer rejects only its own complete slot and cannot truncate,
replace, or remove another slot. Selected skill procedure content is delimited by
`<selected-skill-procedure>`. Runtime facts and conversation summaries use separate
untrusted-data delimiters. The composer is stateless, so omitting a selected skill on
the next turn cannot replay a prior procedure.

Kernel is bounded to 16 KiB / 4096 estimated tokens, the built-in floor to 32 KiB /
8192 estimated tokens, and runtime facts to 16 KiB / 4096 estimated tokens. The global
layer uses the configured `MaxBytes` and `ceil(MaxBytes / 4)` estimated-token bound.
Profile, selected skill, and conversation providers must declare positive bounds.
Actual token estimates are deterministic `ceil(UTF-8 bytes / 4)` values; the UTF-8
byte limit is authoritative.

Every result contains seven named `PromptLayerCompositionReport` values with actual
measurements, declared bounds, inclusion status, provenance on the typed result, and
bounded diagnostics. Each slot retains at most four diagnostics and each detail is at
most 256 UTF-8 bytes, truncated only at a valid rune boundary. Composer diagnostics
precede provider diagnostics. When more than four candidates exist, the result keeps
the first three and emits `DiagnosticsTruncated` as the fourth with
`omitted_count=<N>`. Flattened result diagnostics therefore cannot exceed 28 entries.

## Injection seams and floor ownership

The kernel (`system-prompt.md`) carries only invariants, runtime read contracts, the
skill extension mechanism, a one-line internal tool index, and the cross-platform
grant-before-link principle. Per-domain capability how-to lives in the mandatory
built-in floor and may be extended by the optional global layer. Both reply seams use
the same composer and the same provider contracts:

- Direct chat: `NyxIdChatGAgent.DecorateSystemPrompt` resolves the mandatory floor and
  optional global layer for a `dm` turn, then passes direct runtime facts to the
  composer. The base `RoleGAgent` never resolves these providers; classifier and
  workflow subclasses remain isolated.
- Channel/relay: `NyxIdConversationReplyGenerator.BuildSystemPrompt` resolves the same
  floor plus the global layer for the typed `channel.platform`. Channel context,
  local-skill catalog, attachment visibility, and tool-availability notices are one
  typed runtime-facts layer.

`GetCurrent(request)` is a **synchronous cached read** — never a query-time fetch.
Staleness triggers a single-flight, out-of-band background refresh using the per-turn
token supplied by the seam; the token is used only to read the public set and is never
persisted or logged. Remote gate, TTL, refresh, platform variants, and last-known-good
state affect only `GlobalSystemSkillPromptLayer`. When no usable global variant exists,
`GetCurrent` returns null. It never returns or replaces the built-in floor.

These invariants are enforced by `check_system_skill_overlay_dual_seam_injection`
(both seams call the composer, the floor is always registered, global is independent,
and `Aevatar.AI.Core` never resolves providers), `check_system_skill_overlay_set_source`
(non-secret `SetName`, no `OrgServiceToken`, skillset source, synchronous `GetCurrent`,
and no fallback dependency), and
`check_system_skill_overlay_golden_tasks_doc_present` (golden-tasks document exists;
no eval harness runs yet).

## Rollout Policy

System skill overlays roll out in stages:

1. Staging: validate prompt ordering, per-platform scoping, and eval golden-task
   results against staging hosts.
2. Canary: enable one selected agent or channel path and inspect sampled overlay
   watermark logs for unexpected prompt growth or missing injection.
3. Fleet: expand only after staging and canary have clean eval, CI, and observability
   signals.

Instant fleet rollout is forbidden. Rollback disables only the optional global layer
through host configuration (`Aevatar:SystemSkills:Enabled`) or removes the offending
member from the set. Rollback never disables the built-in floor; do not patch around
the sourcing or composition contract in runtime code.

## Relationship to immutable agent profiles

This dynamic overlay is not the trust source for an immutable agent profile. Reviewed
profiles use exact Ornn GUID and literal-version references, bind only when a direct
conversation is created, and use the separate deployment contract documented in
[Agent Profile Rollout](agent-profile-rollout.md). Overlay set membership, cached bodies,
watermarks, name lookup, and the built-in fallback cannot satisfy an exact profile
closure and never grant profile tools. Mainnet keeps this overlay disabled during the
initial profile rollout, and relay does not consume the profile new-binding gate.
