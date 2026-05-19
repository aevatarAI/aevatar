---
title: 0024 — Studio team command ACK semantics
status: accepted
owner: liyingpei
---

# 0024 — Studio team command ACK semantics

## Status

Accepted. Supersedes ADR [0017](0017-studio-team-first-class-aggregate.md) for the synchronous success response of Studio team update/archive commands.

## Context

ADR-0017 defines Studio team as a first-class aggregate and lists the HTTP surfaces:

- `PATCH /api/scopes/{scopeId}/teams/{teamId}`
- `POST /api/scopes/{scopeId}/teams/{teamId}/archive`

The previous application flow dispatched a team command and immediately reread the eventually consistent team readmodel, returning `200 OK + StudioTeamSummaryResponse`. That mixed three different stages:

1. command intent accepted/dispatched,
2. actor-authoritative state transition,
3. readmodel materialization.

A successful dispatch does not guarantee that the readmodel has observed the transition. Returning a team summary from the write response could therefore expose stale post-state or map readmodel lag to a false not-found.

## Decision

Studio team update/archive endpoints return an honest command ACK receipt instead of a post-state summary:

| Endpoint | Synchronous success response |
|---|---|
| `PATCH /api/scopes/{scopeId}/teams/{teamId}` | `202 Accepted + StudioTeamCommandAcceptedResponse` |
| `POST /api/scopes/{scopeId}/teams/{teamId}/archive` | `202 Accepted + StudioTeamCommandAcceptedResponse` |

`StudioTeamCommandAcceptedResponse` contains:

- `scopeId` — normalized scope id for the command target.
- `teamId` — normalized team id for the command target.
- `commandId` — the dispatched `EventEnvelope.Id` for state-changing command paths; `null` for no-op PATCH that dispatches no envelope.
- `ackStage` — currently only `accepted`. This is an ACK-stage literal, not a command lifecycle/status resource.
- `acceptedAtUtc` — acceptance/envelope creation timestamp. It does not imply actor commit or readmodel materialization.

The HTTP `Location` header points to the existing team readmodel query URI:

```text
/api/scopes/{escaped scopeId}/teams/{escaped teamId}
```

That URI remains an eventually consistent readmodel read. Clients that need post-state must explicitly issue the GET and must treat it as readmodel-current, not as a commit/readiness guarantee.

The write response does not include readmodel post-state fields such as `displayName`, `description`, `lifecycleStage`, `memberCount`, `createdAt`, or `updatedAt`.

No command status endpoint, readiness endpoint, bounded polling, actor reply/result protocol, or generic async operation framework is introduced by this decision.

## Consequences

- PATCH/archive no longer use stale or missing readmodels to determine write success.
- API clients must migrate from `200 OK + StudioTeamSummaryResponse` to `202 Accepted + StudioTeamCommandAcceptedResponse` for team update/archive.
- Synchronous not-found for update/archive is only valid if it comes from an authoritative command path. It must not be inferred from post-dispatch readmodel lag.
- `GET /api/scopes/{scopeId}/teams/{teamId}` remains the explicit readmodel query for materialized team state.
