---
title: "System Skill Overlay Authoring Contract"
status: active
owner: eanzhao
---

# System Skill Overlay Authoring Contract

This document records the authoring and rollout contract for system skill
overlays. The runtime validation is implemented in
`OverlayAuthoringContract`; this page is the governance reference for authors,
operators, and review.

## Authoring Frontmatter

Every overlay skill must start with YAML-style frontmatter. The builder only
materializes skills whose frontmatter satisfies all six fields below.

| Field | Type | Required | Meaning |
|---|---|---:|---|
| `title` | string | yes | Human-readable overlay title. |
| `scope` | string | yes | Operational scope owned by the authoring team. |
| `priority` | integer | yes | Relative ordering and selection priority. |
| `max_bytes` | integer | yes | Author-declared byte budget for the overlay body. |
| `applies_to` | enum string | yes | One of `channel`, `dm`, or `both`. |
| `non_override` | boolean | yes | Must state whether the overlay is non-overriding guidance. |

The builder rejects missing fields, non-integer `priority` or `max_bytes`,
non-boolean `non_override`, and unsupported `applies_to` values. Rejected
skills are skipped and are not partially included.

## Provenance Model

Overlay materialization reads from Ornn using the host-bound organization
service token. A source skill is trusted only when it is private and carries the
configured organization system-skill tag. The token is a host secret and must
not be exposed in prompts, logs, docs, or client responses.

The materialized `SystemSkillOverlay` stores:

- `overlay_markdown`: the composed overlay body.
- `source_watermark`: a deterministic hash of the included skill names,
  descriptions, and bodies.
- `materialized_at`: the refresh timestamp.

The watermark is the operational provenance handle for a turn. It identifies
which composed overlay was used without logging raw overlay content.

## Rollout Policy

System skill overlays must roll out in stages:

1. Staging: validate authoring contract acceptance, prompt ordering, and eval
   golden-task results against staging hosts.
2. Canary: enable one selected agent or one selected channel path and inspect
   sampled overlay watermark/token logs for unexpected prompt growth or missing
   overlay injection.
3. Fleet: expand only after staging and canary have clean eval, CI, and
   observability signals.

Instant fleet rollout is forbidden. Any rollback must disable the host
configuration or remove the organization tag from the offending source skill;
do not patch around the authoring contract in runtime code.
