---
title: "Studio Team as First-Class Aggregate Under Scope"
status: accepted
owner: eanzhao
---

# ADR-0017: Studio Team as First-Class Aggregate Under Scope

## Context

[ADR-0016](0016-studio-member-first-published-service.md) (status: accepted) locked
Studio's primary object as `member`, and explicitly deferred a first-class team
authority:

> ### Current Team Context Policy
>
> - in the current cutover, Studio's visible `team` context is the selected `scope`
> - `Team members` in Studio means "members in the currently selected scope-backed team context"
> - backend does not need a separate first-class `team` authority to complete this member-first cutover
> - **if a first-class team model is added later, it must compose on top of the same member contract instead of replacing it**

ADR-0016 §Required Backend Contract already pre-reserved a `teamId?` field on
the member record, but did not define team identity, lifecycle, or aggregate
semantics. Today the proto and read model do not actually carry `team_id` —
the field is a forward declaration only.

Issue [#468](https://github.com/aevatarAI/aevatar/issues/468) now proposes the
new console IA:

`User -> Scope -> Teams -> Members -> Services / Runs / Approvals`

and asks for the backend surface to support `Team Directory` and `Create Team`
flows. This ADR turns the ADR-0016 hook into actual identity, contract, and
event semantics, while keeping the member-first decision intact.

## Constraints (must honor)

From ADR-0016:

- `member` remains the primary Studio object; no parallel "team-first" object model.
- `memberId / publishedServiceId` semantics are unchanged.
- Adding teams **composes on top of** the existing member contract, not replaces it.

From CLAUDE.md:

- Actor 即业务实体: one actor = one business entity; data + behavior co-located.
- 单一权威拥有者: each stable business fact has exactly one owning actor.
- 聚合必须 actor 化: cross-actor aggregates with stable business semantics must be
  actor-hosted; query-time stitching is forbidden.
- 读写分离: queries read read-models; commands write actor state.
- Projection consumes committed events only; no query-time replay.
- 删除优先: remove fields without a domain owner instead of stubbing them.

## Open Questions and Recommended Decisions

### Q1. Should `Scope` be promoted to a first-class actor as the parent of `Team`?

**Recommendation: No.** Keep `Scope` as a JWT-derived partitioning key.

Rationale:

- ADR-0016 already treats Scope as a partitioning key for members (no `ScopeGAgent`
  exists; `StudioMember` is partitioned by `scope_id` field and queried via the
  read model). Treating teams the same way preserves architectural symmetry.
- "List teams in scope X" is a read-side filter, not a stable cross-actor
  aggregate fact. `TeamCurrentStateDocument.scope_id` filtering is sufficient.
  This does not violate the "聚合必须 actor 化" rule, which targets aggregate
  *facts* (counts, summaries, derived state), not flat list filters.
- Promoting Scope to an actor would force a much wider migration (member,
  workflow, script, service all currently treat scope as a flat field). Out of
  scope for #468.

If a future requirement makes Scope-level aggregation a stable business fact
(e.g. scope-level quota, scope-level governance rollups), that requires its
own ADR and is explicitly **not** decided here.

### Q2. Where does the `member -> team` ownership fact live?

**Recommendation: On the Member side. `team_id` is a field of `StudioMemberState`.**

Rationale:

- ADR-0016 §Required Backend Contract already lists `teamId?` on the member
  record. This ADR makes that field load-bearing.
- "This member currently belongs to team X" is a fact about *the member*, not
  about the team. The member's lifecycle (create / build / bind / retire) is
  independent of any team's lifecycle. A member can be unassigned, reassigned,
  or exist without a team.
- "Single owner" rule: the assignment fact has one authoritative owner — the
  Member actor. The Team's roster and `member_count` are *derived* from this
  fact via projection.
- Reading "which team is this member in" is local to the Member actor / its
  read model.

The Team's view of "who are my members" is **not** an independent fact — it is
the projection of all `StudioMemberReassignedEvent` events filtered by `team_id`. See
Q3.

### Q3. How does `TeamGAgent` maintain aggregate facts (`member_count`, etc.) safely?

**Recommendation: TeamGAgent persists the full `member_ids` roster (not just a
counter), processes inbound member events idempotently against that roster,
and reassignment is carried by a single `StudioMemberReassignedEvent { from, to }`
event that both source and destination TeamGAgents observe.**

Rationale:

- A bare counter cannot be safely maintained from `joined`/`left` deltas:
  - A duplicated `joined` event (network retry, replay during recovery) would
    double-increment.
  - A reassignment from team A to team B without a `leave-old` signal leaves A
    permanently stale.
- The fix is to make idempotency a property of the **state**, not the event
  pipeline. TeamGAgent persists `repeated string member_ids` and treats every
  event as a set operation: "add member_id if not present" / "remove member_id
  if present". Replays and duplicates collapse to no-ops by construction.
- `member_count` is then a derived projection of `member_ids.size()`, written
  alongside the roster in the same committed `TeamMemberRosterChangedEvent`.
- The full roster also lets the Team aggregate answer "is M currently in this
  team?" locally without a cross-actor query. This is needed by future flows
  (e.g. team-scoped permissions) and avoids re-litigating the count protocol
  later.
- For reassignment, `StudioMemberGAgent` emits a single
  `StudioMemberReassignedEvent { from_team_id, to_team_id }`. Both source and
  destination TeamGAgents subscribe to member events and apply set operations:
  - `from = "T1"` matches → T1 removes the member_id (idempotent: no-op if absent)
  - `to = "T2"` matches → T2 adds the member_id (idempotent: no-op if present)
  - Pure assignment (`from_team_id` absent) is the same protocol with `from` absent.
  - Pure unassignment (`to_team_id` absent) is the same protocol with `to` absent.
  This collapses three event types into one and removes the leave-old / join-new
  ordering hazard.
- The Team read model materializes from TeamGAgent's committed state version
  ("权威源单调覆盖"), not from member events directly.

#### Roster size constraint

Persisting `member_ids` makes TeamGAgent state grow with team size. This ADR
targets `team` as a small-to-medium organizational unit (< 10,000 members).
Larger groupings (org-wide rollups, scope-level aggregation) are explicitly
out of scope and require a different aggregate. A guard test should reject
TeamGAgent states exceeding a hard cap.

### Q4. What is the v1 field set for the Team Directory read model?

**Recommendation: Cut speculative fields; ship the minimum that has a real domain
owner.**

Keep:

| Field | Authoritative source |
|---|---|
| `team_id` | TeamGAgent identity |
| `scope_id` | TeamGAgent state (immutable) |
| `display_name` | TeamGAgent state |
| `description` | TeamGAgent state |
| `lifecycle_stage` | TeamGAgent state (`StudioTeamLifecycleStage` enum: `ACTIVE` / `ARCHIVED`) |
| `member_count` | TeamGAgent state (derived from `member_ids.size()`) |
| `created_at_utc` | TeamGAgent state |
| `updated_at_utc` | TeamGAgent state version timestamp |

Cut from #468 proposal:

| Field | Reason |
|---|---|
| `pendingApprovalCount` | No team-level approval domain exists today. The only approval concept (`ToolApprovalMiddleware` / `HumanApprovalResolution`) is run-scoped, not team-scoped. Adding a count without a queue is fictional. |
| `healthStatus` | Health is undefined for a Team. No domain rules exist for what makes a team "healthy" / "degraded". Adding the field invents semantics. |
| `lastActiveAt` | Ambiguous (last member edit? last run? last bind?). Defer to a later ADR after the activity domain is defined. |
| `lastRunAt` | Same as above. Run is member-scoped; rolling up to team-level is a derived view that needs explicit definition. |

These fields can be re-introduced via separate ADRs once their domains are
defined. Not shipping them in v1 is "删除优先" applied to read-model schemas.

### Q5. What is the archive policy?

**Recommendation: Archive is irreversible and is a metadata signal only — it does
not act as a write-side invariant rejecting new member assignments.**

Rationale:

- A previous draft of this ADR said "Archived teams cannot accept new member
  assignments" but did not specify how that invariant is enforced. There is no
  clean implementation:
  - **Read-model check at the application service**: eventually consistent;
    races with a concurrent archive (archive committed but read model not yet
    updated → stale "active" → assignment slips through).
  - **Synchronous query to TeamGAgent**: violates the actor/query boundary
    (CLAUDE.md: "禁止 generic actor query/reply"; query goes through read model).
  - **Saga (request-accept-revert)**: technically clean, but adds two-phase
    commit complexity that no other Studio flow has and that is unnecessary
    for the actual product semantics — archived teams are vestigial labels,
    not gated resources.
- The pragmatic policy: archive is a one-way label. UI surfaces a warning
  before allowing assignment to an archived team; backend accepts the
  assignment. Already-assigned members are not auto-unassigned on archive.
- Archive is **irreversible** to keep TeamGAgent's lifecycle a strict
  monotonic state machine. If a user wants the team back, they create a new
  team and (optionally, via a future migration tool) reassign members.
  Reactivation invites questions about identity reuse and audit ambiguity that
  are not worth re-opening for v1.
- This matches the SaaS norm (archived workspace is a tag, not a lock) and
  fits existing CLAUDE.md rules without inventing new mechanisms.

If a hard rejection is later required (e.g. for compliance), it can be added
as a separate ADR introducing the saga protocol — explicitly, not implicitly.

### Q6. How does PATCH `member` express assign / unassign / no-change?

**Recommendation: Lock JSON Merge Patch semantics at the HTTP boundary, with
the three-state distinction carried only as far as the application-layer DTO.
Proto-level command and event payloads only ever carry the resolved new value
(or no command at all) — they do not encode "no change".**

Rationale:

- `team_id` has three distinct **HTTP** intents the wire must carry:
  - **No change**: don't touch the assignment.
  - **Assign to T**: set `team_id = "T"`.
  - **Unassign**: clear the assignment.
- proto3 plain `string` cannot distinguish "not set" from "set to empty"
  (`Field unset` and `Field = ""` both round-trip to empty string), and proto3
  `optional string` only carries two states (HasValue / not HasValue). Neither
  can distinguish all three HTTP intents on its own. The fix is **not** to
  push three-state semantics into proto; it's to resolve "no change" at the
  application layer before any command is dispatched.
- Layered handling (locked):

  | Layer | Concern |
  |---|---|
  | HTTP body | three states: `absent` / `null` / non-empty |
  | Application DTO | distinguish `absent` (no command) from `null` (unassign command) and non-empty (assign/reassign command). A `Patch<T>` wrapper or `JsonElement?` is required. |
  | Actor command | only emitted when a change is intended; carries the *resolved* new value (or "unassigned"). No "no change" sentinel. |
  | Committed event (`StudioMemberReassignedEvent`) | always reflects an actual roster mutation; cannot represent "no change". |
  | Persisted state (`StudioMemberState.team_id`) | two states: `HasValue` (assigned to T) / not `HasValue` (unassigned). Modeled as `optional string`. |

- HTTP body convention (locked):

  | JSON value of `teamId` | Wire intent |
  |---|---|
  | field absent | no change — application emits no reassignment command |
  | `null` | unassign — application emits `StudioMemberReassignedEvent` with `to_team_id` cleared |
  | `""` (empty string) | **rejected** as invalid input (4xx) |
  | `"T"` (non-empty) | assign / reassign to team `T` — application emits `StudioMemberReassignedEvent` with `to_team_id = T` |

- Rejecting empty string defends against accidental clears caused by frontend
  serialization bugs (the proto3 default for an unset string round-trips to
  `""`, so accepting `""` would also let an unset wire field silently clear
  the assignment).
- This decision applies equally to PATCH `team` itself (display_name,
  description) — the wire convention above governs every nullable PATCH field;
  proto-level events only express committed values.

### Q7. Does this ADR introduce a `User` aggregate?

**Recommendation: No.** Out of scope.

Rationale:

- Issue #468 does not require a User aggregate; it requires Team identity and
  member-to-team ownership.
- The repo has no User actor today (humans are JWT claims). Adding one is a
  much larger decision (identity provenance, NyxID integration boundary, RBAC
  surface) and is independent of Team identity.
- This ADR explicitly defers "team membership for humans" (e.g. invite a user
  to a team, role within team) to a separate ADR. The current `member` is an
  agent-service member, per ADR-0016, and that semantic stays.

If "team membership for humans" is needed, the new aggregate is likely
something like `TeamSeat` or `TeamUserBinding` and lives next to (not inside)
the existing `StudioMember` model.

## Decision

For Studio, the model is:

`scope (jwt partition key) -> team (actor) -> member (actor) -> implementation -> published service -> endpoint -> run`

Concretely:

- `team` is a first-class actor (`StudioTeamGAgent`) under a scope.
- `team_id` is a stable identity, generated server-side at team creation.
- `team` lifecycle is a strict monotonic enum: `active` → `archived`. Archive
  is **irreversible**. Archive does **not** act as a write-side invariant
  (see Q5); it is a metadata signal surfaced to clients.
- A member's `team_id` is a field of `StudioMemberState` (per ADR-0016
  §Required Backend Contract), modelled as `optional string` so that absence
  means "unassigned".
