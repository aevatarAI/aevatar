# External Workflow Capability Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> `superpowers:executing-plans` task-by-task. The active repository instruction
> prohibits delegation, so execute inline and keep every production change behind
> a failing test first.

**Goal:** Make Chat-authored and API-authored workflows select Host Connector or
NyxID by authority owner, persist exact typed capability identities, pass one
server-side workflow admission path, and route NyxID calls with exact
`user_service_id` without accepting secrets.

**Architecture:** Replace slug/string workflow dependency evidence with Protobuf
external capability refs. The existing workflow parser and definition actor own
structural admission; a Workflow Application service composes read-only Connector
and NyxID sources for point-in-time readiness. Every ordinary workflow write
entry invokes the same admission interface before mutation. NyxID remains the
source of truth through live `/keys` and OpenAPI reads, and durable execution
continues through the existing scoped-key authorization pipeline.

**Tech Stack:** .NET 10, C#, Protobuf, xUnit, FluentAssertions, NyxID REST/OpenAPI.

## Global Constraints

- Use distinct test identities: `us-home-alpha`, `home-assistant`, and
  `connector-home-alpha` must never alias each other.
- Do not introduce process-local owner/service/session catalogs or caches.
- Do not prime projections, activate actors, or replay events from readiness or
  query paths.
- Never place a bearer, API key, cookie, OAuth secret, or secret-bearing header
  in Chat output, Workflow YAML, actor state, admission plans, read models,
  receipts, fixtures, or logs.
- Only Connector catalog operations and NyxID OpenAPI operations admitted by
  `x-aevatar-tool` may be authored. No arbitrary raw HTTP authoring surface.
- Preserve accepted-only command receipts; admission does not claim read-model
  observation.
- NyxID durable readiness fails with
  `DURABLE_AUTHORIZATION_UNAVAILABLE` when exact owner/node topology is not
  published. Never persist a caller bearer, mint allow-all credentials, infer
  from slug, or invent an OAuth resource grant.
- Run `bash tools/ci/test_stability_guards.sh` after every test batch and the
  workflow/query guards before completion.

---

### Task 1: Typed Capability Contract And Structural Compiler Admission

**Files:**

- Modify: `src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto`
- Modify: `src/workflow/Aevatar.Workflow.Core/WorkflowAuthorizationDependencyEvaluator.cs`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs`
- Modify: `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs`
- Test: `test/Aevatar.Workflow.Core.Tests/WorkflowAuthorizationDependenciesTests.cs`
- Test: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowRunActorPortBranchTests.cs`

- [ ] **Step 1: Write failing typed extraction and fail-closed tests**

Cover an exact Host Connector operation and an exact NyxID operation carrying
`service_id`, slug snapshot, operation id, method, path template, and contract
digest. Assert the parser rejects dynamic identity, slug-only/service-alias
identity, missing operation identity, changed method/path contract, and OpenAPI
header parameters named `Authorization`, `Proxy-Authorization`, `Cookie`,
`Set-Cookie`, `X-API-Key`, or equivalent API-key/token spellings.

Assert the definition actor independently derives the same typed dependencies
from YAML rather than trusting caller-supplied dependency evidence.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~WorkflowAuthorizationDependenciesTests"
dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~WorkflowRunActorPortBranchTests"
```

- [ ] **Step 3: Add Protobuf messages and compiler validation**

Add `ExternalWorkflowCapabilityRef`, Connector/NyxID refs, execution/readiness
enums, blockers, remediations, source stamps, capability descriptors,
`ExternalCapabilityReadiness`, and `WorkflowCapabilityAdmissionPlan`. Replace
the string lists in `WorkflowAuthorizationDependencies` with repeated typed refs
and reserve the removed wire names/numbers.

Make the evaluator return only static exact references and throw a stable typed
validation exception for unresolved identity or sensitive headers. Make both
`IWorkflowDefinitionParser` and `WorkflowGAgent` use the same evaluator so a
write adapter cannot bypass structural admission.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Task 1 commands, then:

```bash
dotnet build src/workflow/Aevatar.Workflow.Infrastructure/Aevatar.Workflow.Infrastructure.csproj --nologo --no-restore
bash tools/ci/test_stability_guards.sh
```

- [ ] **Step 5: Commit Task 1**

```bash
git commit -am "Add typed workflow capability dependencies"
```

### Task 2: Exact NyxID Instance Discovery And Proxy Routing

**Files:**

- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdConnectedServiceToolSource.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/ConnectedServiceProxyTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/OpenApiToolSpecParser.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdApiClientCoverageTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdConnectedServiceToolSourceTests.cs`
- Test: `test/Aevatar.AI.Tests/OpenApiToolSpecParserTests.cs`

- [ ] **Step 1: Write failing exact-identity tests**

