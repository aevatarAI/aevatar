---
title: "Studio Team Accepted Receipt Semantics"
status: accepted
owner: eanzhao
---

# ADR-0028: Studio Team Accepted Receipt Semantics

## Context

ADR-0017 introduced Studio Team as a first-class aggregate and defined the
team CRUD HTTP surface. The current architecture rules require honest ACK
semantics: synchronous HTTP responses may only promise the stage that has
actually completed. A dispatch admission ACK must not be presented as a
committed write or readmodel-observed state.

Before this decision, team update/archive handlers dispatched the command and
then called `GetAsync` to read the team readmodel, returning `200 OK` plus a
snapshot. That mixed command admission with eventually consistent query
visibility and could imply completion that had not been observed through the
projection pipeline.

## Decision

This ADR extends ADR-0017 for Studio Team update/archive HTTP response
semantics.

`PATCH /api/scopes/{scopeId}/teams/{teamId}` returns `202 Accepted` with:

- a `Location: /api/scopes/{scopeId}/teams/{teamId}` header
- an accepted/no-change command receipt body

`POST /api/scopes/{scopeId}/teams/{teamId}/archive` returns `202 Accepted`
with:

- a `Location: /api/scopes/{scopeId}/teams/{teamId}` header
- an accepted command receipt body

These responses are dispatch admission receipts only. They do not mean the
target actor has committed the command, the committed event has been projected,
or a follow-up readmodel query will immediately observe the new state.

Callers that need visible team state must follow the `Location` resource and
treat it as an eventually consistent team query resource.

## Superseded ADR-0017 Sections

This ADR supersedes only the implicit ADR-0017 interpretation that team
update/archive HTTP handlers may return post-dispatch readmodel snapshots as
the command response.

ADR-0017 remains the source of record for Studio Team aggregate ownership,
schema, lifecycle, member/team semantics, and endpoint shape.

## Consequences

- `StudioTeamService.UpdateAsync` and `ArchiveAsync` return
  `StudioTeamCommandResponse` receipts from the command port.
- The application service does not call `GetAsync` after dispatching update or
  archive commands.
- Studio Team HTTP PATCH/archive endpoints return `202 Accepted` with
  `Location` instead of `200 OK` with a snapshot.
- Readmodel freshness remains a query concern and is not implied by the
  synchronous command response.