- `TeamGAgent` does **not** own member identity. Member identity remains
  owned by `StudioMemberGAgent`.
- `TeamGAgent` persists `member_ids` (the roster) and processes member events
  idempotently as set operations. `member_count` is derived from the roster.
- Member moves between teams are carried by a single
  `StudioMemberReassignedEvent { from_team_id, to_team_id }` event. Pure
  assign / unassign use the same event with one side empty (see Q3).
- The Team Directory read model materializes from `TeamGAgent.state` only;
  never from query-time stitching across member read models.
- `Scope` remains a JWT-derived partitioning key. No `ScopeGAgent` is
  introduced.

## Locked Rules

### 1. Identity

- `teamId` is the stable identity of a team. It is server-generated at create
  time and immutable.
- `teamId` is partitioned within a `scopeId`; `(scopeId, teamId)` is globally unique.
- `teamId` is not derived from `displayName` and is rename-safe.
- `actorId` form: `studio-team:{scopeId}:{teamId}` (parallel to
  `studio-member:{scopeId}:{memberId}`).

### 2. Composition (not replacement) of the Member contract

- `StudioMember` proto and APIs remain authoritative for member identity.
- `team_id` is added to `StudioMemberState` as `optional string`; absence
  means "unassigned". Empty string is **not** valid wire input.
