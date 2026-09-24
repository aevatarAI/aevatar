# NyxID Service Access Review Return Route Design

## Problem

The Workflow Activity vNext Account settings surface starts a NyxID service
access review with the correct scoped return location:

```text
/scopes/:scopeId/workflow-activity-vnext/settings?section=account
```

The shared NyxID authentication client then replaces that caller-provided route
with the legacy Account settings URL:

```text
/settings?section=account
```

After the user approves or denies access on NyxID, `/auth/callback` therefore
returns to a different product surface from the one that initiated the review.
The defect is not only an incorrect constant. A shared protocol adapter owns a
concrete product route, and the `serviceAccessReview` option contract allows the
caller to omit the return target even though the callback flow requires it.

## Semantic Decision

The product surface that initiates an authorization flow owns its return route.
The shared NyxID authentication layer owns OAuth mechanics: it validates the
route as a safe same-origin target, stores it with the pending PKCE state, and
restores it on success or failure. It does not select a Settings implementation
or export a Settings route.

This is an ownership, contract, and runtime correction:

- **Ownership:** legacy Settings and Workflow Activity vNext each construct
  their own canonical Account settings route.
- **Contract:** a service-access-review request must provide its return route at
  compile time.
- **Runtime:** success, denial, finalization failure, retry, and back navigation
  preserve the sanitized pending route instead of substituting a legacy route.

## Goals

- Return a review initiated at Workflow Activity vNext to that same scoped
  Account settings surface.
- Remove all concrete Account settings route knowledge from shared auth code.
- Make an omitted review return route a TypeScript error.
- Keep NyxID review authorization on `prompt=consent` regardless of caller
  options.
- Preserve the initiating route through successful, denied, failed, and retried
  callback paths.
- Prevent external, protocol-relative, login, and callback targets from entering
  callback navigation.
- Give vNext Settings one canonical route builder based on `scopeId` and section
  identity instead of deriving URLs from the current browser pathname.
- Preserve the legacy Settings review flow at its own canonical legacy route.

## Non-Goals

- Remove or redirect the legacy `/settings` product surface.
- Change NyxID OAuth endpoints, PKCE behavior, backend finalization, service
  access semantics, or session persistence.
- Introduce a generic route registry into the authentication layer.
- Add compatibility aliases between legacy Settings and Workflow Activity
  vNext Settings.
- Redesign the Account settings UI.

## Selected Design

### Caller-owned authentication options

Replace the permissive `LoginRedirectOptions` interface with a discriminated
union. The sign-in variant keeps its optional `returnTo` and optional prompt.
The review variant requires both the `serviceAccessReview` discriminant and a
`returnTo` string. It does not expose a prompt override because review always
uses `consent`:

```ts
type LoginRedirectOptions =
  | {
      readonly flow?: 'signIn';
      readonly returnTo?: string;
      readonly prompt?: 'none' | 'consent' | 'login';
    }
  | {
      readonly flow: 'serviceAccessReview';
      readonly returnTo: string;
      readonly prompt?: never;
    };
```

`loginWithRedirect()` continues to support an omitted options object as an
ordinary sign-in. For either variant it sanitizes the supplied route before
writing pending state. Review still forces `prompt=consent` when constructing
the NyxID authorization URL.

The shared `SERVICE_ACCESS_REVIEW_RETURN_TO` constant and the review-specific
route override are deleted. Pending state remains the single correlation record
for `flow` and `returnTo`.

### Canonical Workflow Activity Settings routes

Add `buildWorkflowActivitySettingsHref(scopeId, section)` to the existing
Workflow Activity vNext navigation module. The builder encodes `scopeId`, uses
the canonical `/scopes/:scopeId/workflow-activity-vnext/settings` base, omits the
query for the default `ai` section, and adds `?section=account` or
`?section=advanced` for the other supported settings sections.

`SettingsPage` uses this builder for its settings tabs and for the Account
panel's review destination. It does not build these routes from
`location.pathname`; the typed `scopeId` prop is the route identity source.

Rename the Account panel prop from the transport-shaped `returnTo` to
`accountSettingsHref`. The name states what the caller is providing. The panel
uses this href both to restart an invalid session and to start a service access
review.

