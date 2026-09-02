---
title: "Scope Workflow as the Authoritative Runnable Workflow Model"
status: proposed
owner: eanzhao
---

# ADR-0036: Scope Workflow as the Authoritative Runnable Workflow Model

## Context

Discussion [#2305](https://github.com/aevatarAI/aevatar/discussions/2305) resolved a recurring workflow authority problem: Page/API, Lark/NyxID bot, Ornn skill packages, Studio drafts, inline YAML draft-runs, and local workflow tooling can all handle workflow YAML, but they must not become parallel published workflow models.

The user-facing ambiguity is that "can execute YAML" is not the same as "is a published workflow that can be listed, queried, and run by workflow identity across Aevatar surfaces." Without a single authority, users can create a workflow through one entry point and then fail to find or run it through another.

Aevatar already has the scope workflow path that maps a workflow YAML into the service lifecycle and workflow runtime:

```text
IScopeWorkflowCommandPort.UpsertAsync
 -> service definition / service revision WorkflowSpec
 -> publish / default serving / activation
 -> serving and deployment current-state readmodels
 -> workflow actor binding readmodel
 -> IScopeWorkflowQueryPort
 -> IWorkflowChatRunInteractionPort
```

This ADR records the authority boundary before follow-up implementation issues align Lark, Ornn import/mount, and package conventions.

## Decision

**Scope Workflow is the only authoritative model for workflows that are published, queryable, and runnable inside Aevatar.**

Concretely:

```text
Scope Workflow = system authority for published / queryable / runnable workflows
Ornn Skill Workflow = template, package, or import source
Studio Draft = editor draft / authoring state
Inline YAML = draft-run, preview, or import input
```

Only YAML that enters the scope workflow path through `IScopeWorkflowCommandPort.UpsertAsync` and is materialized through the service revision, deployment, and workflow binding readmodels is a published runnable workflow. Other YAML-bearing surfaces may create drafts, templates, packages, previews, or migration inputs, but they do not create a published workflow identity until they explicitly upsert or mount into Scope Workflow.

## Locked Rules

### 1. Published workflow write authority

The write-side authority for creating or updating a published runnable workflow is `IScopeWorkflowCommandPort`.

- Page/API workflow creation must use `IScopeWorkflowCommandPort`.
- Lark "create workflow" flows that mean "page-visible" or "later runnable by workflow id" must use `IScopeWorkflowCommandPort`.
- Ornn import or mount flows become published workflows only after they call `IScopeWorkflowCommandPort.UpsertAsync`.
- Command responses may return accepted receipts and stable read model URLs; they must not fabricate readmodel readiness or runnable actor facts inline.

### 2. Published workflow query authority

The query-side authority for published workflows is `IScopeWorkflowQueryPort` backed by materialized readmodels.

- Page/API and Lark workflow lookup must use `IScopeWorkflowQueryPort`.
- Query results must honestly distinguish `not found`, `not ready`, `stale`, and `runnable` states.
- Runnable status requires materialized service lifecycle/deployment facts and workflow actor binding facts to agree.
- Query paths must not repair or invent readiness by reading actor state, replaying events, priming projections, or doing ad hoc side reads at request time.

### 3. Published workflow run authority

The execution entry point for a published workflow is `IWorkflowChatRunInteractionPort` after lookup has resolved a runnable Scope Workflow.

Page/API and Lark may keep different adapters for HTTP/SSE/AGUI, NyxID relay context, conversation routing, card rendering, attachments, and delivery, but they must converge before execution on:

```text
IScopeWorkflowQueryPort
 -> runnable Scope Workflow summary
 -> WorkflowChatSource.DefinitionActor(...)
 -> IWorkflowChatRunInteractionPort
```

Adapters must not infer workflow actor ids from strings, local files, member ids, service ids, or Ornn package paths.

### 4. Ornn workflow YAML is not the Aevatar workflow catalog

Ornn skill `workflow_yamls` are templates/package contents/import sources. They are not the Aevatar published workflow catalog.

- `ornn_publish_skill` publishes an Ornn skill package; it does not imply Scope Workflow visibility.
- An Ornn workflow template becomes an Aevatar runnable workflow only after explicit mount/import/upsert into Scope Workflow.
- Before mount/import, it may be visible through Ornn skill discovery but must not appear as a published Scope Workflow.
- After mount/import, the copied scope workflow identity and readmodels are the runtime authority, not the original Ornn package asset.

### 5. Studio drafts are authoring state

Studio drafts are editor state. They may be saved, previewed, validated, and later published, but draft storage is not a published workflow authority.

Studio member-first identity remains valid: `memberId` is the Studio product identity and `publishedServiceId` is the member's published contract identity. Those identities do not become aliases for `workflowId`, `actorId`, or a workflow YAML name.

When a Studio workflow implementation is published as a runnable scope-facing workflow, the published workflow semantics must still flow through Scope Workflow command/query/run contracts.

### 6. Inline YAML is ephemeral input

Inline YAML is allowed for draft-run, preview, validation, and import/mount transitions.

Inline YAML must not receive a durable published workflow identity, must not appear in `IScopeWorkflowQueryPort` results, and must not be treated as a catalog entry merely because it was executed successfully once.

### 7. Identity boundaries are explicit

The following identifiers are not interchangeable and must not be converted by string convention:

- `workflowId`: Scope Workflow identity inside a scope.
- `memberId`: Studio member/product identity.
- `publishedServiceId` / `serviceId`: published service contract identity.
- `actorId`: runtime actor identity, opaque to callers.
- workflow YAML `name`: authoring/runtime metadata inside YAML.
- Ornn package workflow path/id: template/package identity.

Any adapter that needs to cross these boundaries must use typed command/query ports and materialized readmodels, not prefix parsing or name guessing.

## Forbidden Patterns

Implementations must not introduce or preserve a second published workflow authority by:

- treating Ornn skill packages or `workflow_yamls` as the Scope Workflow catalog;
- treating Studio draft/workspace state as published workflow state;
- treating inline YAML draft-runs as durable workflow definitions;
- treating local workflow files or file-backed catalog adapters as production workflow authority;
- treating service catalog definition snapshots alone as runnable workflow facts;
- deriving runnable `actorId`, `deploymentId`, `activeRevisionId`, or status from string rules;
- doing query-time replay, actor side reads, projection priming, or request-path joins to make a workflow appear runnable;
- adding a Lark-specific workflow store or Page/API-specific workflow store.

## Lark Default Semantics

When a Lark user asks to "create a workflow", the default meaning is:

- **Runnable / page-visible / later invokable workflow** → create or update a Scope Workflow through `IScopeWorkflowCommandPort`.
- **Reusable template / distributable skill package** → publish an Ornn skill package.

If both are requested, Scope Workflow is the runtime authority and Ornn is an export/package/template surface. Ornn must not become the primary storage for a workflow that the user expects to run and see through Aevatar workflow APIs.

The Lark adapter may keep Lark-specific delivery, conversation, and card behavior, but workflow publish, lookup, and run must use the same application ports as Page/API.

## Forward Migration

Existing Ornn/Lark workflows must move forward without hot-replacing active runs.

- New runnable workflows should be created in Scope Workflow first.
- Existing Ornn workflow templates can be explicitly mounted/imported into Scope Workflow.
- Optional auto-mount may be introduced when a scope context is explicit, but the resulting Scope Workflow copy is the runtime authority.
- Active runs keep their existing implementation and actor/run state; new runs after migration resolve through Scope Workflow.
- Compatibility paths should either delegate to Scope Workflow or stay clearly marked as template/draft/import adapters, then be deleted when consumers move. Compatibility must not be promoted into a second authority.

This follows the repository upgrade-forward rule: old runs remain on their existing implementation, new requests use the new authority, and no state is hot-swapped without an explicit migration contract.

## Consequences

- Users get one mental model: Scope Workflow is what Page/API and Lark can list, inspect, and run by workflow identity.
- Ornn remains useful as a package and template distribution mechanism without becoming a competing workflow catalog.
- Studio drafts and inline YAML remain first-class authoring and preview tools without becoming published identities.
- Query honesty is required: absence or lag of materialized deployment/binding facts yields `not ready` or `stale`, not fake runnable records.
- Future cleanup issues can align Page/Lark ports, Ornn mount/import, and package path conventions against this decision.

## Non-Goals

This ADR does not:

- remove existing compatibility endpoints;
- redesign Studio member identity or `publishedServiceId` ownership;
- change NyxID registration semantics;
- define Ornn package path migration details;
- implement Lark tool routing changes;
- add or modify runtime code, protobuf contracts, or projections;
- make inline draft-run unavailable for authoring/preview.

## Verification

For this ADR PR:

- run `bash tools/docs/lint.sh`;
- run `bash tools/docs/build-index.sh` and commit the generated `docs/README.md` update;
- run `git diff --check`.