- Existing member endpoints stay; `teamId?` is added to create / patch payloads
  with the JSON Merge Patch semantics locked in Q6.
- The published service identity rules from ADR-0016 are unchanged.

### 3. Authoritative ownership and reassignment protocol

- "Member belongs to team X": owned by `StudioMemberGAgent`, persisted in
  `StudioMemberState.team_id` (`optional string`; absence = unassigned). The
  fact is mutated via the single `StudioMemberReassignedEvent` (Locked Rule 4).
- "Team metadata (name, description, lifecycle)": owned by `StudioTeamGAgent`.
- "Team roster (member_ids set)": owned by `StudioTeamGAgent`, mutated only via
  idempotent set operations triggered by committed `StudioMemberReassignedEvent`.
  Replays and duplicate deliveries collapse to no-ops by construction.
- "Team aggregate facts (`member_count`)": derived from `member_ids`, written
  alongside in the same `StudioTeamMemberRosterChangedEvent`.
- "Member create with initial team": when `POST /api/scopes/{scopeId}/members`
  carries a non-empty `teamId`, the application service validates that the team
  exists (read-model check), then the command port dispatches two events
  sequentially — `StudioMemberCreatedEvent` first (no team field), then
  `StudioMemberReassignedEvent { from_team_id absent, to_team_id = T }`.
  Member-side event ordering is guaranteed; TeamGAgent observes the
  reassignment asynchronously (eventually consistent).
  `StudioMemberCreatedEvent` is **not** extended with a `team_id` field.
