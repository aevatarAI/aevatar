# Studio Query Tools Review Fixes Design

## Problem

The Studio tool surface has read-only tools for teams, members, and schedules,
but the current implementation is not ready for `dev`:

- the advertised workflow catalog/detail tools are absent;
- schedule tools depend on `IScheduledDispatchApplicationService`, a broad
  interface that also exposes mutations;
- schedule tools identify a Team automation with only `member_id`, omitting the
  owning `team_id`;
- returned `schedule_url` values point at the generic schedule endpoint, which
  intentionally hides Team-owned schedules;
- `TeamAutomationLifecycleStatus` is serialized as an integer instead of a
  stable Studio wire value;
- the moved Studio member contract had fallen out of the GAgent identity guard.

The GAgent guard path and its regression meta-test are already fixed. This
design covers the remaining workflow and schedule query corrections while
keeping the frontend tree identical to `dev`.

## Boundaries

Workflow catalog tools belong to `Aevatar.AI.ToolProviders.Workflow`. They
depend directly on `IWorkflowCatalogPort`, which is the existing read-only
application abstraction backed by the workflow catalog read model in composed
production hosts. They do not use the legacy workflow definition command
adapter and do not register mutation tools.

Schedule tools remain in `Aevatar.AI.ToolProviders.StudioProvisioning`, but
depend only on a new `IStudioMemberAutomationQueryPort` in
`Aevatar.Studio.Application.Abstractions`. The existing
`StudioMemberWorkflowSchedulePort` implements this read interface as well as
the existing mutation interface. Dependency injection resolves both interfaces
to the same singleton implementation, while query consumers only receive the
narrow capability.

No tool calls local HTTP endpoints. Workflow queries read through
`IWorkflowCatalogPort`; schedule queries read through the Studio query port,
which validates `scope -> team -> member` ownership before reading Team-owned
schedule read models. No actor state, event store, replay, query-time priming,
or process-local fact map is introduced.

## Workflow Tool Contract

Add a dedicated `WorkflowCatalogAgentToolSource` that discovers exactly two
tools when `IWorkflowCatalogPort` is available:

- `aevatar_list_workflows` accepts an empty object and lists runnable workflow
  catalog entries.
- `aevatar_get_workflow` requires `workflow_name` and returns catalog metadata,
  YAML, the typed definition, and graph edges.

Both tools are read-only and non-destructive. They query the global runnable
workflow catalog, not a Studio member implementation draft. Their descriptions
and arguments therefore use `workflow_name`; they never call a member API,
accept `member_id`, or reinterpret a draft `workflowId`.

The list result preserves query honesty by including the catalog item's
`authority_state_version`, `projection_watermark`, and `last_event_id`. The
detail result preserves the same catalog metadata. Unknown arguments, malformed
JSON, missing names, missing workflows, cancellation, and provider failures use
the same structured error envelope as the Studio query tools; internal
exception messages are not exposed for unexpected failures.

## Schedule Tool Contract

`IStudioMemberAutomationQueryPort` exposes only:

```csharp
Task<StudioMemberAutomationListResponse> ListAsync(
    string scopeId,
    string teamId,
    string memberId,
    int take = 50,
    string? cursor = null,
    bool includeTotalCount = false,
    CancellationToken ct = default);

Task<StudioMemberAutomationView?> GetAsync(
    string scopeId,
    string teamId,
    string memberId,
    string scheduleId,
    CancellationToken ct = default);
```

`IStudioMemberWorkflowSchedulePort` inherits this interface and keeps its
existing mutation members. The schedule tool source accepts only the new query
interface.

`aevatar_list_schedules` requires `team_id` and `member_id`, and accepts
`page_size`, `page_token`, and `include_total_count`. `aevatar_get_schedule`
requires `team_id`, `member_id`, and `schedule_id`. The caller scope always
comes from `AgentToolRequestContext`; model-supplied `scope_id` remains
rejected.

Schedule results are mapped from `StudioMemberAutomationView`, not from the
platform-wide scheduled-dispatch DTO. The wire contract includes the distinct
`scope_id`, `team_id`, `member_id`, `published_service_id`, and `schedule_id`
identities. It exposes the existing stable lowercase `authorization_status`
string rather than a runtime enum. The canonical API URL is:

```text
/api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/{scheduleId}
```

The tool does not return `recent_fires`, because the narrow Studio read
contract and canonical Studio endpoint expose the current automation view, not
the platform-wide schedule detail artifact.

## Registration

`AddWorkflowTools` registers `WorkflowCatalogAgentToolSource` alongside the
existing workflow source. Mainnet's `workspace.default` explicitly includes
the catalog source without pulling legacy definition mutation tools into the
Studio surface.

`AddStudioProvisioningTools` continues to register the schedule source, now
resolved through `IStudioMemberAutomationQueryPort`. Mainnet's existing
workspace registration remains otherwise unchanged.

The built-in Studio workflow allowlist adds `aevatar_list_workflows` and
`aevatar_get_workflow`. Existing team, member, and schedule query names remain
allowed. Legacy `workflow_list_defs`, `workflow_read_def`, and workflow
definition mutation tools remain excluded.

## Error Semantics

Input errors return `invalid_arguments`. A missing caller scope on a schedule
query returns `caller_scope_unavailable`; workflow catalog queries remain
honestly global and do not pretend the scope filters their results. Missing
workflow and schedule resources return `workflow_not_found` and
`schedule_not_found`. Unexpected workflow and schedule provider failures return
`workflow_query_failed` and `schedule_query_failed` with only the exception
type, not its message. `OperationCanceledException` is always rethrown.

Team membership and schedule ownership validation stay inside the Studio
application query port. The tool does not translate an ownership mismatch into
a different identity or fall back to the generic schedule query surface.

## Verification

Focused tests cover:

- workflow source discovery with and without a catalog port;
- workflow list/detail serialization, freshness fields, unknown arguments,
  missing names, not-found results, cancellation, and provider failures;
- schedule source discovery through the narrow query port;
- required `team_id` and `member_id`, caller-scope propagation, paging, and
  ownership-aware list/get calls;
- lowercase `authorization_status` and the nested Team automation URL;
- rejection of model-supplied `scope_id` and structured not-found results;
- DI resolution of query and mutation interfaces to the same Studio singleton;
- Mainnet workspace registration and built-in Studio allowlist coverage.

Final verification runs the affected .NET test projects, test stability guard,
workflow binding and query/projection guards, architecture guards, full .NET
build/test, and confirms `apps/aevatar-console-web` has no diff from
`origin/dev`.
