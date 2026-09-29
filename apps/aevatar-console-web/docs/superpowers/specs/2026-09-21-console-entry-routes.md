# Console Entry And Canonical Routes

## Decision

The current Workflow Activity experience is the user-facing console. Keep all
existing Teams, Members, Studio, Chat, Runtime, Services, Governance,
Deployments and old Settings page source and tests for future work, while
removing those pages from the registered router and visible navigation.
Retain their route definitions in `config/legacyRoutes.ts`; it is an inactive
inventory, not a second runtime router or an authorization mechanism.

This 2026-09-21 user decision supersedes the original preview namespace and
legacy-route exposure requirements in the August design and user paths.
It preserves the existing Excalidraw visual baseline, identity boundaries,
backend contracts, data sources, authentication and localization behavior.

## Public Routes

| Surface | URL |
| --- | --- |
| Account home resolver | `/workflows` |
| Workflows | `/scopes/:scopeId/workflows` |
| Create | `/scopes/:scopeId/workflows/new` |
| Templates | `/scopes/:scopeId/workflows/new/templates` |
| Editor and Schedule | `/scopes/:scopeId/workflows/:workflowId` |
| Activity | `/scopes/:scopeId/activity` |
| Run detail | `/scopes/:scopeId/activity/:runId` |
| Channels | `/scopes/:scopeId/channels` |
| Bind NyxID bot | `/scopes/:scopeId/channels/bind/:botId` |
| Channel details | `/scopes/:scopeId/channels/:registrationId` |
| Edit Channel | `/scopes/:scopeId/channels/:registrationId/edit` |
| Settings | `/scopes/:scopeId/settings` |

`/`, `/overview` and `/scopes` open `/workflows`. The resolver refreshes the
account profile and uses the authoritative scope; it never substitutes a
fixed workspace. Login and callback keep their existing routes and preserve
safe deep-link queries and fragments.

Account reads have a 15-second deadline covering session restoration, the
request and response-body reading. A stalled read is aborted and the home
shows its existing Retry action. Retrying starts a new read; a late result
from a timed-out read cannot supply the destination scope. Network failures
do not clear the login session or substitute a cached workspace.

Legacy business routes and the former `workflow-activity-vnext` URL namespace
are unregistered and resolve to 404, including direct visits. This prevents
users from entering the preserved pages through old links. Their code,
shared dependencies, tests, APIs and persisted resources remain intact.
The existing canvas benchmark keeps its explicit development opt-in.

## Navigation

The current shell is the only visible navigation for Workflows, Activity,
Channels and Settings. Its account Settings action uses the route scope,
including when the account profile contains a different scope. Desktop and
mobile account actions pass through the owning page's navigation guard so
unsaved editing cannot be bypassed.

The old global console shell remains in source for the preserved pages but
is hidden on the current routes. Its legacy Live Ops polling is disabled on
these routes. Internal source-directory, query-key and locale-key names are
unchanged; only public URL construction and matching lose the preview name.

## Verification

Route configuration tests cover the complete public inventory, home aliases,
static/dynamic ordering and exclusion of preserved routes. Current page,
route-builder and auth tests use canonical URLs. Shell integration tests
exercise the real account menu with the current scope and desktop/mobile
navigation guard. Existing legacy page tests remain in the repository.

Local verification runs changed tests and directly related tests, changed-file
Biome, the test stability guard and baseline integrity. Full frontend tests,
typecheck and production build are delegated to GitHub CI. No backend change,
fixture fallback, local backend or new dependency is needed.
