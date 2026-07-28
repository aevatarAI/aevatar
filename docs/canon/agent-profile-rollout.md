---
title: "Agent Profile Rollout"
status: active
owner: eanzhao
---

# Agent Profile Rollout

Agent profile rollout is a deployment and conversation-creation concern. It does not extend the dynamic System Skill Overlay and does not create a second chat, routing, tool, or telemetry system.

## Immutable release closure

The deployment-only `Aevatar.Tools.AgentProfileRollout` command has two operations:

- `provision` publishes the four reviewed focused packages, reads every package back by canonical GUID plus literal version, verifies name, publisher ID, package bounds, file set, and exact declared tool names, creates the reviewed skillset, and verifies that its exact closure contains only those four versions.
- `evaluate` reads a typed evaluation report and applies the fixed safety, quality, and latency gates.

The reviewed release input contains no skill or skillset GUID. GUIDs can enter a deployable profile only from successful publish responses followed by exact read-back. A repeated provision reads the existing resolved GUIDs again; it does not resolve by name or read a mutable latest version. The SHADOW and ENFORCED outputs are separate complete ProtoJSON artifacts with distinct profile versions and fixed activation modes.

The public `1.1` packages remain ordinary on-demand content. They never become curated profile trust inputs.

## Mainnet ownership

Mainnet owns the immutable artifact and validates it locally at startup. The default configuration is:

```json
{
  "NewBindingsEnabled": false,
  "CohortBasisPoints": 0,
  "ReviewedProfilePath": ""
}
```

An enabled gate requires one complete reviewed artifact and a cohort in `1..10000`. Selection hashes `profileVersion + actorId` and runs only when a direct NyxID conversation is created. It performs no Ornn I/O. Configuration changes affect later creations only; an actor-owned snapshot is never replayed, backfilled, lazily rebound, or hot-upgraded. Rollback sets the new-binding cohort to zero.

`Aevatar:SystemSkills:Enabled` stays false. Relay does not read this gate because it has no durable profile owner.

## Activation semantics

SHADOW may execute the existing bounded profile router/classifier and emit candidate routing telemetry. It must return to the legacy path before exact fetch, selected-body injection, tool materialization, plan creation, or handoff. ENFORCED uses the single profile routing and tool-admission chain established by the profile implementation children. Failures only reduce the available tool set.

The first rollout limits are fixed:

| Gate | Requirement |
|---|---|
| Sequence | `0 -> 500 bps SHADOW -> 500 bps ENFORCED -> 10000 bps` |
| Canary evidence | At least 24 continuous hours and 200 eligible turns per online stage |
| Offline matrix | 64/64 invariants; selection accuracy at least 95% |
| Safety | unsafe admission, approval bypass, replay acceptance, secret telemetry, and SHADOW execution side effects are all zero |
| SHADOW latency | classifier and added pre-turn p95 at most 600 ms |
| ENFORCED latency | total pre-turn p95 at most 2100 ms |
| First output | p95 regression at most 10% |
| Product quality | completion drop at most 5 percentage points; unnecessary tool-round increase at most 5% |

Security review, latency review, and the evaluation-report digest are required before promotion. Insufficient samples extend observation; they do not relax a gate.

## Telemetry boundary

`AgentProfileTelemetry` uses the existing `Aevatar.GenAI` ActivitySource/Meter owner. Metrics use only fixed seam, activation-mode, outcome, and size-kind labels. Profile revision/hash, exact provenance, opaque intent, route/degradation, effective tool count, layer size, plan/handoff status, recovery count, and first-output latency may appear on traces. User content, classifier prose, raw arguments, tokens, headers, credentials, and secrets are forbidden.

## Dependency state

The rollout remains disabled until the immutable exact-reference, ordered overlay, actor-owned binding, deterministic routing/tool admission, and typed handoff children have landed and passed acceptance. The deployment layer must not duplicate their runtime Protobuf schema or simulate their actor-owned state while those contracts are absent.