- The Team read model has no fact that is not authoritative in `StudioTeamGAgent`.

### 4. Reassignment event contract

- The single source of "member belongs to team" change is
  `StudioMemberReassignedEvent`. Per-team `joined` / `left` event types are
  not introduced; both source and destination TeamGAgents derive their
  reaction from the same event by matching `from_team_id` / `to_team_id`
  against their own `team_id`.
- `from_team_id` and `to_team_id` are proto3 `optional string`. Presence
  carries the meaning ("a team is named") and absence carries the meaning
  ("unassigned"); empty-string sentinels are not used. Proto3 cannot
  distinguish unset from `""` for plain string, so the empty-string sentinel
  pattern is rejected here.
- At least one of `from_team_id` / `to_team_id` must be present in any
  emitted event. Application-layer validation rejects events where both are
  absent or where both are present and equal. A CI guard checks this on the
  emit path.
- TeamGAgents must handle the event idempotently: "remove if present" /
  "add if not present" against `member_ids`.
- Cross-scope reassignment is **not** allowed in v1. `from_team_id` and
  `to_team_id` (when present) must resolve to a team whose `scope_id` equals
  the member's `scope_id`. A guard test enforces this.

### 5. Lifecycle and archive

- `lifecycle_stage` is a typed enum (`StudioTeamLifecycleStage`) with values
  `UNSPECIFIED` / `ACTIVE` / `ARCHIVED`. It is not a free-form string.
