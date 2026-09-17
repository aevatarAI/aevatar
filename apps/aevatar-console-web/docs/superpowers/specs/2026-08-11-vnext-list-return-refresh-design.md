# vNext List Return Refresh Design

## Status

Approved for implementation on 2026-08-11 under the user's standing direction
to proceed with the recommended approach without another approval round.

Implementation branch: `fix/2026-08-11_refresh-vnext-lists-on-return`.

Base branch: `feat/2026-08-04_workflow-activity-vnext` at
`4ea36e70f904680b05f90a27b66cf7b85cb14bbd`.

## Problem

The shared React Query client keeps successful query data fresh for 30 seconds.
Workflow Activity vNext renders list and detail surfaces exclusively, so returning
from a detail surface unmounts the detail and remounts the list. During the
freshness window, React Query serves the cached list without issuing a request.

This leaves the Workflows catalogue stale after returning from the workflow
editor. The Activity runs list has the same lifecycle when returning from run
detail and can show an outdated run status for the same reason.

## Product Contract

- Entering the Workflows list refreshes its catalogue from the server.
- Entering the Activity list refreshes its runs from the server.
- The rule covers editor, new-workflow, run-detail, browser-back, and sidebar
  return paths without requiring each source surface to know the list query key.
- Existing filters and pagination query keys remain unchanged.
- Existing loading and error presentation remains unchanged.
- Other cached queries keep the shared 30-second freshness policy.

## Design

Set `refetchOnMount: 'always'` on the Workflows catalogue query and the Activity
runs query. This makes list ownership explicit: a list surface decides that its
server-backed collection must be revalidated whenever the surface is entered.

The alternative of setting `staleTime: 0` would depend on default remount
behavior and obscure the product intent. Invalidating from editor/detail
mutations would couple independent surfaces, miss no-mutation return paths, and
require every navigation path to cooperate.

## Verification

Use production-like test clients with `staleTime: 30_000` so the tests reproduce
the bug rather than passing because test data is immediately stale. Cover:

- Workflows list -> workflow editor -> Workflows list, asserting a second
  catalogue request.
- Activity list -> run detail -> Activity list, asserting a second runs request.

Run only the changed test files and affected frontend checks. The complete
frontend suite, typecheck, and production build remain delegated to GitHub CI
under the personal incremental frontend policy.
