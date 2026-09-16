# Workflow vNext Home Design

## Product Decision

Updated on 2026-09-14: the console opens the current account's Workflows catalogue.
A fixed deployment scope incorrectly sends every account to the same workspace.
`/workflows` is the unscoped home resolver; the existing authenticated account API
owns the scope used to open the catalogue.

This decision supersedes the earlier fixed-scope home and the original vNext
preview-only entry restriction. Teams and members are not an intermediate home.

## Semantic Ownership

`CONSOLE_HOME_ROUTE` is `/workflows` and remains the shared owner of default
navigation. Route configuration, login/callback fallback, protected-route
fallback, and not-found recovery use it.

`WorkflowHomePage` refreshes `GET /api/auth/me` through `studioApi.getAuthSession`
and the existing account query key. It reads the response's explicit `scopeId`,
then replaces the URL with
`/scopes/:scopeId/workflow-activity-vnext/workflows` using the scoped route builder.
The account subject, browser user ID, cached account, and deployment constants
must never substitute for an authoritative scope.

The `/api/auth/me` scope contract was checked against `feature/integrate` commit
`0e3f7cdac58c55d6fe652308e8f63f6fa1373e97`. This change needs no backend endpoint
or contract modification.

## Navigation Behavior

| Entry | Result |
| --- | --- |
| `/`, `/overview`, `/scopes` | Redirect to `/workflows` |
| Successful login without a safe return target | Open `/workflows`, then resolve the account scope |
| `/workflows` | Refresh account; replace URL with the scoped Workflows catalogue |
| Login initiated from a safe protected deep link | Return to that original deep link, preserving query and fragment |
| `/scopes/:scopeId/teams` | Existing scoped Teams page |

The home resolver uses the existing vNext fullscreen presentation and page
loading component. It does not duplicate the catalogue or require a Team or
member to open Workflows. `/runtime/workflows` remains its own runtime surface;
`/workflows` no longer aliases that route, including in login return sanitization.

## Failure And Security Behavior

- While the account refresh is pending, show loading without navigating from
  cached identity. A failed refresh must not use the stale cached scope.
- A failed request shows an unavailable state and Retry, which repeats the API
  read. A successful response without a scope shows a distinct access message
  and Retry; it does not create an implicit workspace.
- When server authentication is enabled and the account or session is explicitly
  unauthenticated, offer the existing login route without using its scope value.
- When server authentication is disabled, an explicitly returned scope is usable.
- External, protocol-relative, login, and callback return targets still pass
  through the existing `sanitizeReturnTo` rules. Safe internal links remain intact.

## Verification

Route and auth contract tests protect the unscoped home, its aliases, scoped deep
links, and existing Team routes. Home integration tests exercise a deferred API
response, distinct/encoded scope IDs, stale-cache failure and retry, missing scope,
and sign-in recovery using the real QueryClient and scope/route helpers.

Run focused Jest tests and changed-file Biome checks. GitHub CI owns the complete
frontend suite, typecheck, and production build.