- Lifecycle is monotonic: `ACTIVE → ARCHIVED`. There is **no** reactivation
  event in v1; archive is irreversible.
- Archive does **not** reject member assignment commands at the actor layer.
  Clients are responsible for warning before assigning to an archived team.
  Existing assignments survive archive.
- Removing the irreversibility, or adding a hard rejection invariant, requires
  a new ADR.

### 6. Scope

- `Scope` is not an actor.
- "List teams in scope" is a read-model filter on `team_current_state_document.scope_id`.
- No `ScopeAggregate` is introduced; cross-scope aggregation is not a goal.

### 7. Read model discipline

- Team Directory read model v1 fields are exactly the list in Q4 ("Keep").
- Adding any field requires a new ADR or an amendment that names the domain
  owner, the committed event source, and the version source.
- No query-time stitching, no event replay in the query path. Standard
  projection pipeline only.

### 8. Approval, health, activity

- No team-level approval inbox is defined by this ADR. `pendingApprovalCount`
  is **not** on the contract.
- No team-level health semantics are defined. `healthStatus` is **not** on the
  contract.
- Team-level activity (`lastActiveAt`, `lastRunAt`) is **not** on the contract.
  These require their own domain ADR before becoming read-model fields.

### 9. User identity

- This ADR does not introduce a User aggregate, RBAC, or human team
  membership semantics.
- Future work in that area must compose on top of this ADR's `Team` model, not
  replace it.

## Required Backend Contract

### Proto additions

`agents/Aevatar.GAgents.StudioTeam/studio_team_messages.proto` (new file):

```proto
syntax = "proto3";

import "google/protobuf/timestamp.proto";

// Lifecycle: monotonic, no reactivation in v1. See ADR-0017 §Locked Rule 5.
enum StudioTeamLifecycleStage {
  STUDIO_TEAM_LIFECYCLE_STAGE_UNSPECIFIED = 0;
  STUDIO_TEAM_LIFECYCLE_STAGE_ACTIVE      = 1;
  STUDIO_TEAM_LIFECYCLE_STAGE_ARCHIVED    = 2;
}

message StudioTeamState {
  string team_id        = 1;
  string scope_id       = 2;
  string display_name   = 3;
  string description    = 4;
  StudioTeamLifecycleStage lifecycle_stage = 5;
  // Roster: persisted set of member_ids currently assigned to this team.
  // Mutated only via idempotent set operations on committed
  // StudioMemberReassignedEvent (see ADR-0017 §Locked Rule 3, §Locked Rule 4).
  // member_count is a derived projection of member_ids.size().
  repeated string member_ids = 6;
  google.protobuf.Timestamp created_at_utc = 7;
  google.protobuf.Timestamp updated_at_utc = 8;
  // Reserved for v2: last_activity_at_utc, etc.
}

message StudioTeamCreatedEvent {
  string team_id        = 1;
  string scope_id       = 2;
  string display_name   = 3;
  string description    = 4;
  google.protobuf.Timestamp created_at_utc = 5;
}

// proto3 `optional` carries presence — absent field means "no change".
// Empty string is a valid clear for description; display_name empty is rejected
// at the application layer.
message StudioTeamUpdatedEvent {
  string team_id  = 1;
  string scope_id = 2;
  optional string display_name = 3;
  optional string description  = 4;
  google.protobuf.Timestamp updated_at_utc = 5;
}

message StudioTeamArchivedEvent {
  string team_id  = 1;
  string scope_id = 2;
  google.protobuf.Timestamp archived_at_utc = 3;
}

// Roster mutation event: emitted by TeamGAgent after applying an idempotent
// set operation triggered by a committed StudioMemberReassignedEvent. The
// `effect` enum makes "did this event mutate the roster or no-op?" explicit
// for downstream auditors and the read model projection.
enum StudioTeamRosterEffect {
  STUDIO_TEAM_ROSTER_EFFECT_UNSPECIFIED = 0;
  STUDIO_TEAM_ROSTER_EFFECT_ADDED       = 1;
  STUDIO_TEAM_ROSTER_EFFECT_REMOVED     = 2;
  STUDIO_TEAM_ROSTER_EFFECT_NOOP        = 3;  // duplicate / replay collapsed
}

message StudioTeamMemberRosterChangedEvent {
  string team_id   = 1;
  string scope_id  = 2;
  string member_id = 3;
  StudioTeamRosterEffect effect = 4;
  int32  member_count = 5;       // size of member_ids after this event
  google.protobuf.Timestamp changed_at_utc = 6;
}
```

