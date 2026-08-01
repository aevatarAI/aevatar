---
title: "Studio Team Duplicate Reconciliation Runbook"
status: active
owner: studio
---

# Studio Team Duplicate Reconciliation Runbook

## Purpose and boundary

This runbook defines the production reconciliation for Studio Teams created by
issue #3101. Before the fix, `aevatar_create_team` could commit a Team and then
return `tool_outcome_unknown` to Chat. A retry omitted `team_id`, so the command
service minted another random Team identity.

The code fix prevents new multiplication in two independent ways:

1. the Studio provider emits a typed receipt whose result preserves the
   authoritative `scope_id`, `team_id`, `display_name`, `team_url`, and full
   result payload;
2. when `team_id` is omitted, the Chat tool derives the same Team identity from
   `scope_id + display_name` on every retry. Explicit `team_id` remains the
   opt-in path for intentionally distinct Teams with the same display name.

The fix does not merge, delete, or archive Teams already created by the
incident. Reconciliation is an operator-owned command procedure after the fix
is deployed. It must not run in a query path, projection, application startup,
or Chat tool.

## Preconditions

Record all of the following in the incident/change record before mutation:

1. the deployed commit containing both the typed Studio Team receipt and stable
   omitted-`team_id` derivation;
2. the exact affected `scope_id` and incident time window from private
   production correlation data;
3. a read-only Team inventory from
   `GET /api/scopes/{scopeId}/teams`, including every candidate's exact
   `team_id`, lifecycle, `created_at`, `updated_at`, and `member_count`;
4. per-candidate member inventory from
   `GET /api/scopes/{scopeId}/teams/{teamId}/members` and detail from
   `GET /api/scopes/{scopeId}/teams/{teamId}`;
5. a reviewed keep/archive manifest and rollback/escalation owner.

Use distinct synthetic examples in tickets and tests, such as
`scope-alpha`, `t-canonical`, and `t-duplicate-1`. Do not place production
scope, member, workflow, published-service, user, or credential values in this
repository.

## Candidate classification

Exact display-name equality inside the affected scope and incident window is
only a candidate signal; it is not enough to archive a Team. For every
candidate, verify the exact Team identity and classify usage from normal
read-model surfaces:

- lifecycle and entry member;
- member roster and member workflow ownership;
- workflow schedules and any other references to that exact `team_id`;
- audit/correlation evidence linking the create command to the incident.

Choose the retained Team using these rules:

1. If exactly one candidate owns the intended member, workflow, schedule, or
   entry member, retain that Team.
2. If every candidate is active and empty, retain the earliest `created_at`;
   break an exact timestamp tie by ordinal `team_id` so the decision is
   deterministic.
3. If more than one candidate owns business resources, if references disagree,
   or if any read is missing/stale/ambiguous, stop. Do not merge identities or
   guess from prefixes, display names, route order, or creation adjacency.

The manifest must list each exact `team_id`, the evidence checked, the retained
Team, the proposed archive set, and reviewer approval. A Team outside the
manifest is never a mutation target.

## Approved mutation

Use the existing command endpoint for each approved duplicate:

```text
POST /api/scopes/{scopeId}/teams/{teamId}/archive
```

Archive only; do not hard-delete actor state, committed events, or read-model
documents. The Team actor owns lifecycle authority and publishes the committed
fact that projections materialize. Send commands serially, record each command
receipt/correlation ID, and stop on any rejection, timeout, ambiguous receipt,
or manifest mismatch.

Archival is irreversible in the Team lifecycle. If an approved candidate gains
a member, entry member, workflow, schedule, or newer update after the dry run,
do not archive it; regenerate and re-review the manifest.

## Verification

After projection visibility catches up, perform read-only verification:

1. the retained Team remains active and preserves its exact members, entry
   member, workflows, and schedules;
2. every approved duplicate is archived and no unapproved Team changed;
3. the Studio Team list no longer presents active incident duplicates;
4. a new Chat create request without `team_id`, repeated with different request
   and tool-call IDs, returns the same `team_id` and a typed `Success` receipt;
5. Chat can pass that confirmed `team_id` to workflow and schedule provisioning
   without another Team create;
6. logs contain no new `tool_outcome_unknown` result for
   `aevatar_create_team` and no unexpected Team actor conflict.

Store before/after counts, the manifest checksum, command receipts, read-only
verification results, deployed commit, and reviewer sign-off with the incident
record. Production reconciliation is incomplete until this evidence exists.
