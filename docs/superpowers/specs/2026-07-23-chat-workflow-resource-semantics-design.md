# Chat Workflow Resource Semantics Design

**Status:** Approved

**Date:** 2026-07-23

## Problem

The Console Chat implies that an unqualified request such as "list my workflows"
means the workflows owned by Teams in the caller's current workspace, but the
runtime currently exposes `aevatar_list_workflows` as a query over the global
public workflow catalog. The model therefore reports public templates as if they
were the caller's workspace resources.

This is an ownership and contract mismatch, not a presentation-only problem.
Prompt wording alone cannot correct a tool whose name and data source encode the
wrong resource semantics.

## Evidence And Root Cause

- Console Chat selects the built-in `studio` workflow.
- The Studio role allowlist includes `aevatar_list_workflows` and
  `aevatar_get_workflow`.
- Both tools are implemented by `WorkflowCatalogAgentToolSource` and read the
  globally shared `IWorkflowCatalogPort` read model.
- Studio already exposes `IStudioMemberQueryPort`, whose projection-backed
  summaries carry independent `TeamId`, `MemberId`,
  `ImplementationRef.WorkflowId`, and `PublishedServiceId` facts, but there is no
  workflow-named tool that presents those member-owned resources.

The earlier catalog isolation fix correctly prevents newly committed scope-owned
definitions from entering the shared catalog. It does not change the product
meaning of an unqualified workflow request, and it does not remove documents
that were materialized before that fix.

## Product Decision

In Console Chat, an unqualified `workflow` means a workflow implementation owned
by a Team member in the caller's current workspace.

The global catalog is a template library. Chat may query it only when the user
explicitly asks for public templates, examples, or the template library.

The identities remain separate throughout the tool contract:

- `member_id` identifies Team member authority.
- `workflow_id` identifies the workspace workflow draft or definition document.
- `published_service_id` identifies the callable runtime service.

No tool, prompt, route builder, or test may derive one identity from another.

## Selected Architecture

### Workspace Workflow Query

Add a dedicated `StudioWorkflowQueryToolSource` to
`Aevatar.AI.ToolProviders.StudioProvisioning`. It discovers one read-only tool,
`aevatar_list_workflows`, when `IStudioMemberQueryPort` is available.

The tool:

1. Resolves scope only from `AgentToolRequestContext`, preferring owner scope in
   the same way as the existing Studio query tools.
2. Accepts optional `team_id`, `page_size`, and `page_token` arguments.
3. Calls `IStudioMemberQueryPort.ListAsync`, which reads the Studio member
   projection and applies the `scope_id + team_id` filter before pagination.
4. Returns only members whose `ScopeId` matches the resolved scope, whose
   `TeamId` is present, and whose `ImplementationKind` is `workflow`.
5. Passes through the member roster continuation token. If the user requested
   every workflow, Chat must continue until `next_page_token` is absent.

Each result has a stable, flat identity contract containing at least:

- `scope_id`
- `team_id`
- `member_id`
- `workflow_id` (present with `null` when the member has not yet observed a
  workflow implementation reference)
- `published_service_id`
- `workflow_url`

The canonical workflow editor URL is:

`/scopes/:scopeId/teams/:teamId/members/:memberId/workflow`

The result may also include display, lifecycle, revision, and freshness-adjacent
member summary fields already supplied by the read model. It must not embed a
generic identity bag or infer missing IDs.

`aevatar_get_member` remains the detail query after a workflow list result. A
second ambiguous `get workflow` workspace tool is unnecessary.

### Public Template Catalog

Keep the existing `IWorkflowCatalogPort` and its projection boundary, but make
the agent-facing contract explicit:

- `aevatar_list_workflow_templates`
- `aevatar_get_workflow_template`

The list result root property becomes `templates`. The detail argument becomes
`template_name`. Template lookup and error messages use template vocabulary.
There are no compatibility aliases for the old ambiguous names.

The list operation continues to expose only `ShowInLibrary = true` entries. An
exact template lookup retains the existing ability to address a hidden global
example by name.

### Chat Tool Selection

Update the Studio system prompt and allowlist so that:

- unqualified workflow inventory requests use `aevatar_list_workflows`;
- explicit public-template, example, or template-library requests use the two
  template catalog tools;
- responses preserve `member_id`, `workflow_id`, and `published_service_id` as
  different identities;
- listing all workspace workflows follows continuation tokens to exhaustion.

The prompt guides product behavior, while the tool names and backing ports make
the resource boundary enforceable without relying on prompt compliance.

### Host Composition

Register `StudioWorkflowQueryToolSource` through
`AddStudioProvisioningTools()` and compose it into `workspace.default` alongside
the existing Studio query sources. Keep `WorkflowCatalogAgentToolSource` in the
same tool set because explicit template discovery remains supported under its
new tool names.

## Error Semantics

- Missing caller scope returns `caller_scope_unavailable` without querying the
  port.
- Unknown or malformed arguments return `invalid_arguments` without querying
  the port.
- Cancellation propagates as `OperationCanceledException`.
- Unexpected member-query failures return a sanitized `workflow_query_failed`
  error that reveals only the exception type.
- Public catalog failures retain sanitized structured errors, renamed to
  template semantics where exposed to the model.

## Existing Catalog Contamination

No online query may delete, replay, rebuild, or prime a projection. Documents
materialized before the scope-isolation fix require a separate, explicit
background materialization or operations migration against the catalog read
model. The active canon and an operations runbook will record this boundary.

This change does not claim to repair production data. Production access was not
available during investigation, so the code fix is based on the complete static
data path and the repository evidence associated with issues #2913 and #2925.

## Alternatives Considered

### Prompt-Only Steering

Rejected. The model would still see an ambiguously named tool backed by the
wrong owner, and future prompt changes could reintroduce the behavior.

### Scope-Filtering The Global Catalog At Query Time

Rejected. The global catalog has one shared public-template meaning and does not
carry scope ownership. Adding scope filtering there would mix two resources and
would not provide Team, member, draft, or published-service identities.

### Reusing `aevatar_list_members` Without A Workflow Tool

Rejected. It leaves the model to translate an unqualified workflow request into
a broader member inventory and to filter product resources itself. A narrow
workflow query adapter can enforce the resource semantics while reusing the same
authoritative read port.

## Verification

Tests will prove:

- workspace discovery, DI registration, and `workspace.default` composition;
- caller-scope and optional Team/page argument forwarding;
- exclusion of scripts, unassigned members, and cross-scope rows;
- distinct `m-alpha`, `wf-alpha`, and `svc-alpha` identities plus the canonical
  member workflow URL;
- an explicit `workflow_id: null` for an unbound workflow member;
- template tool names, `template_name`, and `templates` wire fields;
- Studio prompt and allowlist selection rules;
- cancellation, validation, and sanitized provider failures;
- no stale ambiguous tool names in production code or active canon.

Targeted tests, query/projection guards, test stability guards, architecture
guards, docs lint, and relevant project builds must pass before push.