`agents/Aevatar.GAgents.StudioMember/studio_member_messages.proto` (extend
existing):

```proto
// Add to StudioMemberState. `optional` so absence means "unassigned"; empty
// string is rejected at the application layer (see ADR-0017 §Q6).
optional string team_id = 50;

// Single reassignment event covers assign / unassign / move. Both source and
// destination TeamGAgents subscribe and apply idempotent set operations.
// from_team_id and to_team_id are `optional` so "unassigned" is presence=false
// rather than empty-string sentinel — proto3 cannot distinguish unset string
// from "" otherwise (see ADR-0017 §Q6 layered handling).
//   pure assign:   from_team_id absent,    to_team_id = "T2"
//   pure unassign: from_team_id = "T1",    to_team_id absent
//   move:          from_team_id = "T1",    to_team_id = "T2"
// Constraints (enforced at the application layer with CI guard):
//   - At least one of from_team_id / to_team_id must be present.
//   - from_team_id == to_team_id (both present and equal) is rejected.
//   - from_team_id and to_team_id (when present) must resolve to a team whose scope_id equals the member's scope_id.
message StudioMemberReassignedEvent {
  string member_id              = 1;
  string scope_id               = 2;
  optional string from_team_id  = 3;
  optional string to_team_id    = 4;
  google.protobuf.Timestamp reassigned_at_utc = 5;
}
```

> **Note on event identity.** `StudioMemberAssignedToTeamEvent` /
> `StudioMemberUnassignedFromTeamEvent` were named in earlier drafts of this
> ADR and the original review thread. They are **not** part of the locked
> contract. The single `StudioMemberReassignedEvent` replaces both, eliminating
> the leave-old / join-new ordering hazard called out in the line-298 review.

> **Note on first-time assignment via member create.** When `POST /api/scopes/{scopeId}/members`
> is invoked with a non-empty `teamId`, the application command port dispatches
> two events sequentially to `StudioMemberGAgent`:
>
> 1. `StudioMemberCreatedEvent` (no `team_id` field — created event keeps a
>    single responsibility), then
> 2. `StudioMemberReassignedEvent { from_team_id absent, to_team_id = T }`.
>
> The two events are dispatched sequentially by the application command port.
> Member-side event ordering is guaranteed (Created lands before Reassigned),
> but TeamGAgent observes the reassignment asynchronously — there is a brief
> eventually-consistent window where the member exists but the Team's roster
> has not yet materialized the addition. This is normal projection lag.
> TeamGAgent only
> subscribes to `StudioMemberReassignedEvent`; `StudioMemberCreatedEvent`
> never feeds the team pipeline. This keeps the event vocabulary minimal and
> avoids extending `StudioMemberCreatedEvent` with a `team_id` field. See
> §Locked Rule 4 and §Cutover Order step 3.