Return two `/keys` entries with the same slug and distinct ids. Assert discovery
preserves both, tool names are unambiguous, each dynamic tool owns its exact id,
and execution sends `_nyxid_via=<exact-id>`. Assert `nyxid_proxy` requires
`service_id` for invocation, rejects sensitive headers before HTTP, and never
chooses the first matching slug.

Assert OpenAPI parsing excludes operations with sensitive header parameters,
even when they carry `x-aevatar-tool`.

- [ ] **Step 2: Run focused tests and verify RED**

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~NyxIdApiClientCoverageTests|FullyQualifiedName~NyxIdConnectedServiceToolSourceTests|FullyQualifiedName~OpenApiToolSpecParserTests"
```

- [ ] **Step 3: Preserve exact ids and route through `_nyxid_via`**

Discover caller-visible instances from `GET /api/v1/keys`, merge user/org
results by exact id rather than slug, pass the id into every
`ConnectedServiceProxyTool`, and include an id-derived suffix when duplicate
slug/operation names would collide. Add exact-id proxy overloads that append the
reserved query parameter without losing the admitted operation query.

Keep service discovery read-only and uncached. Remove raw invocation discovery
from `nyxid_proxy`; authoring uses the typed capability listing tool in Task 3.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Task 2 command and test stability guard.

- [ ] **Step 5: Commit Task 2**

```bash
git commit -am "Route NyxID tools by exact service identity"
```

### Task 3: Read-Only Capability Sources And Typed Readiness

**Files:**

- Add: `src/workflow/Aevatar.Workflow.Application.Abstractions/ExternalCapabilities/ExternalWorkflowCapabilityPorts.cs`
- Add: `src/workflow/Aevatar.Workflow.Application/ExternalCapabilities/ExternalWorkflowCapabilityReadinessService.cs`
- Modify: `src/workflow/Aevatar.Workflow.Application/DependencyInjection/ServiceCollectionExtensions.cs`
- Add: `src/Aevatar.Studio.Application/Studio/Services/ConnectorExternalWorkflowCapabilitySource.cs`
- Modify: Studio application DI registration
- Add: `src/Aevatar.AI.ToolProviders.NyxId/NyxIdExternalWorkflowCapabilitySource.cs`
- Modify: `src/Aevatar.AI.ToolProviders.NyxId/ServiceCollectionExtensions.cs`
- Test: workflow application readiness tests
- Test: `test/Aevatar.Studio.Tests/ConnectorExternalWorkflowCapabilitySourceTests.cs`
- Test: `test/Aevatar.AI.Tests/NyxIdExternalWorkflowCapabilitySourceTests.cs`

- [ ] **Step 1: Write failing source and readiness tests**

Cover public, `client_credentials`, and `secret_ref_header` Connectors and prove
all remain Host-owned. Cover missing/disabled Connector and contract drift.

For NyxID cover API-key/OAuth/direct/local-Node fixtures, duplicate slug,
inactive/pending status, access denied, missing/offline Node, missing OpenAPI,
unallowlisted/missing operation, and stale source. Prove each maps to a stable
typed status and credential-free remediation. Prove durable mode returns
`DURABLE_AUTHORIZATION_UNAVAILABLE` when topology evidence is incomplete.

- [ ] **Step 2: Run focused tests and verify RED**

Run the new test classes in their owning projects.

- [ ] **Step 3: Implement narrow sources and composite service**

Define source/list/inspect/admission ports in Workflow Application Abstractions.
The composite service fans out to registered sources and never stores results.
The Connector source reads `IConnectorCatalogQueryPort`; the NyxID adapter reads
live `/keys` and the exact OpenAPI spec, maps external JSON immediately into
Protobuf, computes deterministic SHA-256 contract/source digests, and returns
typed blockers/remediations.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Task 3 tests, affected DI tests, and stability/query guards.

- [ ] **Step 5: Commit Task 3**

```bash
git commit -am "Evaluate external workflow capability readiness"
```

### Task 4: Read-Only Chat Tools And Server-Side Workflow Admission

**Files:**

- Add: `src/Aevatar.AI.ToolProviders.Binding/Tools/ListExternalWorkflowCapabilitiesTool.cs`
- Add: `src/Aevatar.AI.ToolProviders.Binding/Tools/InspectExternalWorkflowCapabilityReadinessTool.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Binding/BindingAgentToolSource.cs`
- Modify: `src/Aevatar.AI.ToolProviders.Binding/Aevatar.AI.ToolProviders.Binding.csproj`
- Add: `src/workflow/Aevatar.Workflow.Application/ExternalCapabilities/WorkflowExternalCapabilityAdmissionService.cs`
- Modify: normal workflow write services in GAgentService and Studio
- Modify: workflow definition binding event/state plumbing
- Test: `test/Aevatar.AI.ToolProviders.Binding.Tests/BindingToolsTests.cs`
- Test: scope upsert, Studio provisioning, member binding, draft, and adapter tests

- [ ] **Step 1: Write failing Chat tool and shared-admission tests**

Assert both tools are read-only, take exact typed candidates, use the current
scope/caller context, preserve duplicate slugs, and serialize no secrets.

For Scope upsert, Studio provisioning, member bind, draft create/update, skill
mount, prepare, and publish entry points, inject one recording admission service
and assert it is called before the first mutation. Assert a non-ready result
causes no command dispatch. Assert the actor rejects a definition/admission-plan
digest or capability mismatch.

- [ ] **Step 2: Run focused tests and verify RED**

Run affected Binding, GAgentService, Studio, Workflow Core, and Infrastructure
test classes.

- [ ] **Step 3: Implement one admission service and wire all write paths**

The service calls the parser once, evaluates every typed ref, and creates a
`WorkflowCapabilityAdmissionPlan` with definition digest, exact refs, contract
digests, and source stamps. No-external-capability workflows succeed without an
external query. All ordinary write paths use the service; none reimplements its
rules. Pass the plan to definition bind, where the actor re-parses the YAML and
verifies structural identity/digest before committing YAML plus the admission
fact in one event/state transition.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run Task 4 tests plus:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
```