### Legacy Settings ownership

The legacy Account settings component defines its own local canonical Account
settings href, `/settings?section=account`, and passes it explicitly to the auth
client. This route is a property of that product surface, not a shared auth
default. No legacy route is exported from the authentication module.

### Callback navigation

The auth client sanitizes a pending return target both when it is initially
persisted and when it is restored. Restoring is treated as a trust boundary
because local storage may contain stale or manually modified data.

`/auth/callback` also sanitizes any structured error's `returnTo` before using
it for retry or back navigation. A review error without a valid restored route
falls back to `CONSOLE_HOME_ROUTE`, never to legacy Settings. Ordinary sign-in
errors retain the existing `/login` fallback when no structured return target
exists.

The callback retry handler branches on the discriminant and constructs a valid
option variant explicitly. Review retries pass the preserved route and rely on
the auth client to force consent. Sign-in retries retain the existing prompt
behavior needed for missing required access. This avoids weakening the union
with a dynamically mixed option object.

## Data Flow

1. `SettingsPage` builds the canonical scoped Account href from `scopeId`.
2. `AccountPanel` calls `loginWithRedirect` with
   `flow: 'serviceAccessReview'` and that explicit href.
3. `NyxIDAuthClient` sanitizes the href and persists it with the PKCE verifier,
   OAuth state, and flow.
4. The browser navigates to NyxID with `prompt=consent`.
5. NyxID returns to `/auth/callback` with the matching state.
6. The auth client loads the pending record, sanitizes the stored href again,
   finalizes the session when applicable, and returns or throws with that href.
7. The callback page uses the same href for successful replacement, error back
   navigation, and a newly persisted retry request.

For a vNext review, the resulting destination is always:

```text
/scopes/:scopeId/workflow-activity-vnext/settings?section=account
```

unless the stored target fails safety validation, in which case it is
`CONSOLE_HOME_ROUTE`.

## Error And Safety Rules

- OAuth denial removes the matching pending record and reports the sanitized
  initiating route in `NyxIDAuthCallbackError`.
- Backend finalization failure removes pending state and reports the same
  sanitized route.
- A retry of either error starts a new PKCE transaction with the same route.
- A missing, empty, external, protocol-relative, `/login`, or `/auth/callback`
  review target resolves to `CONSOLE_HOME_ROUTE`.
- Callback rendering never accepts a route merely because it starts with `/`;
  it uses the shared return-target sanitizer.
- No error path fabricates `/settings?section=account` when the pending state
  did not contain that value.

## Testing

Focused frontend tests cover the ownership and round-trip contract:

- Navigation tests prove the settings builder encodes the scope and emits the
  default, Account, and Advanced canonical URLs.
- `SettingsPage` or Account panel tests prove the vNext Account action receives
  and submits the canonical scoped Account href.
- Legacy Settings tests prove its Account action explicitly submits its local
  legacy href.
- Auth client tests prove the review href is sanitized and stored in pending
  state, `prompt=consent` is forced, and callback success returns the stored
  scoped href.
- Auth client tests prove OAuth denial and backend finalization failures carry
  the stored scoped href for back navigation and retry.
- Callback tests prove retry preserves the scoped href and unsafe structured
  error targets resolve to `CONSOLE_HOME_ROUTE`.
- The exported discriminated union makes `serviceAccessReview` without
  `returnTo` a compile-time error. All affected call sites are updated to one
  valid union variant; GitHub CI owns the full-project TypeScript verification.

Validation follows the repository's frontend incremental policy: run only the
changed tests, related tests, and changed-file static checks, plus
`bash tools/ci/test_stability_guards.sh` because tests change. Full frontend
typecheck, test suite, and production build remain delegated to GitHub CI.

## Rollout

No backend, persistent-data, or deployment configuration migration is needed.
Existing pending local-storage records are read through the same schema and are
sanitized on restoration. A legacy review started before deployment retains its
stored legacy route; new reviews return to the product surface that initiated
them. The change is complete when focused validation passes and the pull request
targets `feat/2026-08-04_workflow-activity-vnext`.