### Read model

`src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto`:

```proto
message StudioTeamCurrentStateDocument {
  // Metadata
  string id              = 1;
  string actor_id        = 2;
  int64  state_version   = 3;
  string last_event_id   = 4;
  google.protobuf.Timestamp updated_at_utc = 5;

  // Identity
  string team_id  = 20;
  string scope_id = 21;

  // Display
  string display_name = 22;
  string description  = 23;

  // Lifecycle (mirrors actor enum, wire-stable via int).
  StudioTeamLifecycleStage lifecycle_stage = 24;
  google.protobuf.Timestamp created_at_utc = 25;

  // Aggregate (derived from StudioTeamState.member_ids.size()).
  // The full roster is NOT mirrored into the read model — listing members goes
  // through the member read model filtered by team_id (see API §Team -> Member listing).
  int32 member_count = 30;
}
```

Extend `StudioMemberCurrentStateDocument` with `optional string team_id = 50`.
Absence means "unassigned"; empty string is **not** a valid serialized value.

### HTTP endpoints

Team CRUD:

- `POST   /api/scopes/{scopeId}/teams`            — create team
- `GET    /api/scopes/{scopeId}/teams`            — list teams in scope
- `GET    /api/scopes/{scopeId}/teams/{teamId}`   — read team
- `PATCH  /api/scopes/{scopeId}/teams/{teamId}`   — update display name / description (Merge Patch semantics, see below)
- `POST   /api/scopes/{scopeId}/teams/{teamId}/archive` — archive (irreversible)

Team -> Member listing (read model query, filtered):

- `GET    /api/scopes/{scopeId}/teams/{teamId}/members` — list members assigned to this team (queries the member read model filtered by `team_id`; **not** TeamGAgent state)

Member-side extensions (do not break existing endpoints):

- `POST   /api/scopes/{scopeId}/members` — body accepts optional `teamId`
- `PATCH  /api/scopes/{scopeId}/members/{memberId}` — body accepts `teamId` per Merge Patch table below

#### Merge Patch semantics for `teamId` (locked, see Q6)

Both the team-update and member-update endpoints follow JSON Merge Patch
semantics for `teamId` (and analogously for `displayName` / `description` on
team update):

| JSON value of `teamId` | Server intent |
|---|---|
| field absent | no change to current assignment |
| `null` | unassign (clear `team_id`); rejected if used in team update for `displayName` |
| `""` (empty string) | **rejected** as 400 Bad Request |
| `"T"` (non-empty string) | assign / reassign to team `T` |

The application-layer DTO must distinguish "field absent" from "explicit
null". A `JsonElement?`-backed wrapper or an explicit `Patch<T>` type is
acceptable; the wire semantics in the table above are normative.

No new endpoint for member-to-team binding. Member-to-team is a *property* of
the member, mutated via the existing member PATCH surface. This avoids the
double-write API shape ("attach member to team" + "set member's team") that
would otherwise need its own consistency contract.

### Application contracts

- `IStudioTeamQueryPort` (read): list / get team summary; queries the read model only.
- `IStudioTeamCommandPort` or equivalent dispatch through `IActorDispatchPort`
  (write): create / update / archive. Must carry business semantics (validation,
  default values), not be a pass-through shell.
- Existing `IStudioMemberService` extended with `team_id` propagation in
  create / patch flows; no new "binding" service.

## Consequences

- Studio left rail and Team Directory page can read a real backend source of
  truth via `StudioTeamCurrentStateDocument`.
- Frontend no longer infers teams from unrelated scope assets.
- Member contract remains the primary object per ADR-0016; team is an
  optional grouping that composes on top.
- A small amount of cross-actor projection work is added: TeamGAgent must
  subscribe to committed member events. This is the standard projection
  pipeline pattern, not a new mechanism.
- TeamGAgent state grows linearly with team size because the full `member_ids`
  roster is persisted (required for idempotent event processing). v1 caps
  expected size at < 10,000 members per team; very large groupings need a
  different aggregate.
- Archive is irreversible. Operators / users who archive a team by mistake
  must create a new team and reassign members — no undo.
