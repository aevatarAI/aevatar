# Agent Profile Authority Convergence Design

**Status:** Approved merge remediation

**Problem:** The merged histories give the same product concept two content
authorities: the Profile Actor published state and a Mainnet-loaded runtime
profile artifact. The runtime artifact can bypass owner API changes, while the
Profile publication is not consumed by new NyxID conversations.

## Semantic Decision

The Profile Actor committed state is the only authority for Agent Profile
content. The execution read model is its query replica and the only
Profile-content source a conversation binder may read. The namespace read model
is the binder's identity-resolution source: it maps the typed human reference to
the opaque Profile ID used for the execution read. A Mainnet rollout artifact
owns deployment admission only: release identity, stage, cohort selection,
runtime bounds, expected exact closure, and a pin to one published Profile
snapshot digest. It cannot contain or supply instructions, routing catalog
content, tool policy, or skill bodies.

The read model is not a second authority. If it is absent, stale, malformed, or
does not match the rollout pin, admission fails closed. The binder never primes
a projection, replays events, reads an Actor, or falls back to artifact content.

## Ownership

| Fact | Owner | Query or transport form |
|---|---|---|
| Profile identity, instructions, routing catalog, tool policy, exact Ornn refs, sealed packages | Profile Actor committed state | `AgentProfileExecutionDocument` read model |
| Profile namespace to opaque Profile ID mapping | Profile Namespace Actor committed state | namespace catalog read model |
| Rollout release, stage, cohort and admission pins | Mainnet deployment configuration | immutable admission manifest |
| One conversation's selected Profile revision and effective policy | Conversation Actor | immutable `AgentProfileExecutionBinding` in Actor state |
| Per-turn selection and narrowed tool ceiling | Conversation Actor turn protocol | committed turn authority state |

## Authoritative Profile Contract

Routing data used by the existing SHADOW and ENFORCED classifier becomes a
strongly typed part of each Profile skill binding. It is authored through the
same owner API and tool as the exact Ornn reference and is frozen into the
published snapshot:

```proto
enum AgentProfileSkillSideEffectClass {
  AGENT_PROFILE_SKILL_SIDE_EFFECT_CLASS_UNSPECIFIED = 0;
  AGENT_PROFILE_SKILL_SIDE_EFFECT_CLASS_READ_ONLY = 1;
  AGENT_PROFILE_SKILL_SIDE_EFFECT_CLASS_EXTERNAL_HANDOFF = 2;
  AGENT_PROFILE_SKILL_SIDE_EFFECT_CLASS_SERVICE_CALL = 3;
  AGENT_PROFILE_SKILL_SIDE_EFFECT_CLASS_MAINTENANCE = 4;
}

message AgentProfileSkillRoutingPolicy {
  string intent_id = 1;
  string routing_description = 2;
  repeated string explicit_trigger_aliases = 3;
  AgentProfileToolPolicy task_tool_policy = 4;
  AgentProfileSkillSideEffectClass side_effect_class = 5;
}
```

`AgentProfileSkillBinding` and `SealedAgentProfileSkillBinding` each carry one
`routing_policy`. Profile content also carries a typed recovery tool policy.
Validation requires canonical unique intent IDs and aliases, bounded text and
counts, a typed side-effect class, and a task/recovery policy no broader than
the Profile maximum policy. Publishing copies normalized routing facts beside
the sealed package; nothing is inferred from binding IDs, package names, or
free-form text.

## Rollout Admission Contract

The Mainnet artifact is renamed to an admission manifest. It contains only:

- stable rollout release and stage;
- `system/nyxid-chat` as a typed human reference, never an inferred Profile ID;
- SHADOW or ENFORCED activation mode;
- a stable cohort salt and basis-point gate;
- expected published revision and 32-byte published snapshot digest;
- the expected canonical exact Ornn closure;
- route admission ceiling and runtime classifier/prompt bounds.

The published snapshot digest pins instructions, routing policy, tool policy,
exact refs, and sealed packages together. Exact closure is retained as
human-reviewable defense in depth. The manifest cannot express Profile content.

## Conversation Binding Flow

1. The rollout selector returns `NotSelected` or one typed admission manifest
   using `release + stage + cohortSalt + actorId` as stable cohort input.