- [ ] **Step 5: Commit Task 4**

```bash
git commit -am "Enforce unified workflow capability admission"
```

### Task 5: Exact Durable Authorization Evidence

**Files:**

- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/Adapters/WorkflowServiceImplementationAdapter.cs`
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs`
- Modify: `src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationPlanner.cs`
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionScheduledInvocationAuthorityQueryPorts.cs`
- Test: GAgentService adapter/planner tests
- Test: Studio projection authority query tests
- Test: scheduled API-key issuer tests

- [ ] **Step 1: Write failing exact-service evidence tests**

Assert deployment evidence derives Connector refs and NyxID service ids from the
same typed capability refs, never from slug. Two equal slugs with distinct ids
must produce distinct grants. Missing exact id or unavailable topology must fail
with the existing typed durable authorization failure and must not invoke key
issuance. Keep both allow-all flags false.

- [ ] **Step 2: Run focused tests and verify RED**

Run the affected adapter, planner, projection, and issuer tests.

- [ ] **Step 3: Migrate evidence consumers**

Replace workflow evidence string fields with typed capability refs or derived
exact id/ref lists at the boundary. Delete slug resolution from the scheduled
planner. Preserve actor-backed source stamps and existing integrity digests.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run Task 5 tests and workflow/query guards.

- [ ] **Step 5: Commit Task 5**

```bash
git commit -am "Use exact capabilities for durable authorization"
```

### Task 6: Prompt, Canonical Documentation, And Migration Fixtures

**Files:**

- Modify: `agents/Aevatar.GAgents.NyxidChat/Skills/system-prompt.md`
- Modify: `docs/canon/connector.md`
- Modify: `docs/canon/nyxid-connected-service-tools.md`
- Modify: `docs/canon/scheduled-skill-runners.md`
- Modify: `docs/canon/workflow-primitives.md`
- Modify: relevant Chat/workflow architecture docs and example workflows

- [ ] **Step 1: Add prompt/document assertions first where existing tests enforce content**

Assert the prompt no longer accepts pasted credentials, instructs use of typed
readiness/remediation, requires `READY` before workflow write, and treats slug as
a display/routing snapshot rather than identity.

- [ ] **Step 2: Update prompt, canon, examples, and Mermaid diagram**

Document the authority-owner decision, exact identity, structural/readiness
admission split, secret boundary, interactive bearer, durable scoped key, and
fail-closed durable topology behavior. Update repository-owned example workflows
away from slug-only `nyxid_proxy` calls or explicitly mark them as migration
fixtures that cannot be newly admitted.

- [ ] **Step 3: Run docs and prompt tests**

```bash
bash tools/docs/lint.sh
bash tools/ci/test_stability_guards.sh
```

- [ ] **Step 4: Commit Task 6**

```bash
git commit -am "Document external capability admission"
```

### Task 7: Repository-Wide Verification, Review, And Delivery

- [ ] **Step 1: Run focused regression suites**

Run every focused command from Tasks 1-6 from a clean build output.

- [ ] **Step 2: Run required guards**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

- [ ] **Step 3: Run full build and test**

```bash
dotnet build aevatar.slnx --nologo --no-restore
dotnet test aevatar.slnx --nologo --no-build --no-restore
```

- [ ] **Step 4: Review the final diff**

Check layer direction, generated state compatibility, secret strings, slug-only
paths, process-local maps, query-time lifecycle operations, ports, and
documentation consistency. Run `git diff --check` and inspect every changed
file.

- [ ] **Step 5: Synchronize and push**

Fetch `origin/feature/integrate`, rebase the feature branch if remote advanced,
rerun affected verification after conflict resolution, then push the verified
HEAD with:

```bash
git push origin HEAD:feature/integrate
```