- v1 ships without `pendingApprovalCount`, `healthStatus`, `lastActiveAt`,
  `lastRunAt`. Frontend Team Directory cards must be designed without those
  fields, or those fields must be added in follow-up ADRs *with* domain
  definitions.

## Cutover Order

1. Land this ADR (proposed → accepted) and align on Q1–Q7.
2. Add `StudioTeam` proto messages (`StudioTeamLifecycleStage`,
   `StudioTeamRosterEffect`, `StudioTeamState`, the four event types) and the
   `StudioTeamGAgent` actor with `Created / Updated / Archived` handling.
3. Extend `StudioMemberState` with `optional string team_id` and add
   `StudioMemberReassignedEvent` (with `optional string from_team_id` /
   `to_team_id`). Reject empty-string `team_id` at the application layer.
   `StudioMemberCreatedEvent` is **not** extended with a `team_id` field.
4. Wire the application command port so that `POST /members` with a non-empty
   `teamId` dispatches two events sequentially — first
   `StudioMemberCreatedEvent`, then `StudioMemberReassignedEvent
   { from_team_id absent, to_team_id = T }`. Member-side ordering is
   guaranteed; team roster update is eventually consistent.
5. Wire `StudioTeamGAgent` to subscribe to committed
   `StudioMemberReassignedEvent` and apply idempotent set operations to
   `member_ids`, emitting `StudioTeamMemberRosterChangedEvent` with the
   correct `effect` (ADDED / REMOVED / NOOP).
6. Add `StudioTeamCurrentStateDocument` read model and the standard projector.
7. Extend member endpoints (`POST` / `PATCH`) to accept `teamId` per the Merge
   Patch table in §HTTP endpoints. The application DTO must carry a
   `Patch<string>` (or equivalent) wrapper to distinguish `absent` from
   `explicit null` before issuing any actor command.
8. Add team CRUD endpoints (`POST` / `GET` / `PATCH` / archive) and the
   `team -> members` listing endpoint.
9. Add `IStudioTeamQueryPort` / command dispatch contracts in the application
   layer.

Each step is gated by build + targeted tests + the relevant CI guards
(`projection_state_version_guard.sh`, `projection_route_mapping_guard.sh`,
`workflow_binding_boundary_guard.sh` where applicable). Additional guards to
add (or update) as part of step 5:

- A guard rejecting `StudioMemberReassignedEvent` instances where both
  `from_team_id` and `to_team_id` are absent, or where both are present and
  equal (no-op event).
- A guard rejecting cross-scope reassignment.
- A roster size cap test on `StudioTeamState.member_ids` (see Q3 size
  constraint).
- A guard rejecting empty-string `teamId` on the member PATCH / POST surface.

## Non-Goals

This ADR does **not** define:

- A `User` aggregate or human identity model.
- Team-level RBAC, roles within a team, or invite flows.
- Approval workflows at the team level (`pendingApprovalCount` is explicitly
  excluded).
- Team health, activity, or run rollup semantics (`healthStatus`,
  `lastActiveAt`, `lastRunAt` are excluded from v1).
- Reactivation of an archived team. `StudioTeamReactivatedEvent` is
  intentionally not introduced; if recovery is needed, future work must add it
  via a new ADR.
- A write-side hard rejection of member assignment to archived teams. Archive
  is a metadata signal (Q5).
- `Scope` as a first-class actor.
- Frontend visual layout for the Team Directory.
- Migration of existing scope-implicit teams (in current state, "the team is
  the scope" per ADR-0016; a future migration ADR can decide whether to
  auto-create a default team per scope).
- Hierarchical / nested teams.
- Mirroring the full team `member_ids` roster into the Team read model (the
  read model carries only `member_count`; member listing goes through the
  member read model filtered by `team_id`).

## Outcome

After this ADR is accepted and implemented, Studio backend semantics are:

- `team` is a first-class actor under a scope, with stable identity and a
  minimum aggregate read model.
- `member` remains the primary Studio object; `team_id` is an optional
  property of a member.
- Team Directory queries read a real backend source of truth without
  speculative fields.
- The "if a first-class team model is added later, it must compose on top of
  the same member contract" hook from ADR-0016 is now realized.
