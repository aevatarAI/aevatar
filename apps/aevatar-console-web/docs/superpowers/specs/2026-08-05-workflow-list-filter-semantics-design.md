# Workflow List Filter Semantics Design

## Status

Approved in conversation on 2026-08-05. This document records the product
semantics for aligning the Workflow and Activity list toolbars before the
frontend implementation changes.

This design supplements, but does not modify, the Workflow Activity vNext
design baseline. Real backend responses remain authoritative over prototype
labels and demonstration data.

## Mismatch

The UI currently makes the two list pages appear inconsistent: Activity has
categorical filters while Workflows exposes only search and refresh. Copying
Activity's filters into Workflows would still be wrong because the pages own
different resources and facts.

- Activity lists Runs. Its status and source are authoritative Run API fields.
- Workflows merges the scoped Workflow list with the scoped draft list.
  `draft` and `committed` describe frontend merge inputs, not two mutually
  exclusive product lifecycle states.
- A Workflow can have both a committed source and a draft. The frontend must
  not present `Committed` as the opposite of `Draft`.
- The current backend does not provide the published identity, last-Run
  outcome, or other facts required to implement the prototype's `Published`
  and `Failing` Workflow filters honestly.

The semantic owner of Workflow filtering is therefore the Workflow catalogue,
using only facts returned by its existing scoped Workflow and draft APIs. Run
status and source remain owned by Activity.

## Decision

Both pages use the same toolbar grammar:

- search on the left;
- resource-specific categorical filters on the right;
- refresh in the page header rather than mixed into the filter group;
- active search and filter values represented in the URL;
- an honest filtered-empty state with a clear-filters action.

Activity keeps its existing Run status and Run source filters.

Workflows adds one select with two options:

| Option | Product meaning | Evidence |
| --- | --- | --- |
| `All workflows` | Every row produced by the existing merge of scoped Workflows and scoped drafts | Successful results from the two existing list APIs |
| `Drafts` | Workflows whose exact `workflowId` is present in `studioApi.listWorkflowDrafts(scopeId)` | A real draft summary returned by that API |

The Workflows filter must not include `Committed`, `Published`, `Failing`,
`Ready`, or similar values until a real contract supplies an unambiguous,
user-facing fact for that option.

## Data And URL Behavior

The existing Workflow merge remains authoritative and unchanged:

1. Load the scoped committed Workflow summaries.
2. Load the scoped draft summaries.
3. Merge by exact `workflowId` without inferring identity from names, prefixes,
   route position, member IDs, or service IDs.
4. When `view=drafts`, retain only merged rows whose exact `workflowId` was
   returned by the draft API.
5. Apply the text search to the selected view and keep the existing updated-at
   ordering.

The Workflows URL uses:

- `q=<text>` for non-empty search text;
- `view=drafts` for the Drafts view;
- no `view` parameter for All workflows.

Activity adds its non-empty search text as `q=<text>` while preserving its
existing `status`, `origin`, `definition`, and `workflowFilter` parameters.
Empty values are omitted. Reloading, browser navigation, or returning to a
copied URL restores the same visible search and filters.

## Loading, Failure, And Empty States

- While either Workflow source is loading, retain the existing loading state.
- If the draft source fails, the Drafts option is disabled because the
  frontend cannot know which Workflows have drafts.
- A draft-source failure does not hide successfully returned scoped Workflow
  rows in All workflows.
- If the user was already viewing Drafts when the draft source fails, show an
  unavailable state with Retry rather than an empty result.
- A successful filtered result with zero rows shows `No matching workflows`
  and a `Clear filters` action.
- Clearing filters removes `q` and `view` while preserving the scoped route.
- No mock rows, localStorage state, timer results, or successful fallback may
  stand in for either API.

## Presentation And Accessibility

- Workflows and Activity use the same toolbar spacing, search width, select
  height, responsive wrapping, and mobile full-width controls.
- Selects retain visible accessible names through existing localized labels.
- The Drafts option is a normal menu option, not a status badge or lifecycle
  claim.
- Keyboard, touch, and screen-reader behavior continue through Ant Design's
  existing Input and Select controls.
- All new copy is added to both the `en-US` and `zh-CN` catalogues.

## Test Contract

Focused tests must prove that:

- Workflows filters Drafts by exact draft API membership while All workflows
  still contains committed-only and draft-backed rows.
- Workflows never renders unsupported `Committed`, `Published`, or `Failing`
  filter options.
- Workflows search and view values restore from and write to the URL.
- Activity search joins its existing URL-backed filters without dropping
  `status`, `origin`, `definition`, or `workflowFilter`.
- A failed draft source disables or makes the Drafts view unavailable instead
  of presenting a false empty state.
- Clearing filters restores the unfiltered list and scoped URL.
- Both locale catalogues contain every new message key.

Browser verification uses the running frontend with the real remote backend at
desktop, tablet, and mobile widths. If a remote endpoint is unavailable, the
unavailable state is recorded as a verification gap; test fixtures are not
used to fabricate browser evidence.

## Non-Goals

- No backend endpoint, DTO, projection, identity, or persistence change.
- No change to existing Workflow, Run, Settings, Studio, Team, member, login,
  callback, redirect, session, menu, or locale route behavior.
- No attempt to implement prototype-only Published or Failing filters.
- No identity conversion among `workflowId`, `memberId`,
  `definitionActorId`, or `publishedServiceId`.