2. For a selected conversation, the async binder resolves the manifest's human
   Profile reference through `IAgentProfileNamespaceQueryPort`.
3. The binder reads `IAgentProfileExecutionSnapshotQueryPort` once using the
   opaque Profile ID returned by the namespace read model.
4. It verifies namespace identity/summary, source state version, published
   revision, snapshot digest, deterministic Profile digest, exact closure, and
   admission pins.
5. It intersects the Host admission ceiling with the Profile maximum, recovery,
   and member policies, then maps sealed packages into a complete immutable
   `AgentProfileExecutionBinding`.
6. The create command carries the complete binding. The conversation Actor
   persists it once; replays and later turns never query Profile state again.

The binder returns a typed result: `NotSelected`, `Bound`,
`ProfileUnavailable`, or `AdmissionMismatch`. Only `NotSelected` follows the
legacy unprofiled path. The two failure results map to admission unavailable
and do not create an Actor.

## Runtime Binding

The final runtime contract is
`Aevatar.AI.Abstractions.AgentProfileExecutionBinding`. It is a query-derived
execution binding, not a content authority. It contains source Profile ID,
source state version, published revision, published snapshot digest, rollout
release/stage/mode, normalized effective policies, and sealed execution members.
Each member contains the authoritative routing facts plus the already sealed
instruction body and content digest.

The binding has its own deterministic digest covering the source provenance,
rollout admission, effective policies, and sealed members. The Actor accepts an
identical replay and rejects any replacement.

## Turn Execution

The existing alias, bounded classifier, SHADOW/ENFORCED, tool collision,
telemetry, and replay behavior remains. Effective tools are always a narrowing
intersection:

```text
route-owned and caller-visible
  intersect Host admission ceiling
  intersect Profile maximum policy
  intersect selected member plus recovery policy
```

SHADOW runs alias/classifier observation but uses recovery authority and never
reads or injects a selected skill body. ENFORCED reads the selected sealed body
from the Actor-owned binding. Runtime code removes `IExactRemoteSkillFetcher`,
Ornn access tokens, network timeouts, and runtime `SKILL.md` parsing. Ornn is
used only by the publish-side Profile sealer and deployment provisioning tool.

Canonical direct chat keeps the binding in
`NyxIdChatConversationGAgentState`. The conversation controller verifies that
binding, selects the route, and commits `AgentProfileTurnAuthorityState` before
dispatching a typed command carrying both binding and authority to the turn
Actor. The turn executor materializes one exact `AgentProfileTurnCatalog` into
its transient execution session. Every provider round and same-attempt
continuation reuses that same object; any binding, authority, or reconciliation
drift fails before model or tool invocation. A fresh retry with newly committed
authority clears the transient catalog and rematerializes it.

Telemetry remains on the shared pipeline: controller route/materialization,
one-shot first streamed output across continuation rounds, and plan/handoff only
after the typed operation-reconciled fact commits with no successor command.

## Rollout Spec Portability

The rollout provisioning tool must not execute a packaged x64 `protoc` at
runtime. The release spec moves from textproto to ProtoJSON and is parsed with
`Google.Protobuf.JsonParser` into the generated message. This preserves a typed
Protobuf contract while working on Linux, Intel macOS, and Apple Silicon
without a global compiler or Rosetta.

## Failure Semantics

- Not in cohort: create an unprofiled conversation.
- Selected but namespace/read model unavailable or stale: admission unavailable.
- Manifest pin, exact closure, or deterministic digest mismatch: admission
  unavailable with no Profile content in logs.
- Bound binding replacement or tampering: reject in the Actor before effects.
- Turn classifier/materializer failure: existing bounded fail-closed recovery.

## Governance

The Roslyn Agent Profile guard must prove that a rollout selector cannot also
be a runtime content source, the binder depends only on Profile read-model
ports, and turn code has no Ornn/remote-fetch dependency. Existing projection,
stability, ownership, and docs guards remain mandatory.

## Non-Goals

- Query-time projection repair or Actor reads.
- Hot-upgrading existing conversations.
- Binding arbitrary owner Profiles to Lark or Workflow Chat in this merge.
- A compatibility path for the Host-owned full runtime Profile artifact.
