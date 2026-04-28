---
title: "Studio Team as First-Class Aggregate Under Scope"
status: proposed
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
  Member actor. The Team's roster and `memberCount` are *derived* from this
  fact via projection.
- Reading "which team is this member in" is local to the Member actor / its
  read model.

The Team's view of "who are my members" is **not** an independent fact — it is
the projection of all `MemberAssignedToTeam` events filtered by `team_id`. See
Q3.

### Q3. How does `TeamGAgent` maintain aggregate facts (`memberCount`, etc.)?

**Recommendation: TeamGAgent subscribes to committed Member events and updates
its own state via projection. Aggregates are written to TeamGAgent state, then
materialized into the Team read model via the standard projection pipeline.**

Rationale:

- CLAUDE.md mandates that aggregates be actor-hosted. `memberCount` is an
  aggregate fact; it must live in `TeamGAgent.state`, not be computed query-time.
- The projection input is committed `StudioMemberAssignedToTeamEvent` /
  `StudioMemberUnassignedFromTeamEvent`. TeamGAgent consumes these as inbound
  events and emits its own `TeamMemberRosterChangedEvent` to commit the state
  change. The Team read model materializes from TeamGAgent's committed state,
  not from Member events directly.
- This satisfies "权威源单调覆盖": the Team read model version comes from
  TeamGAgent's `state_version`, not a downstream counter.
- The Member events drive the Team aggregate, not the other way around. This
  matches the existing pattern (e.g. service binding events drive service
  read models).

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
| `lifecycle_stage` | TeamGAgent state (`active` / `archived`) |
| `member_count` | TeamGAgent state (aggregate from member events) |
| `created_at` | TeamGAgent state |
| `updated_at` | TeamGAgent state version timestamp |

Cut from #468 proposal:

| Field | Reason |
|---|---|
| `pendingApprovalCount` | No team-level approval domain exists today. The only approval concept (`ToolApprovalMiddleware` / `HumanApprovalResolution`) is run-scoped, not team-scoped. Adding a count without a queue is fictional. |
| `healthStatus` | Health is undefined for a Team. No domain rules exist for what makes a team "healthy" / "degraded". Adding the field invents semantics. |
| `lastActiveAt` | Ambiguous (last member edit? last run? last bind?). Defer to a later ADR after the activity domain is defined. |
| `lastRunAt` | Same as above. Run is member-scoped; rolling up to team-level is a derived view that needs explicit definition. |

These fields can be re-introduced via separate ADRs once their domains are
defined. Not shipping them in v1 is "删除优先" applied to read-model schemas.

### Q5. Does this ADR introduce a `User` aggregate?

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
- `team` lifecycle: `active` / `archived`. Archived teams remain queryable but
  cannot accept new member assignments.
- A member's `team_id` is a field of `StudioMemberState` (per ADR-0016
  §Required Backend Contract). It is nullable — members can exist outside any
  team.
- `TeamGAgent` does **not** own member identity. Member identity remains
  owned by `StudioMemberGAgent`.
- `TeamGAgent.member_roster` is an aggregate projected from committed
  `StudioMemberAssignedToTeamEvent` / `StudioMemberUnassignedFromTeamEvent`.
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
- `team_id` is added to `StudioMemberState` as an optional field; absent value
  means "unassigned".
- Existing member endpoints stay; `teamId?` is added to create / patch payloads.
- The published service identity rules from ADR-0016 are unchanged.

### 3. Authoritative ownership

- "Member belongs to team X": owned by `StudioMemberGAgent`, persisted in
  `StudioMemberState.team_id`, committed via `StudioMemberAssignedToTeamEvent`
  / `StudioMemberUnassignedFromTeamEvent`.
- "Team metadata (name, description, lifecycle)": owned by `StudioTeamGAgent`.
- "Team aggregate facts (`member_count`, etc.)": owned by `StudioTeamGAgent`,
  maintained by consuming committed Member events.
- The Team read model has no fact that is not authoritative in `StudioTeamGAgent`.

### 4. Scope

- `Scope` is not an actor.
- "List teams in scope" is a read-model filter on `team_current_state_document.scope_id`.
- No `ScopeAggregate` is introduced; cross-scope aggregation is not a goal.

### 5. Read model discipline

- Team Directory read model v1 fields are exactly the list in Q4 ("Keep").
- Adding any field requires a new ADR or an amendment that names the domain
  owner, the committed event source, and the version source.
- No query-time stitching, no event replay in the query path. Standard
  projection pipeline only.

### 6. Approval, health, activity

- No team-level approval inbox is defined by this ADR. `pendingApprovalCount`
  is **not** on the contract.
- No team-level health semantics are defined. `healthStatus` is **not** on the
  contract.
- Team-level activity (`lastActiveAt`, `lastRunAt`) is **not** on the contract.
  These require their own domain ADR before becoming read-model fields.

### 7. User identity

- This ADR does not introduce a User aggregate, RBAC, or human team
  membership semantics.
- Future work in that area must compose on top of this ADR's `Team` model, not
  replace it.

## Required Backend Contract

### Proto additions

`agents/Aevatar.GAgents.StudioTeam/studio_team_messages.proto` (new file):

