# NyxID Canonical Operation Admission Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:executing-plans` and follow this plan task by task. Every behavior change starts with a focused failing test.

**Goal:** Fix #2944 through the generic #2984 contract: workflow authors select an exact connected-service operation, Aevatar resolves and commits a server-owned call-site proof from NyxID's effective OpenAPI, and every ordinary or indirect runtime invocation is built and checked against that proof before any proxy request.

**Architecture:** Keep NyxID as the sole OpenAPI/UserService owner. Add a typed step-level selector and compile all external tool call sites (including foreach/while synthesized sub-steps) into `external-capability-admission.v3` invocation admissions. The definition actor owns the committed plan; it is copied into run actor state and carried as a typed execution proof through the workflow tool adapter. A generic proof-bound request builder produces the NyxID wire request from path/query/body values without runtime OpenAPI reads.

**Tech stack:** .NET 10, C#, Protobuf, YAML workflow definitions, Aevatar actors, xUnit, FluentAssertions, NyxID REST/OpenAPI boundary.

## Decisions and constraints

- `WorkflowCapabilityAdmissionPlan` v3 uses call-site-scoped invocation admissions as the single source of truth. Field 4 remains a deprecated v2-only deserialization slot so persisted plans can receive typed migration errors; v3 creation leaves it empty and v3 validation rejects it when populated.
- Persisted v2 plans are not upgraded implicitly. Any prepare/publish/rebind path receives a stable typed blocker instructing the caller to bind the definition again; already-running actors retain their old definition under forward-only lifecycle semantics.
- Adding an empty repeated field does not change canonical protobuf bytes; the explicit schema string is therefore the version boundary and is covered by tests.
- The author-owned selector contains only `user_service_id` and `operation_id`. Slug, method, path template, schemas, response policy, source stamp, and digest are server-derived proof fields.
- `capability` is a typed step field, never a `parameters`/`arguments` bag entry. Derived proof fields supplied by authoring/import surfaces fail closed with migration remediation.
- `sub_param_` remains a generic argument-template mechanism. Indirect tool invocations gain a separate typed invocation spec and stable compiler-generated call-site identity.
- NyxID Proxy wire remains slug + exact UserService routing + HTTP request; `operation_id` and `contract_digest` are never sent.
- Runtime code does not read OpenAPI, definition actors, read models, or event stores, and no process-local registry owns proof state.
- No Lark-specific production branch or contract resource is introduced. Lark identifiers may appear only in fixtures that cover #2944.
- The existing connected-service create/provisioning contract stays unchanged. The approval-gated update surface may expose NyxID's already-published `openapi_spec_url` override field.

## Task 1: Confirm and expose the published UserService OpenAPI override

**Files:**

- Modify `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/nyxid_service_tools.proto`.
- Modify `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/NyxIdServiceInstanceClient.cs`.
- Modify `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/NyxIdServiceTools.cs`.
- Modify `src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdServicesTool.cs` only if this legacy human-session surface is still registered and covered by tests.
- Modify focused tests in `test/Aevatar.AI.Tests/NyxIdServiceToolsTests.cs` and related connected-service tests.

- [ ] Add RED tests proving update schema and wire body support set and clear (`""`) for `openapi_spec_url`, while create still omits it.
- [ ] Add the optional protobuf field and map it only through approval-gated update tools.
- [ ] Run focused AI tests and confirm GREEN.
- [ ] Comment on #2984 with the verified NyxID handler/update evidence and repository boundary.

## Task 2: Introduce the typed author selector and v3 call-site plan

**Files:**

- Modify the workflow definition proto/model that owns `StepDefinition`.
- Modify `src/workflow/Aevatar.Workflow.Abstractions/workflow_capability_admission.proto`.
- Modify admission integrity/normalization code and focused tests.

- [ ] Add RED serialization/parser tests for a step-level `NyxIdOperationSelector { user_service_id, operation_id }`; prove capability keys in the parameters map are rejected/ignored as author intent.
- [ ] Add `WorkflowCapabilityInvocationAdmission { call_site_id, capability }` and replace the v2 capability collection with v3 invocation admissions.
- [ ] Bump integrity schema to `external-capability-admission.v3`; add stable duplicate/empty call-site checks and deterministic ordering.
- [ ] Add RED tests that persisted v2 revalidation returns a typed `CAPABILITY_ADMISSION_REBIND_REQUIRED` result and that protobuf digest behavior is explicit.
- [ ] Implement the smallest model/integrity changes and run focused workflow tests GREEN.

## Task 3: Make OpenAPI parsing and operation resolution fail closed

**Files:**

- Modify `src/Aevatar.AI.ToolProviders.NyxId/ConnectedServices/OpenApiToolSpecParser.cs`.
- Modify `src/Aevatar.AI.ToolProviders.NyxId/NyxIdExternalWorkflowCapabilitySource.cs`.
- Modify parser/source result contracts and focused AI tests.

- [ ] Add RED tests for missing `operationId`, duplicate exact `operationId`, policy-rejected required headers, unsupported required body, unknown operation, and unallowlisted operation.
- [ ] Return typed parse/selection diagnostics; remove method/path fallback identity and `FirstOrDefault` ambiguity.
- [ ] Resolve the exact selector to one canonical operation and derive slug snapshot, method, template, parameter/body contract, response policy, digest, and source stamp server-side.
- [ ] Prove caller-supplied derived fields cannot affect the proof.
- [ ] Run focused AI tests GREEN.

## Task 4: Compile every ordinary and indirect invocation into admission

**Files:**

- Modify `src/workflow/Aevatar.Workflow.Core/WorkflowAuthorizationDependencyEvaluator.cs`.
- Modify workflow compiler/parser types as needed.
- Modify `ForEachModule.cs` and `WhileModule.cs` only to consume a shared typed invocation spec while retaining generic `sub_param_` behavior.
- Modify focused core/application tests and #2944 fixture acceptance tests.

