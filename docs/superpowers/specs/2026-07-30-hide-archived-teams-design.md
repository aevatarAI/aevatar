# Hide Archived Teams Design

## Status

Approved for implementation on July 30, 2026.

## Problem

The Team roster read model correctly returns both active and archived Team
summaries. The Team home page currently builds previews from every returned
summary, so an archived Team remains visible as a drafting card and is included
in the summary counts. The page may also request runtime data for that Team's
entry member even though the Team is no longer part of the active roster.

The API response is an honest read-model result. Hiding archived Teams is a
product visibility rule for the Team collection page, not a reason to erase the
archived lifecycle fact from the shared API client or read-model contract.

## Decision

The Team home page will derive its visible Team collection immediately after it
merges the server roster with pending just-created Team summaries. A Team whose
normalized lifecycle stage is `archived` will be excluded from that collection.
Active and unknown lifecycle stages remain visible so an unexpected or newly
introduced value does not silently make a live Team unreachable.

Every downstream home-page concern will consume the same visible collection:

- entry-member runtime query selection;
- Team preview construction;
- card and compact-list rendering;
- total, attention-needed, and stable-run summary counts;
- card/list mode selection and the empty-roster state.

The shared `studioApi.listTeams` decoder will continue returning archived Team
summaries. Direct Team detail routes and other consumers remain able to read the
archived Team and enforce their own lifecycle-specific behavior.

## Pending Roster Interaction

The existing pending-roster merge remains responsible only for keeping a newly
created Team visible while the roster projection catches up. When the server
returns the same Team ID, the merge gives the server summary precedence by
excluding the duplicate pending summary. The following React effect then clears
the synchronized pending entry from session storage. Therefore, an older
pending active summary cannot reintroduce a Team after the authoritative roster
reports it as archived.

## Removed Behavior

The Team preview builder's archived-to-draft presentation branch becomes
unreachable once visibility is applied at the collection boundary. It will be
deleted instead of retained as dead compatibility behavior.

## Verification

A focused Team home-page regression test will return one active Team and one
archived Team with distinct Team, member, and published-service identities. It
must prove that:

1. The active Team remains visible.
2. The archived Team is absent from the rendered roster.
3. The summary counts include only the active Team.
4. Runtime data is never requested for the archived Team's service.

A second focused case will return only archived Teams and verify that the page
uses the existing empty-roster state without requesting archived runtime data.

The final verification set includes the focused Team home-page test suite,
frontend type checking, the frontend production build, and the repository's
test-stability guard because a frontend test is modified.
