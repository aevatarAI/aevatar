---
title: "Agent Profile Rollout"
status: active
owner: eanzhao
---

# Agent Profile Rollout

Agent Profile rollout controls whether a new direct NyxID conversation may bind
one already-published Profile. It does not own Profile instructions, sealed skill
bodies, routing policy, or tool policy, and it does not create a second chat,
routing, projection, or telemetry pipeline.

## Authority Boundary

Profile content has one authority: committed `AgentProfileGAgent` state. The
namespace catalog read model replicates `AgentProfileNamespaceGAgent`
identity/reference state. The protected execution read model replicates
`AgentProfileGAgent` content/published execution state. Mainnet Host owns only
release, stage, cohort, runtime bounds, and admission pins represented by the
shared `AgentProfileRolloutReleaseSpec` Protobuf contract.

The human reference is the typed `system/nyxid-chat` reference. It is not a
Profile id. For a selected new conversation, the binder resolves that reference
once through the namespace read model, obtains the opaque `profileId`, and then
reads the protected execution snapshot once by that opaque id. It validates
namespace/execution agreement, the authoritative revision and digest, the exact
skill closure, runtime bounds, route admission, and release pins before sealing
one `AgentProfileExecutionBinding`.

The binder never primes a projection, replays an event store, reads Actor state,
or calls Ornn/HTTP. `NotSelected` is the only result that permits creation of an
unprofiled Actor. `ProfileUnavailable` and `AdmissionMismatch` fail before Actor
creation. A successful new conversation commits its complete immutable binding;
later turns, restart recovery, and replay use that conversation-owned fact with
zero Profile queries and zero Ornn reads.

## Mainnet Configuration

The only Mainnet configuration surface is
`Aevatar:AgentProfiles:NyxIdChat`:

```json
{
  "Enabled": false,
  "ReleaseSpecPath": ""
}
```

An enabled rollout requires a non-empty `ReleaseSpecPath`. A disabled rollout
requires the path to be empty; a dormant manifest path is rejected rather than
retained as latent authority.

## Pin-Only Release Spec

The Host reads exactly one strict ProtoJSON `AgentProfileRolloutReleaseSpec`.
Unknown fields and malformed values are rejected. The spec contains only:

- release and stage identity;
- the typed `system/nyxid-chat` Profile reference;
- SHADOW or ENFORCED mode and deterministic cohort inputs;
- expected published revision and snapshot digest;
- the canonically ordered exact Ornn identity closure; and
- the fixed runtime bounds.

The selector hashes `releaseId + stage + cohortSalt + actorId` for stable
new-conversation cohort assignment. The basis-point gate in an enabled spec is
in `1..10000`; disabled rollout is represented by configuration, not by a zero
gate.

The runtime bounds are exactly `max_plan_steps = 4`,
`handoff_ttl_seconds = 900`, `classifier_timeout_ms = 600`, and
`max_selected_skill_bytes = 24576`. A different value fails startup validation.

It contains no Profile instructions, skill body, routing description, Profile
tool policy, recovery policy, or opaque Profile id. Those facts come only from
the execution current-state read model after namespace resolution.

The checked-in `reviewed-release.json` uses deterministic development pins while
rollout is disabled and no release path is configured. Those pins are dormant,
not production evidence. Enablement requires the real committed Actor revision
and snapshot digest plus exact Ornn GUID, literal version, expected name, and
publisher identities. Do not retain or later re-enable against dormant pins;
the enabling deployment must install and review its current pins.

## Release Verification Tool

The deployment-only `Aevatar.Tools.AgentProfileRollout provision` operation
consumes the same strict ProtoJSON spec used by Mainnet. It exact-reads every
pinned Ornn reference by GUID and literal version, verifies the returned
identity, and atomically emits an output directory containing exactly one
canonical pin-only `reviewed-release.json`.

Despite the CLI operation name, it does not publish a package, create a
skillset, author or publish a Profile, or emit separate SHADOW and ENFORCED
copies of the complete Profile. It never resolves by name or mutable latest
version. The runtime never invokes this tool, performs remote exact reads, or
executes `protoc`; both the tool and Host use the shared compiled Protobuf
contract.

## Activation And Tool Semantics

Profile members have three activation modes:

- `ALWAYS` procedures enter every materialized Profile prompt in authoritative
  published order. They never participate in routing and never widen tools.
- `ROUTED` members may be selected by an exact alias or bounded classifier and
  take precedence over the default member.
- `DEFAULT_FOR_UNMATCHED_TURN` applies only after a true no-match or when there
  are zero routed candidates. Classifier failure, timeout, or unknown intent
  remains fail-closed and does not select the default.

SHADOW may classify and emit bounded observation, but it does not inject a
routed selected body and uses recovery authority. Profile-level instructions
and `ALWAYS` procedures still apply. ENFORCED may inject the selected sealed body
after deterministic selection. Neither mode performs a runtime remote read.

Every effective tool set is a narrowing intersection of route-owned and
caller-visible capability, the Host admission ceiling, and the Profile maximum.
The recovery branch applies the recovery policy; an ENFORCED selected branch may
also admit the selected task policy, but neither branch can restore a capability
excluded by an earlier ceiling. An empty intersection remains restricted-empty.

Profile `instructions` retain the 32,768-byte raw UTF-8 bound. The complete
materialized Profile prompt layer has an exact 65,536-byte UTF-8 bound, including
canonical `ALWAYS` wrappers and separators.

## Promotion Gates

The rollout sequence and gates are fixed:

| Gate | Requirement |
|---|---|
| Sequence | `disabled -> 500 bps SHADOW -> 500 bps ENFORCED -> 10000 bps` |
| Canary evidence | At least 24 continuous hours and 200 eligible turns per online stage |
| Offline matrix | 64/64 invariants; selection accuracy at least 95%; expected-match no-match at most 5%; classifier timeout/error at most 1% |
| Safety | Unsafe admission, approval bypass, replay acceptance, secret telemetry, and SHADOW execution side effects are all zero |
| SHADOW latency | Classifier and total added pre-turn p95 at most 600 ms |
| ENFORCED latency | Total pre-turn p95 at most 2100 ms |
| First output | p95 regression at most 10% |
| Product quality | Completion drop at most 5 percentage points; unnecessary tool-round increase at most 5% |

Security review, latency review, and the evaluation-report digest are required
before promotion. Insufficient samples extend observation; they do not relax a
gate. Rollback sets `Enabled` to false and clears `ReleaseSpecPath` in the same
deployment; an enabled release spec cannot use a zero-sized cohort. Existing
bound conversations retain their immutable binding and are never replayed,
backfilled, lazily rebound, or hot-upgraded.

## Telemetry Boundary

`AgentProfileTelemetry` uses the existing `Aevatar.GenAI` ActivitySource and
Meter. Metric labels remain bounded to fixed operation, activation-mode,
outcome, and size-kind dimensions. Traces may carry safe authority, selection,
degradation, effective-tool-count, size, and latency facts. User content,
classifier prose, raw arguments, tokens, headers, credentials, sealed bodies,
and secrets are forbidden.
