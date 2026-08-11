# Workflow Catalogue Frontend Integration Design

## Goal

Migrate the Workflow Activity vNext catalogue from two client-owned source queries to the backend-owned scope workflow catalogue contract.

The page must stop joining draft and committed rows, stop filtering or sorting the complete catalogue in browser memory, and stop treating workflow, member, published service, deployment, or actor identities as interchangeable.

## Backend Contract

The frontend will call:

```http
GET /api/scopes/{scopeId}/workflow-catalogue?view=all|drafts&query={query}&cursor={cursor}&take=50
```

The API client will model and decode the complete response:

- `items`
- `nextPageToken`
- `freshness`
- `search`
- row source facts
- row capabilities
- committed workflow facts

The API belongs in `scopesApi` because its route, authorization boundary, and returned runtime facts are scope-owned. The query accepts an `AbortSignal` so React Query can cancel superseded searches and view changes.

## Page Semantics

The catalogue selector will contain only the backend-supported views:

- `All workflows` maps to `view=all`.
- `Drafts` maps to `view=drafts`.

The product meaning of `Drafts` is unpublished workflow drafts. The current
backend `view=drafts` transport is broader: it returns every row with a draft
source, including published workflows that retain an editable draft. Until the
backend contract exposes the narrower product view, the page excludes rows
with an active committed revision from the Drafts presentation. This adapter
must preserve the backend cursor even when every row on a loaded page is
excluded, so later unpublished drafts remain reachable through pagination.

The old `Active workflows` and `Archived` catalogue filters will be removed. Existing URL values outside `all|drafts`, including `active` and `archived`, will resolve to `all`. Archived workflows remain visible in `All workflows` and retain their row-level Archived status.

Search text remains visible immediately and is written to the URL. A short debounce controls the server query. The query key contains `scopeId`, backend view, and debounced search text. React Query cancels the obsolete request when any of those values changes.

The page will use cursor-based infinite querying with `take=50`. It will render returned pages in backend order and expose a Load more command while `nextPageToken` is present. The client may flatten loaded pages for rendering. It must not join or sort catalogue rows, and the only permitted row filter is the Drafts semantic adapter described above.

## Row Mapping And Actions

The page will map each backend row directly to its presentation model:

- `workflowId` remains the only workflow route and Activity query identity.
- `name`, `description`, and `updatedAtUtc` come directly from the catalogue row.
- runtime fields come only from the optional `committed` object.
- the ownership label remains the scope workspace label because the catalogue response does not expose a directory owner.

`Open`, `Activity`, `Rename`, and `Delete` availability will come from the corresponding backend capability fields. Unavailable primary actions will be disabled rather than inferred from source flags. Rename and Delete menu entries will be present only when their capabilities are available.

Archive is not part of the new catalogue capability contract. The existing archive eligibility policy remains based on committed deployment facts and deployment status. The catalogue does not expose the published service identity needed by the archive command, so confirmation resolves `publishedServiceId`, `serviceAppId`, `serviceNamespace`, and the authoritative `deploymentId` from the workflow detail read model. The frontend must not parse `serviceKey` or substitute `workflowId` for a service identity. Archive observation searches the catalogue in `view=all` and follows every returned cursor until the exact `workflowId` is found or the result is exhausted. After archive observation succeeds, the catalogue query is refreshed.

Rename and Delete continue to use their existing command paths. After successful materialization or deletion, they refresh the catalogue query instead of refreshing the removed draft list query.

## Loading And Failure States

The two-source partial-failure model will be deleted. The first catalogue page has one loading state, one failure state, and one retry action. A manual catalogue refresh reuses the table loading state while the authoritative page is refetched.

Loading another page keeps existing rows visible and disables the Load more command while the request is pending; it must not switch the table back to its loading skeleton. A next-page failure keeps existing rows visible, reports the failure beside the pagination action, and allows the same command to retry.

An empty `items` result is authoritative for the selected backend view and search query.

## Testing

Implementation will follow test-first development.

Focused API tests will verify:

- exact route and query parameter encoding
- `all` and `drafts` view values
- cursor and `take` propagation
- AbortSignal propagation
- response decoding, including nullable committed facts and capabilities
- distinct published service identity from the workflow detail contract

Focused Workflow Activity tests will verify:

- one catalogue request replaces the draft and committed list requests
- only All workflows and Drafts are offered
- URL restoration maps unsupported legacy values to All workflows
- search is debounced and superseded requests are cancelable
- Drafts is sent to the server instead of applied in browser memory
- backend row order is preserved
- Load more uses `nextPageToken` and appends the next page
- a failed next page preserves loaded rows and retries the same cursor
- row actions honor backend capabilities
- archive resolves its distinct service identity, observes across catalogue pages, and refreshes the catalogue query
- workflow routes never use member, service, deployment, or actor identities

Local validation will run only directly related Jest files and changed-file static checks. Full frontend tests, typecheck, and production build remain delegated to GitHub CI.

## Non-Goals

- Adding frontend-only Active or Archived catalogue filters
- Downloading every page to recreate those filters
- Changing backend catalogue semantics
- Changing archive, rename, delete, or workflow publication command contracts
- Refactoring unrelated Workflow Activity pages
