# Workflow Archive Boundary Design

**Date:** 2026-08-10
**Status:** Approved for implementation

## Problem

Workflow Activity currently archives a published Workflow by resolving its
published service identity in the browser and calling the generic service
deployment endpoint:

```http
POST /api/services/{publishedServiceId}/deployments/{deploymentId}:deactivate
```

That endpoint belongs to the service-identity administration boundary. An
authenticated browser session carries user and scope authority, not the
`tenant_id`, `app_id`, and `namespace` service-principal claims required by
`ServiceIdentityEndpointAccess`. The backend therefore correctly returns
`403 SERVICE_IDENTITY_ACCESS_DENIED`; request-body identity fields cannot be
used as a fallback for an already authenticated caller.

The Workflow catalogue also joins two independently owned resources by stable
`workflowId`:

- an optional editable workspace draft;
- an optional committed published Workflow and active deployment.

The page currently presents `Delete draft` whenever the draft exists and
`Archive` whenever the deployment is active. A published row that still has a
draft consequently presents two destructive actions, while a committed-only
row presents only Archive. The distinction is technically explainable but is
not a coherent list-level product model.

## Product Semantics

The Workflow list uses the row's dominant lifecycle surface:

| Row facts | Destructive list action |
|---|---|
| Draft source only | `Delete draft` |
| Published source, with or without a draft | `Archive` |
| Archived published source | None |

`Delete draft` removes only the editable workspace document. It is never
presented as a lifecycle action for a published row.

`Archive` is a logical, reversible product boundary:

- stop new runs by deactivating the authoritative published deployment;
- preserve the editable draft, published revisions, committed events, and
  Activity history;
- let normal catalogue filtering hide or de-emphasize archived material;
- require a separate explicitly named delete/purge contract if permanent
  physical removal is introduced in the future.

The current change does not add physical deletion, cascade cleanup, or a new
global linear Workflow lifecycle.

## Backend Contract

Add a user-facing scope-owned command:

```http
POST /api/scopes/{scopeId}/workflows/{workflowId}:archive
```

The request has no body. `scopeId` and `workflowId` retain their isolated
meanings; the browser does not submit `publishedServiceId`, `deploymentId`,
`serviceAppId`, or `serviceNamespace`.

The endpoint performs only host responsibilities:

1. validate caller access to `scopeId` with `AevatarScopeAccessGuard`;
2. delegate to a narrow `IScopeWorkflowArchiveCommandPort`;
3. map application rejection categories to HTTP status and error payloads;
4. return `202 Accepted` for a successfully dispatched command.

The Application implementation resolves the Workflow through the existing
scope Workflow query/read-model contract. The resolved summary is the sole
source of:

- `ScopeId` -> service identity tenant;
- `ServiceAppId` -> service identity app;
- `ServiceNamespace` -> service identity namespace;
- `PublishedServiceId` -> service identity service ID;
- `DeploymentId` -> target deployment.

The implementation never assumes `workflowId == publishedServiceId`, never
parses identity prefixes, and never accepts a browser-provided service
identity. It verifies that the deployment is currently `Active` before
dispatching `DeactivateServiceDeploymentCommand` through
`IServiceCommandPort`.

The accepted result contains scope/workflow identity, the exact target
deployment ID, the standard command handle, and the Workflow read-model URL.
Acceptance means only that the command entered dispatch. It does not claim
that the deployment is already deactivated or that projections are current.

## Frontend Contract

The frontend branch based on
`feat/2026-08-04_workflow-activity-vnext` remains frontend-only. It adds
`scopesApi.archiveWorkflow(scopeId, workflowId)` and removes the direct
`servicesApi.deactivateDeployment` call from Workflow Activity.

After `202 Accepted`, the existing bounded observer continues querying the
scope Workflow catalogue. Success is reported only after the exact
`workflowId` has authoritative `deploymentStatus = Deactivated`. A delayed
observation retries only observation and never resubmits the archive command.

Menu visibility becomes:

```text
canDeleteDraftFromList = capabilities.delete.available && !hasCommittedSource
canArchiveFromList = committed deployment facts indicate Active
```

Rename may remain available when a published row retains an editable draft;
the change removes only the conflicting destructive `Delete draft` action.

## Failure Semantics

- Missing Workflow: `404 SCOPE_WORKFLOW_NOT_FOUND`.
- Ambiguous, stale, or not-ready published identity: `409` with a stable
  Workflow archive rejection code.
- Deployment already inactive or deactivated: `409 WORKFLOW_NOT_ACTIVE` and no
  duplicate command dispatch.
- Invalid route identity: `400 INVALID_USER_WORKFLOW_REQUEST`.
- Scope access denial: the existing scope guard response, before application
  dispatch.
- Accepted but not yet projected: frontend keeps the dialog open and offers
  observation-only retry.

The generic `/api/services/...:deactivate` endpoint and
`ServiceIdentityEndpointAccess` are unchanged.

## Branch Boundaries

- Backend contract and canonical documentation are implemented from latest
  `feature/integrate` on `fix/2026-08-10_workflow-archive-command`.
- Frontend API integration and menu policy are implemented from latest
  `feat/2026-08-04_workflow-activity-vnext` on
  `fix/2026-08-10_workflow-archive-boundary`.
- No backend source, tests, configuration, or documentation are added to the
  frontend feature branch.

## Verification

Backend focused tests prove:

- the route accepts a user-scoped caller without service-principal claims;
- the Application service resolves distinct workflow, published-service, and
  deployment identities from the read model;
- only an active deployment dispatches deactivation;
- missing, stale, and inactive Workflows dispatch no command;
- the HTTP response remains accepted-only.

Frontend focused tests prove:

- Archive calls the scope Workflow endpoint with only `scopeId/workflowId`;
- the generic service API is not used;
- a draft-only row exposes only `Delete draft`;
- a published row with a draft exposes `Archive` but not `Delete draft`;
- a committed-only published row exposes `Archive` but not `Delete draft`;
- accepted commands still require observed `Deactivated` catalogue state.
