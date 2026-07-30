---
title: "Team Automation Canonical Query Repair"
status: approved
owner: eanzhao
---

# Team Automation Canonical Query Repair

## Goal

Make the Team Automations surface enter a canonical member-owned resource and
query its projected schedules. The repair must preserve the distinct meanings
of `memberId`, `workflowId`, and `publishedServiceId`, use the existing
owner-aware schedule read path, and avoid restoring the removed nested CRUD
API or inventing a Team-wide aggregate.

This design fixes the missing query and the stale query transport. It does not
redesign or claim to repair the remaining Team automation mutation contracts.

## Verified Root Cause

The failure is an incomplete API and route migration, not a TanStack Query
runtime defect.

The schedule projection originally supported Team ownership through
`GET /api/schedules` with scoped owner fields. A later frontend migration moved
`teamAutomationApi` to nested Studio member URLs. The backend subsequently
removed the nested list, detail, mutation, and action endpoints and retained
only:

```text
POST /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/preflight
```

The current `TeamAutomationsTab` nevertheless calls the stale nested list URL.
It also enables that query only when the current path already contains a valid
`memberId`. On the Team-level Automations route, `route.memberId` is empty, so
the query is disabled even when the Team read model has already identified an
eligible selected member. The UI then renders that member and enables creation,
which makes the absence of the read request appear to be a query-library bug.

Removing the `enabled` condition alone is not a valid fix: it would issue a GET
to a route that no longer exists and return 404. Both resource canonicalization
and query transport must be corrected together.

## Canonical Resource Resolution

The Automations collection is owned by exactly one Team member. Its canonical
browser route is:

```text
/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations
```

The Team-level tab is a selector shell, not an alternative automation resource.
It resolves a canonical member in this order:

1. On a canonical member route, use the path `routeMemberId` only when that
   member exists in the current Team and is eligible for automation.
2. On the Team-level shell, use the one eligible member explicitly selected by
   the Team read model (`isSelectedMember`).
3. If the Team read model has no explicit selection and exactly one eligible
   member exists, use that member.
4. If multiple eligible members exist without an explicit selection, keep the
   selector visible and do not query until the user chooses one.

Once the Team-level shell resolves an owner, it replaces the location with the
member's canonical `automationsHref`. The canonical route then supplies the
query authority. A query-string `memberId`, `workflowId`, or `serviceId` may
participate in Team selection before canonicalization, but it never overrides
an existing path `routeMemberId` and is not forwarded as schedule authority.

An invalid or ineligible path member remains an unavailable member state. The
client must not fall back to another roster member, because doing so would make
the displayed URL and queried owner disagree.

## Query Architecture

The frontend product API keeps `TeamAutomationRoute` and `TeamAutomationView`
as the component-facing contract. Its list and detail reads move to the
canonical schedule endpoints:

```text
GET /api/schedules
  ?ownerKind=studio_member_automation
  &ownerScopeId={scopeId}
  &ownerTeamId={teamId}
  &ownerMemberId={memberId}

GET /api/schedules/{scheduleId}
  ?ownerKind=studio_member_automation
  &ownerScopeId={scopeId}
  &ownerTeamId={teamId}
  &ownerMemberId={memberId}
```

The existing schedule adapter owns normalization and encoding of the typed
`ScheduledDispatchOwner`. The Team automation API boundary adapts the returned
schedule current-state representation into `TeamAutomationView`; the page does
not know the generic transport DTO and does not parse wire fields itself.

Each decoded item must expose and exactly match the requested owner tuple:
`scopeId + teamId + memberId`. A missing or mismatched owner is a contract error,
not a row to display or silently discard. Detail reads apply the same check.
Pagination continues through the canonical cursor until exhausted, with the
existing repeated-cursor protection and total-count semantics.

This remains a pure read-model query. It does not prime a projection, replay an
event stream, read actor state, or create client-side owner registries.

## UI States And Errors

The canonical member route retains the existing loading, empty, error, pending,
and mutation-observation UI. A canonical query begins only after `scopeId`,
`teamId`, and the validated path member are present.

The unresolved Team selector shell presents eligible members without showing a
fabricated empty result. Selecting a member navigates to that member's canonical
route. A Team with no eligible member retains the existing unavailable/disabled
state.

Transport failures remain query errors and use the existing retry and refresh
behavior. Owner contract violations fail closed and surface as query errors;
they never leak or merge another member's schedules.

## Rejected Alternatives

### Enable the existing nested query

This changes the visible symptom but calls a deliberately removed backend
route. It preserves the contradictory API contract and fails with 404.

### Restore nested Studio lifecycle endpoints

The repository canon defines nested Studio automation HTTP as preflight-only.
Restoring list/detail would create a second read surface over the same schedule
facts and reopen the dual-path migration that caused the defect.

### Query or merge all Team members

A Team-wide fan-out would turn the browser into an aggregation layer, weaken
member ownership, and make pagination and authorization ambiguous. A stable
Team aggregate would require its own actor-owned read-model design and is not
needed for this member collection.

### Default to the first roster member

Roster order is not identity authority. With multiple eligible members, an
implicit first-row choice can query and mutate the wrong owner while the URL
still represents only the Team.

## Test Contract

Tests use deliberately distinct identities such as `m-alpha`, `wf-alpha`, and
`svc-alpha` so an accidental identity substitution cannot pass.

The API adapter tests prove:

- list and detail use `/api/schedules` with all four owner query fields;
- owner values are normalized and URL encoded;
- list pagination stays owner-scoped on every request;
- schedule state maps into `TeamAutomationView`;
- a missing or mismatched owner tuple is rejected.

The component and Team detail tests prove:

- an explicitly selected eligible member on the Team shell canonicalizes to the
  member route, after which the exact owner query runs;
- a sole eligible member canonicalizes and queries;
- multiple eligible members without a selection remain in the chooser and do
  not query;
- an invalid path member does not fall back or query;
- query-string member, workflow, and service candidates do not override a path
  member;
- the previous assertions that intentionally required zero requests from a
  resolvable Team shell are removed.

Focused tests run red before production changes and green afterward. Final
verification includes the focused API/component/integration suites, frontend
type checking, the complete frontend test suite and production build, plus
`test_stability_guards.sh` and `query_projection_priming_guard.sh`.

## Separate Lifecycle Command Debt

The current frontend create, update, reauthorize, pause, resume, run-now,
delete, and retry-revocation methods also target removed nested lifecycle URLs.
They cannot be mechanically redirected in this query repair because the rich
Studio preflight confirmation carries authorization evidence that the generic
schedule HTTP configuration does not currently expose with the same contract.

That mismatch requires a separate lifecycle design covering typed
authorization confirmation, admission receipts, owner-aware actions, deletion
replay, and credential revocation. This query repair neither restores the old
handlers nor weakens those semantics to make writes appear functional.

## Documentation Impact

The implementation follows the active canon in
`docs/canon/scheduled-skill-runners.md`; it does not change backend architecture
or introduce a new public contract. The canon therefore needs no semantic
change for this repair. The regression tests and this design record the frontend
alignment that was previously missing.