```proto
message StudioTeamState {
  string team_id = 1;
  string scope_id = 2;
  string display_name = 3;
  string description = 4;
  string lifecycle_stage = 5;          // wire-stable: "active" | "archived"
  int32 member_count = 6;              // aggregate from member events
  google.protobuf.Timestamp created_at = 7;
  google.protobuf.Timestamp updated_at = 8;
  // Reserved for v2: last_activity_at, etc.
}

message StudioTeamCreatedEvent {
  string team_id = 1;
  string scope_id = 2;
  string display_name = 3;
  string description = 4;
  google.protobuf.Timestamp created_at = 5;
}

message StudioTeamUpdatedEvent {
  string team_id = 1;
  string scope_id = 2;
  string display_name = 3;       // present iff changed
  string description = 4;        // present iff changed
  google.protobuf.Timestamp updated_at = 5;
}

message StudioTeamArchivedEvent {
  string team_id = 1;
  string scope_id = 2;
  google.protobuf.Timestamp archived_at = 3;
}

message StudioTeamMemberRosterChangedEvent {
  string team_id = 1;
  string scope_id = 2;
  string member_id = 3;
  string change_kind = 4;        // wire-stable: "joined" | "left"
  int32 member_count = 5;        // aggregate after the change
  google.protobuf.Timestamp changed_at = 6;
}
```

`agents/Aevatar.GAgents.StudioMember/studio_member_messages.proto` (extend
existing):

```proto
// Add to StudioMemberState:
string team_id = 50;       // optional; empty string means unassigned

// New events:
message StudioMemberAssignedToTeamEvent {
  string member_id = 1;
  string scope_id = 2;
  string team_id = 3;
  google.protobuf.Timestamp assigned_at = 4;
}

message StudioMemberUnassignedFromTeamEvent {
  string member_id = 1;
  string scope_id = 2;
  string previous_team_id = 3;
  google.protobuf.Timestamp unassigned_at = 4;
}
```

### Read model

`src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto`:

```proto
message StudioTeamCurrentStateDocument {
  // Metadata
  string id = 1;
  string actor_id = 2;
  int64 state_version = 3;
  string last_event_id = 4;
  google.protobuf.Timestamp updated_at = 5;

  // Identity
  string team_id = 20;
  string scope_id = 21;

  // Display
  string display_name = 22;
  string description = 23;

  // Lifecycle
  string lifecycle_stage = 24;        // wire-stable
  google.protobuf.Timestamp created_at = 25;

  // Aggregate
  int32 member_count = 30;
}
```

Extend `StudioMemberCurrentStateDocument` with `string team_id = 50`.

### HTTP endpoints

Team CRUD:

- `POST   /api/scopes/{scopeId}/teams`            — create team
- `GET    /api/scopes/{scopeId}/teams`            — list teams in scope
- `GET    /api/scopes/{scopeId}/teams/{teamId}`   — read team
- `PATCH  /api/scopes/{scopeId}/teams/{teamId}`   — update display name / description
- `POST   /api/scopes/{scopeId}/teams/{teamId}/archive` — archive (soft retire)

Team -> Member listing (read model query, filtered):

- `GET    /api/scopes/{scopeId}/teams/{teamId}/members` — list members assigned to this team

Member-side extensions (do not break existing endpoints):

- `POST   /api/scopes/{scopeId}/members` — body accepts optional `teamId`
- `PATCH  /api/scopes/{scopeId}/members/{memberId}` — body accepts `teamId` (string or null)

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
- v1 ships without `pendingApprovalCount`, `healthStatus`, `lastActiveAt`,
  `lastRunAt`. Frontend Team Directory cards must be designed without those
  fields, or those fields must be added in follow-up ADRs *with* domain
  definitions.

## Cutover Order

1. Land this ADR (proposed → accepted) and align on Q1–Q5.
2. Add `StudioTeam` proto messages and `StudioTeamGAgent` actor with
   `Created / Updated / Archived` events.
3. Extend `StudioMemberState` with `team_id` and add
   `StudioMemberAssignedToTeamEvent` / `StudioMemberUnassignedFromTeamEvent`.
4. Wire `StudioTeamGAgent` to consume committed member assignment events and
   maintain `member_count` via `StudioTeamMemberRosterChangedEvent`.
5. Add `StudioTeamCurrentStateDocument` read model and the standard projector.
6. Extend member endpoints (`POST` / `PATCH`) to accept `teamId`.
7. Add team CRUD endpoints and `team -> members` listing endpoint.
8. Add `IStudioTeamQueryPort` / command dispatch contracts in the application
   layer.

Each step is gated by build + targeted tests + the relevant CI guards
(`projection_state_version_guard.sh`, `projection_route_mapping_guard.sh`,
`workflow_binding_boundary_guard.sh` where applicable).

## Non-Goals

This ADR does **not** define:

- A `User` aggregate or human identity model.
- Team-level RBAC, roles within a team, or invite flows.
- Approval workflows at the team level (`pendingApprovalCount` is explicitly
  excluded).
- Team health, activity, or run rollup semantics (`healthStatus`,
  `lastActiveAt`, `lastRunAt` are excluded from v1).
- `Scope` as a first-class actor.
- Frontend visual layout for the Team Directory.
- Migration of existing scope-implicit teams (in current state, "the team is
  the scope" per ADR-0016; a future migration ADR can decide whether to
  auto-create a default team per scope).
- Hierarchical / nested teams.

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