- [ ] First reproduce the current bypass with RED expectations: foreach/for_each/foreach_llm and while/loop + synthesized `nyxid_proxy` must no longer yield zero capabilities / `NOT_REQUIRED_NO_EXTERNAL_SERVICE`.
- [ ] Add a shared `ExternalToolInvocationSpec` with stable call-site ID, typed selector, and runtime argument template.
- [ ] Enumerate ordinary children and synthesized sub-step declarations through one compiler path.
- [ ] Fail closed when an external indirect invocation lacks a static selector or changes service/operation per iteration.
- [ ] Preserve tests for `sub_param_prompt`, `sub_param_workflow`, and `sub_param_prompt_prefix`.
- [ ] Run focused workflow tests GREEN.

## Task 5: Generate and commit proof at definition admission

**Files:**

- Modify `WorkflowExternalCapabilityAdmissionService` and associated ports/results.
- Modify definition binding commands/events/state application where necessary.
- Modify application/core tests.

- [ ] Add RED tests for selector-only author input resolving into one proof per call site, exact UserService isolation, source access/no-contract failures, operation drift, and deterministic plan digest.
- [ ] Resolve each selector through the exact NyxID UserService OpenAPI source and write only canonical server-derived proof into v3.
- [ ] Preserve selected capability identity on every blocker without weakening anti-enumeration behavior.
- [ ] Commit the v3 plan to the definition actor and reject old/forged/ambiguous plans.
- [ ] Run focused application/core tests GREEN.

## Task 6: Hand proof to the run actor and tool execution context

**Files:**

- Modify `workflow_execution_messages.proto` and `workflow_state.proto`.
- Modify `WorkflowGAgent.cs` and run binding/state application.
- Modify `IWorkflowToolSource.cs`, `ToolCallModule.cs`, and `AgentWorkflowToolSourceAdapter.cs`.
- Add a typed tool execution context contract in the narrow AI/workflow boundary.
- Modify focused run/adapter tests.

- [ ] Add RED tests proving definition plan → bind run event → run state → execution request → typed tool context carries exactly the current call-site proof.
- [ ] Copy the committed plan into run actor state during binding.
- [ ] Resolve call-site proof from actor-owned run state, fail on missing/duplicate/wrong call-site, and place it on the execution request.
- [ ] Map proof to a typed agent tool context field; do not use metadata/items.
- [ ] Cover ordinary, nested, foreach, and while call sites.
- [ ] Run focused workflow/AI integration tests GREEN.

## Task 7: Build and enforce the operation-bound NyxID request

**Files:**

- Add a generic proof-bound request builder under the NyxID adapter boundary.
- Modify `NyxIdProxyTool.cs` and its focused tests.
- Update any tool argument schema exposed to workflow authoring so proof fields and raw route fields are absent.

- [ ] Add RED tests for static and multiple dynamic path segments, exact path-param names, missing/extra values, independent query/body handling, response policy, and zero HTTP calls on failure.
- [ ] Add traversal/encoding tests for raw and encoded slash, backslash, NUL, dot segments, encoded traversal delimiters, and unresolved placeholders.
- [ ] Build concrete path only from proof template plus typed runtime values; validate query/headers/body against the proof contract.
- [ ] Require matching proof, service, operation, method/template/digest at `NyxIdProxyTool` before dispatch; emit stable typed runtime failure codes.
- [ ] Keep `operation_id` and digest out of the NyxID wire request.
- [ ] Run focused runtime tests GREEN.

## Task 8: Preserve typed readiness at REST and migration surfaces

**Files:**

- Modify `src/Aevatar.Studio.Hosting/Endpoints/StudioMemberEndpoints.cs`.
- Modify host/application DTOs and tests.
- Modify migration/authoring rewrite path if present; otherwise add the narrow command required by current surfaces.

- [ ] Add RED endpoint tests for `STUDIO_MEMBER_EXTERNAL_CAPABILITY_NOT_READY` with typed status, selected capability, blockers, remediations, and sources.
- [ ] Map admission exceptions without leaking bearer, credentials, external response bodies, full OpenAPI, or stack details.
- [ ] Distinguish missing selector, source unavailable/access denied, contract required, operation unknown/rejected, drift, v2 rebind, and runtime argument invalid with stable codes.
- [ ] Ensure historical raw definitions can only migrate by online exact-contract resolution and never retain a runtime raw-path fallback.
- [ ] Run focused Studio tests GREEN.

## Task 9: Documentation, acceptance, and architecture guards

**Files:**

- Modify `docs/canon/nyxid-connected-service-tools.md`.
- Modify `docs/canon/workflow-primitives.md`.
- Modify the existing workflow external-capability canon/architecture document and migration section.
- Add generic fixture-based acceptance tests for the #2944 message-resource, approval-instance, and foreach scenarios.

- [ ] Document the single authority chain, v3 migration, author selector, indirect invocation declaration, proof handoff, runtime parameter model, and A′ update option.
- [ ] Include the repository-standard Mermaid init directive and quoted labels.
- [ ] Verify production code/resources contain no Lark-specific contract or branch and no secret-like fixture.
- [ ] Run all affected project tests plus required guards.

## Task 10: Final verification, integration, and push

- [ ] Review the complete diff against #2984 and repository architecture rules.
- [ ] Run:

```bash
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
bash tools/ci/architecture_guards.sh
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/docs/lint.sh
```

- [ ] Commit with a focused imperative message.
- [ ] Fetch the latest `origin/feature/integrate`, integrate without force, rerun proportional verification on the integrated tree, and push the verified commit to `origin/feature/integrate`.
