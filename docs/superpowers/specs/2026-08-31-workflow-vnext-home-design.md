# Workflow vNext Home Design

## Product Decision

The console currently treats the unscoped Teams entry as home, but this deployment expects the fixed-scope Workflow Activity vNext workflow catalogue to be home. The mismatch is an ownership and runtime mismatch: default navigation points to the old Teams resolver even though the intended first product surface is Workflow Activity vNext.

The fixed console home is:

`/scopes/ccb108c4-dcb3-473a-a0f7-e9859bb2f2a0/workflow-activity-vnext/workflows`

## Scope

- Make the shared console fallback route resolve to the fixed Workflow Activity vNext catalogue.
- Redirect `/`, `/overview`, and `/scopes` to that shared home route.
- Preserve explicit safe post-login deep links. A user who was sent to login from another protected page still returns to that page.
- Keep the existing Teams page implementation and canonical scoped Team routes unchanged.
- Do not add a duplicate Workflow Activity page or copy its UI into the old home page.

## Semantic Ownership

`CONSOLE_HOME_ROUTE` remains the single product-level owner of default console navigation. Route configuration consumes that constant rather than restating the fixed scope path. Authentication, callback fallback, protected-route fallback, root navigation, and not-found recovery therefore continue to agree on the same home.

The old Teams page remains owned by scoped Team routes such as `/scopes/:scopeId/teams`. The technical `/scopes` entry no longer renders it and is hidden from the generated menu, so it cannot be presented as "My Teams" while opening Workflow Activity vNext.

## Navigation Behavior

| Entry | Result |
| --- | --- |
| Successful login without an explicit safe return target | Fixed Workflow Activity vNext workflow catalogue |
| `/` | Fixed Workflow Activity vNext workflow catalogue |
| `/overview` | Fixed Workflow Activity vNext workflow catalogue |
| `/scopes` | Fixed Workflow Activity vNext workflow catalogue |
| Login initiated from a safe protected deep link | Original deep link |
| `/scopes/:scopeId/teams` | Existing scoped Teams page |

## Failure And Security Behavior

No authentication or return-target validation logic changes. External, protocol-relative, login, and callback return targets continue to fall back through `sanitizeReturnTo`; safe internal paths continue to be preserved.

## Verification

- Update the home-navigation unit test to assert the exact fixed path.
- Update the route contract test to assert that `/`, `/overview`, and `/scopes` redirect to the shared home constant and that `/scopes` no longer mounts the Teams component.
- Keep an assertion that `/scopes/:scopeId/teams` still mounts the existing Teams page.
- Run only changed/related Jest tests and changed-file Biome checks, per the personal frontend validation policy. Full frontend validation remains owned by GitHub CI.
