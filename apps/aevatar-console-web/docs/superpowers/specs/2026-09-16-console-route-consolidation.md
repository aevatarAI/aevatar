# Console Route Consolidation

## Status And Precedence

Approved by the user's 2026-09-16 request on top of
`feat/2026-08-04_workflow-activity-vnext`.

The current Workflow, Activity, Channels, and Settings experience becomes the
only production console surface. This decision supersedes the original vNext
namespace isolation requirement, the requirement to retain legacy console
pages, and earlier Team/member frontend route requirements. The existing
Excalidraw visual baseline, API contracts, identity boundaries, authentication,
localization, and user journeys continue to apply.

## Canonical Routes

All scoped routes remove the `workflow-activity-vnext` segment:

| Surface | Canonical URL |
| --- | --- |
| Workflows | `/scopes/:scopeId/workflows` |
| Create Workflow | `/scopes/:scopeId/workflows/new` |
| Workflow templates | `/scopes/:scopeId/workflows/new/templates` |
| Workflow editor and Schedule | `/scopes/:scopeId/workflows/:workflowId` |
| Activity | `/scopes/:scopeId/activity` |
| Run detail | `/scopes/:scopeId/activity/:runId` |
| Channels | `/scopes/:scopeId/channels` |
| Connect Telegram | `/scopes/:scopeId/channels/connect/telegram` |
| Channel connection result | `/scopes/:scopeId/channels/:registrationId` |
| Edit Channel | `/scopes/:scopeId/channels/:registrationId/edit` |
| Settings | `/scopes/:scopeId/settings` |

`/workflows` retains the account-owned home behavior: fetch the current auth
profile, resolve its scope, and open that scope's Workflow list. `/`,
`/overview`, and `/scopes` redirect to `/workflows`. `/scopes/:scopeId` opens
the scoped Workflow list. No fixed workspace is introduced.

`/login`, `/auth/callback`, and the not-found surface remain. Authentication
preserves safe destinations, including their query and hash. Account Settings
uses the current scope and the owning page's navigation guard, so opening it
cannot bypass unsaved Workflow changes.

Retired business URLs, including the former vNext namespace, render the
not-found surface. There are no hidden Teams, Members, Runtime, or legacy
Settings route aliases. The existing `/workflow-canvas-benchmark` diagnostic
route remains available only through its explicit development opt-in; it is
absent from the default production route table.

## Retired Frontend Surfaces

Remove the old Teams, Members, Studio, Scopes, Chat, Workflows, Runtime Runs,
Actors, GAgents, Primitives, Mission Control, Mission Wall, Services,
Governance, Deployments, and Settings pages, together with their obsolete
navigation helpers and global console shell.

This is frontend retirement only. It does not delete backend resources,
endpoints, persisted data, or the distinct Team/member/workflow/service
identities used by API contracts.

The current editor still reuses the existing Workflow canvas, editor surface,
node library, and empty state. Move these dependencies to
`src/shared/workflowEditor/`. Move the LLM selection and save-observation
helpers to `src/shared/settings/` so the retained console no longer imports
retired page modules. Preserve their behavior and direct tests.

The internal `pages/workflow-activity-vnext` directory, locale keys, and query
keys may retain their names. They do not define public URLs and do not require
a separate namespace migration.

## Verification

Protect the complete canonical route inventory, home resolution, auth
recovery, Workflow navigation and editing, Activity details, Channel creation
and editing, Settings, and account-menu navigation with focused existing
tests. Retain regression coverage for shared components moved out of retired
pages. Audit internal imports after deletion.

Run only dependency-related tests and changed-file static checks locally.
Full frontend tests, type checking, and production build belong to GitHub CI.
Local preview must use the configured remote backend, with no mock data,
authentication bypass, or local backend substitution.
