# Workflow Template Route And Page Semantics Design

## Goal

Make the Workflow Activity vNext template browser a real, addressable creation
surface instead of transient state inside the generic New workflow page. The
page must have one clear heading, survive refresh and browser navigation, and
report an unavailable backend contract honestly without introducing a mock or
local fallback.

## Product Semantics

Workflow templates are a workflow creation method. They are not workspace
workflow resources and they are not an independent top-level catalogue in the
current product model.

The canonical route is therefore:

```text
/scopes/:scopeId/workflow-activity-vnext/workflows/new/templates
```

The generic creation chooser remains at `/workflows/new`. Choosing `Use
template` navigates to the canonical template route. `Change method` navigates
back to `/workflows/new`. Direct navigation, refresh, browser back, and browser
forward must all reconstruct the same surface from the URL.

## Page Hierarchy

The template route owns one page-level heading:

- Title: `Start from a template`
- Description: `Browse public templates, inspect details, or create a draft directly.`
- Header action: `Change method`

The template browser component owns the catalogue controls, list, pagination,
detail modal, creation actions, and catalogue states. It must not render a
second page title or a second description. The workflow side navigation
already provides a route back to the workflow collection, so this page does
not add another competing `Back to workflows` action.

## Component Boundaries

`WorkflowActivityVNextPage` resolves the canonical template pathname before
the dynamic workflow detail pathname and renders a dedicated
`WorkflowTemplatesPage`.

`WorkflowTemplatesPage` owns the shell title, description, and `Change method`
navigation. It composes `WorkflowTemplateBrowser` with the current `scopeId`.

`NewWorkflowPage` owns only the creation method chooser and the Describe and
Import YAML creation flows. The template choice navigates instead of changing
local component state.

`WorkflowTemplateBrowser` remains responsible for template list, detail, and
instantiate behavior. Its API contracts and identity handling do not change.

## Backend Availability And Errors

The frontend continues to call the real backend contracts introduced by
backend PR #3484:

```http
GET /api/workflow-templates
GET /api/workflow-templates/{templateId}
POST /api/scopes/{scopeId}/workflow-templates/{templateId}:instantiate
```

The current remote backend based on `feature/integrate` does not contain these
routes because PR #3484 is not merged or deployed. The frontend must not replace
them with mock data, a local backend, a different path, or a successful empty
response.

For an HTTP 404 from the initial catalogue request, the primary product message
is `Templates are not available in this environment.` The raw HTTP failure
remains available in technical details. Other failures retain the general
`Templates could not be loaded` message and retry action.

## Testing

Implementation follows test-first development. Focused tests will verify:

- the canonical route exists before `/workflows/:workflowId`
- the template URL builder encodes `scopeId`
- `Use template` navigates to `/workflows/new/templates`
- direct navigation renders the dedicated template page
- the page contains exactly one `Start from a template` heading and one
  supporting description
- `Change method` navigates to `/workflows/new`
- a catalogue 404 uses the environment-unavailable product message while
  preserving `HTTP 404 Not Found` as technical detail
- existing list, detail modal, and instantiate behavior remains covered

Local validation runs only the affected Jest files and changed-file static
checks. Full frontend tests, typecheck, and production build remain delegated
to GitHub CI.

## Non-Goals

- Merging or deploying backend PR #3484
- Starting a local backend or adding mock template data
- Changing the workflow template API contract
- Moving templates to a top-level workspace route
- Refactoring unrelated Workflow Activity pages
