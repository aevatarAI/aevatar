# Task 2 Phase A Report: Explicit Request Confirmation Admission

## Status

Implemented only Phase A application admission for caller-supplied NyxID explicit request confirmations.

Deliberately excluded:

- durable authorization catalog work;
- scope, Studio, or service-revision entry-point threading;
- `WorkflowGAgent` selector/grant switches;
- HTTP DTOs and frontend changes.

## RED

### 1. Task 1 baseline: missing confirmation has no typed admission blocker

Baseline: detached temporary worktree at commit `46f3b8c25`.

Command:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --nologo \
  --filter FullyQualifiedName~WorkflowExplicitRequestAdmissionRedTests
```

Result: failed as expected, `0 passed / 1 failed`.

Expected:

- `WorkflowExternalCapabilityAdmissionException`;
- readiness status `ContractDrift`;
- blocker code `NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED`.

Actual baseline behavior:

```text
System.InvalidOperationException: Workflow NyxID explicit request grant is required.
at WorkflowCapabilityAdmissionPlanIntegrity.ValidateNyxIdExplicitRequestAdmission(...)
at WorkflowExternalCapabilityAdmissionService.AdmitAsync(...)
```

This proved that Task 1 integrity validation rejected a missing grant only after application admission had already accepted the explicit selector; no typed application blocker existed.

The temporary RED worktree/test was not included in the implementation commit.

### 2. Current implementation review: stale risk was accepted

After reviewing the pre-existing Phase A draft, changed the risk fixture from `UNSPECIFIED` to a valid but stale `WRITE` attestation while readiness currently reported `READ_ONLY`.

Command:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --nologo --no-restore \
  --filter "FullyQualifiedName~WorkflowExplicitRequestAdmissionTests.AdmitAsync_WithStaleOrMismatchedConfirmation&DisplayName~risk"
```

Result: failed as expected, `0 passed / 1 failed`.

Failure:

```text
Expected a WorkflowExternalCapabilityAdmissionException to be thrown, but no exception was thrown.
```

Cause: admission checked only the HTTP method risk floor, so a GET confirmation could attest `WRITE` even though the current readiness contract was `READ_ONLY`.

Minimal fix: require `confirmation.attested_risk` to equal the current readiness proof risk, while retaining the HTTP method risk-floor check.

## GREEN

### Focused Phase A tests

Command:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --nologo --no-restore \
  --filter FullyQualifiedName~WorkflowExplicitRequestAdmissionTests
```

Result: passed, `8 passed / 0 failed / 0 skipped`.

Covered behavior:

- confirmation Protobuf exposes only `call_site_id`, `request_contract_digest`, and `attested_risk`;
- explicit selector without confirmation returns `NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED`;
- call-site mismatch returns `NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH`;
- digest drift returns `NYXID_EXPLICIT_REQUEST_CONFIRMATION_DIGEST_MISMATCH`;
- current-risk drift returns `NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH`;
- matching confirmation creates a typed binder-owned grant;
- grant authority is `AEVATAR_WORKFLOW_BINDER`;
- grant owner subject is the normalized request caller;
- allowed modes contain only the current interactive execution mode even when readiness advertises both interactive and durable;
- neither bearer text nor its tested SHA-256 representation is present in the plan bytes/string;
- `FromWorkflowYamls` clones confirmation inputs;
- published `NyxIdOperation` admission succeeds without a confirmation and has no explicit request grant.

### Application project regression

Command:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --nologo --no-restore
```

Result: passed, `509 passed / 0 failed / 0 skipped`.

### Test stability guard

Command:

```bash
bash tools/ci/test_stability_guards.sh
```

Result: exit code `0`; polling, coverage-file, layer, projection-reader, exception-observability, audit-trail, Lark path, FKST, GAgent registry, query-projection, and NyxIdChat guards passed.

Existing NuGet `NU1507` package-source warnings remain; no new test/build error was observed.

## Files

- `src/workflow/Aevatar.Workflow.Abstractions/workflow_capability_admission.proto`
  - Added stable `NyxIdExplicitRequestConfirmation` with exactly three caller-attestation fields.
- `src/workflow/Aevatar.Workflow.Application.Abstractions/ExternalCapabilities/ExternalWorkflowCapabilityPorts.cs`
  - Added cloned confirmation inputs to `WorkflowExternalCapabilityAdmissionRequest` and `FromWorkflowYamls`.
- `src/workflow/Aevatar.Workflow.Application/ExternalCapabilities/WorkflowExternalCapabilityAdmissionService.cs`
  - Added typed confirmation matching, drift blockers, server-side grant materialization, and unused-confirmation rejection.
- `test/Aevatar.Workflow.Application.Tests/WorkflowExplicitRequestAdmissionTests.cs`
  - Added real parser + readiness Phase A coverage.

## Self-review

- Caller cannot provide a full grant: the input contract has no grant, grantor, owner, authority, or execution-mode fields.
- Grantor data has a single source: admission fixes authority to `AEVATAR_WORKFLOW_BINDER`, owner kind to personal, and owner subject to normalized `request.Access.CallerId`.
- Admission narrows the readiness policy to the confirmed current risk and current execution mode before hashing the typed grant into the admitted capability.
- Matching uses exact call-site and request-contract digest equality and exact current-risk equality.
- Failure paths throw `WorkflowExternalCapabilityAdmissionException` before `AdmitAsync` returns a plan; this layer contains no mutation or active-revision command.
- Published-operation admission is unchanged and does not require or materialize an explicit request grant.
- No bearer field is read by grant construction, copied into Protobuf, persisted, or used as grant digest input.
- `git diff --check` passed.
- Changed files are limited to the Phase A Protobuf/application contracts, admission service, and Application tests.

## Concerns / Deferred Proof

- Phase A exposes a callable admission layer but does not yet thread confirmations through scope save/bind, Studio member bind/publish, or service-revision commands. Therefore this commit proves pre-plan application failure, not yet end-to-end absence of mutation in those later entry points.
- Durable catalog admission is explicitly excluded. A durable caller path cannot be considered complete until the later catalog and entry-point phases are implemented and tested.
- HTTP/frontend request surfaces and `WorkflowGAgent` switching remain intentionally unchanged.

## Phase A Review Fixes

### RED: partial confirmations across authored call-sites

Added a real-parser workflow containing authored explicit request call-sites `wf-alpha/request-alpha` and `wf-alpha/request-beta`, with a matching confirmation only for `request-alpha`.

Command:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --nologo --no-restore \
  --filter FullyQualifiedName~AdmitAsync_WithTwoExplicitRequestsButOnlyFirstConfirmed_ShouldRequireSecondGrant
```

RED result: `0 passed / 1 failed`. The second call-site incorrectly returned `NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH` instead of `NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED`.

Root cause: current-call-site matching used global `ExplicitRequestConfirmations.Count > 0`, so an unrelated but valid confirmation for another authored call-site changed a missing confirmation into a call-site mismatch.

Minimal fix:

- derive expected explicit call-sites from the parsed authored invocations;
- reject confirmations whose call-site is outside that expected set with the stable call-site mismatch blocker;
- once unknown confirmations are excluded, classify zero confirmations for the current expected call-site as `NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED`;
- keep duplicate confirmations for one expected call-site as call-site mismatch.

GREEN result: `1 passed / 0 failed`. The pre-existing unknown-only call-site mismatch theory case also remains green.

### RED: durable approval-required explicit requests

Added real-parser admission coverage for durable `POST`, `PUT`, `PATCH`, and `DELETE`, each with a matching confirmation.

Command:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --nologo --no-restore --no-build \
  --filter FullyQualifiedName~AdmitAsync_WithDurableApprovalRequiredExplicitRequest_ShouldRequireInteractive
```

RED result: `0 passed / 4 failed`. All four methods returned `DurableAuthorizationUnavailable`, proving the catalog-readiness branch ran before the stable interactive-only policy blocker.

Minimal fix: after parsing the authored selector, but before readiness/catalog inspection, durable `POST`, `PUT`, `PATCH`, and `DELETE` now return:

```text
status: ContractDrift
code: NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED
message: This explicit request can only be admitted for interactive execution.
```

This preserves approval-required and interactive-only semantics without adding or changing any durable catalog behavior.

GREEN result: `4 passed / 0 failed`.

### Safe-method non-regression

Added real-parser durable `GET`, `HEAD`, and `OPTIONS` coverage. All three continue to the existing Phase B catalog boundary and return `DURABLE_AUTHORIZATION_SOURCE_REQUIRED`, not the interactive-only blocker.

Result: `3 passed / 0 failed`.

### Review-fix verification

- `WorkflowExplicitRequestAdmissionTests`: `16 passed / 0 failed / 0 skipped`.
- `Aevatar.Workflow.Application.Tests`: `517 passed / 0 failed / 0 skipped`.
- `bash tools/ci/test_stability_guards.sh`: exit code `0`.
- `git diff --check`: passed.

Review-fix files:

- `src/workflow/Aevatar.Workflow.Application/ExternalCapabilities/WorkflowExternalCapabilityAdmissionService.cs`;
- `test/Aevatar.Workflow.Application.Tests/WorkflowExplicitRequestAdmissionTests.cs`.

Remaining concern is unchanged: Phase B must provide the durable read-only catalog path for `GET`, `HEAD`, and `OPTIONS`; this review fix only ensures approval-required methods cannot be misclassified as catalog-eligible.

## Authoritative Safe-Method Risk Review Fix

### RED: elevated authoritative risk was hidden by the catalog blocker

Parameterized the real readiness source so durable `GET`, `HEAD`, and `OPTIONS` could report an authoritative current risk of either `WRITE` or `DESTRUCTIVE`. Added six cases with a matching elevated confirmation.

Command:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --nologo --no-restore \
  --filter FullyQualifiedName~AdmitAsync_WithDurableElevatedRiskSafeRequest_ShouldRequireInteractive
```

RED result: `0 passed / 6 failed`. Every case returned `DurableAuthorizationUnavailable` instead of `ContractDrift` with `NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED`.

Added three more cases where the authoritative risk was `WRITE` but the caller supplied a stale `READ_ONLY` confirmation.

Command:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --nologo --no-restore \
  --filter FullyQualifiedName~AdmitAsync_WithStaleReadOnlyConfirmationForElevatedSafeRequest_ShouldReturnRiskMismatch
```

RED result: `0 passed / 3 failed`. Every case again returned `DurableAuthorizationUnavailable` instead of `NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH`.

Root cause: readiness execution-mode and selector identity validation, durable catalog validation, and general source validation were combined in `ValidateReadinessProof`. The durable catalog branch therefore returned before `BuildInvocationAdmission` could compare the caller confirmation with the authoritative selected-capability risk.

Minimal fix:

- split readiness validation into identity proof and source/catalog proof;
- after identity proof, validate the exact call-site, request digest, and attested risk against the authoritative selected capability;
- after that exact risk match, reject durable `WRITE` or `DESTRUCTIVE` explicit requests with `NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED`, using the selected capability risk rather than caller input as the authorization fact;
- only then continue to the unchanged source/catalog proof, so authoritative `READ_ONLY` requests still reach the Phase B catalog blocker;
- leave published capabilities on the existing source/catalog validation path without requiring an explicit-request confirmation.

No durable catalog implementation was added.

### GREEN and regression verification

- New authoritative-risk cases: `9 passed / 0 failed / 0 skipped`.
- `WorkflowExplicitRequestAdmissionTests`: `25 passed / 0 failed / 0 skipped`.
- `Aevatar.Workflow.Application.Tests`: `526 passed / 0 failed / 0 skipped`.
- `bash tools/ci/test_stability_guards.sh`: exit code `0`.
- `git diff --check`: passed before final report update.

Existing NuGet `NU1507`, obsolete-member, and `CA1506` warnings remain unchanged; no new build or test failure was observed.

## Phase B: Durable Exact-Service Authorization

### Status

Implemented only the Phase B NyxID explicit capability source and Application admission path. The implementation reuses the existing owner-scoped `INyxIdAuthorizationCatalogQueryPort` and the published capability source's durable catalog semantics. Scope/Studio/service-revision entry points, `WorkflowGAgent`, HTTP, and frontend remain unchanged.

### RED

Added source tests before production changes and ran:

```bash
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo \
  --filter FullyQualifiedName~NyxIdExplicitWorkflowCapabilitySourceTests
```

RED result: `14 passed / 7 failed`.

- exact catalog grants for `GET`, `HEAD`, and `OPTIONS` still returned `DurableAuthorizationUnavailable`;
- durable `POST`, `PUT`, `PATCH`, and `DELETE` returned the old generic catalog blocker instead of `NYXID_EXPLICIT_REQUEST_INTERACTIVE_REQUIRED`.

Added real parser + real readiness + real explicit source + real admission coverage and ran:

```bash
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj \
  --no-build --nologo --filter FullyQualifiedName~WithRealExplicitSource
```

Initial RED result: `3 passed / 3 failed`; all exact-grant safe methods stopped at `DURABLE_AUTHORIZATION_UNAVAILABLE`.

The subsequent GREEN iterations exposed two further real contract failures that the earlier fake readiness source had hidden:

- `Workflow NyxID explicit request proof digest is invalid`: the explicit source used an old local digest shape instead of the canonical admission integrity contract;
- `Workflow NyxID explicit request grant policy is invalid`: durable admission wrote only `Durable`, while the persisted grant contract requires `Interactive` plus read-only `Durable`.

### GREEN

- `NyxIdExplicitWorkflowCapabilitySourceTests`: `21 passed / 0 failed`.
- real parser/readiness/source/Application E2E cases: `6 passed / 0 failed`.
- `NyxIdExternalWorkflowCapabilitySourceTests`: `63 passed / 0 failed`.
- full `Aevatar.Workflow.Application.Tests`: `532 passed / 0 failed`.
- full `Aevatar.AI.Tests`: `1935 passed / 0 failed`.
- `bash tools/ci/test_stability_guards.sh`: exit code `0`.
- `git diff --check`: passed.

The source matrix proves:

- exact active `UserService.id` plus canonical caller-owned catalog admits durable `GET`, `HEAD`, and `OPTIONS`;
- readiness emits both fresh `NYX_ID_USER_SERVICES` and owner-derived `DURABLE_AUTHORIZATION_CATALOG` source stamps;
- missing catalog, owner mismatch, missing exact-ID grant, slug-only evidence, and same-slug/different-ID evidence fail closed with `DURABLE_AUTHORIZATION_UNAVAILABLE`;
- inactive and inaccessible live services return their typed service blockers before any catalog read;
- interactive explicit requests remain ready without reading or requiring the catalog;
- `POST`, `PUT`, `PATCH`, and `DELETE` are approval-required, interactive-only, and rejected before catalog reads;
- Phase A elevated-risk safe-method cases remain covered by the full Application suite;
- bind-time explicit inspection performs only `/api/v1/keys` reads; test handlers reject every other path.

### Files

- `src/Aevatar.AI.ToolProviders.NyxId/NyxIdDurableAuthorizationCatalogInspector.cs`
  - Extracted the published source's canonical owner, catalog integrity/freshness, exact service grant, slug snapshot integrity, and source-id rules into one internal reusable inspector.
- `src/Aevatar.AI.ToolProviders.NyxId/NyxIdExternalWorkflowCapabilitySource.cs`
  - Reuses the shared inspector with no behavior change to published-operation admission.
- `src/Aevatar.AI.ToolProviders.NyxId/NyxIdExplicitWorkflowCapabilitySource.cs`
  - Uses the existing catalog only for safe durable methods, emits both source proofs, derives conservative approval/mode policy, and uses canonical explicit proof digests.
- `src/workflow/Aevatar.Workflow.Application/ExternalCapabilities/WorkflowExternalCapabilityAdmissionService.cs`
  - Materializes durable read-only explicit grants with `Interactive + Durable`, preserving interactive-only grants as `Interactive`.
- `test/Aevatar.AI.Tests/NyxIdExplicitWorkflowCapabilitySourceTests.cs`
  - Adds exact-service, owner, slug, live service state, no-read, method policy, and source-stamp coverage.
- `test/Aevatar.Workflow.Application.Tests/WorkflowExplicitRequestAdmissionTests.cs`
  - Adds real parser/readiness/source/admission durable success and fail-closed tests.
- `test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj`
  - References the NyxID provider only for the real source integration tests.

### Self-review

- There is one durable catalog implementation and one query path: both NyxID sources call the same inspector over `INyxIdAuthorizationCatalogQueryPort`.
- Catalog lookup owner is exactly `nyxid / Personal / normalized access.CallerId`; returned catalog owner must match it, and the source stamp ID is `NyxIdAuthorizationCatalogActorIds.Build(owner)`.
- Authorization lookup is exact ordinal `UserServiceId`; slug equality is checked only as a server-derived integrity snapshot and never substitutes for ID.
- Catalog state version, activation, invalidation/cleanup, freshness, content digest, access, node grant shape, and resource-owner normalization preserve the previously approved published-source checks.
- Live visibility, active state, and credential-source access are checked from `/api/v1/keys` before durable catalog lookup.
- No MCP/OpenAPI read, cache, in-process fact dictionary, new catalog, new query port, or external repository change was added.
- Application source-proof validation remains independent and verifies the returned durable catalog source belongs to the authenticated caller before plan creation.
- Existing published-source durable tests and both affected test projects pass after the shared-inspector extraction.

### Concerns / Deferred Scope

- This phase intentionally does not make durable explicit admission reachable through Scope, Studio, service-revision, HTTP, frontend, or `WorkflowGAgent` entry points; those remain later phases.
- Existing repository NuGet/package-source and analyzer warnings were present during builds; no new compiler or test failure remains.

## Phase C1: Scope Live-Admission Entry Points

### Status

Threaded caller-supplied NyxID explicit request confirmations through the three Scope live-admission entry points:

- workflow upsert;
- workflow save-and-bind;
- workflow binding upsert.

Persisted plans still use `RevalidatePersistedAsync` and do not require a fresh interactive confirmation. HTTP, Studio, service-revision, frontend, and `WorkflowGAgent` surfaces remain outside this phase.

### RED

First added a context ownership test. Before the production contract change, the focused test failed to compile because `WorkflowCapabilityAdmissionContext` had no `ExplicitRequestConfirmations` property. After adding the property and defensive cloning, the focused context test passed `1 / 1`.

Then added real-parser, real-readiness, real-admission behavior coverage for all three entry points and ran the three affected test classes. Before entry-point threading, the result was `4 passed / 9 failed` across the 13 new cases:

- missing confirmation already failed closed with `NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED`;
- stale digest, stale risk, and matching confirmations all lost their caller input at the Scope boundary and therefore degraded to `NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED`;
- no mutation command was dispatched on each invalid path.

The RED result proved the failure was entry-point propagation, not parser, readiness, or admission behavior.

### GREEN

Minimal production change:

- `WorkflowCapabilityAdmissionContext` now accepts and clones `NyxIdExplicitRequestConfirmation` values;
- all three live-admission requests pass those confirmations into `WorkflowExternalCapabilityAdmissionRequest.FromWorkflowYamls`;
- save-and-bind preserves the cloned confirmations when rebuilding its transient trusted context;
- the existing-plan branches remain unchanged and continue to call `RevalidatePersistedAsync` without fresh confirmations.

The 13 new behavior cases now pass:

- each entry point rejects missing confirmation with `NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED`;
- each entry point rejects stale digest with `NYXID_EXPLICIT_REQUEST_CONFIRMATION_DIGEST_MISMATCH`;
- each entry point rejects stale risk with `NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH`;
- each entry point admits a matching confirmation and forwards or persists the binder-created caller-owned grant;
- workflow upsert and binding upsert dispatch no command on invalid admission;
- save-and-bind dispatches neither workflow nor binding mutation on invalid admission.

The fixture deliberately separates identities:

- `scope-c1-alpha`;
- `wf-route-c1-alpha`;
- `svc-runtime-c1-alpha`;
- `rev-c1-alpha`;
- `caller-c1-alpha`.

### Verification

- `WorkflowCapabilityAdmissionContext_ShouldCloneExplicitRequestConfirmations`: `1 passed / 0 failed`.
- The three affected test classes: `65 passed / 0 failed / 0 skipped`.
- Full `Aevatar.GAgentService.Tests`: `1588 passed / 0 failed / 0 skipped`.
- `bash tools/ci/workflow_binding_boundary_guard.sh`: exit code `0`.
- `bash tools/ci/test_stability_guards.sh`: exit code `0`.
- `git diff --check`: passed before final report update.

### Files

- `src/platform/Aevatar.GAgentService.Abstractions/ScopeWorkflows/WorkflowCapabilityAdmissionContext.cs`;
- `src/platform/Aevatar.GAgentService.Application/Workflows/ScopeWorkflowCommandApplicationService.cs`;
- `src/platform/Aevatar.GAgentService.Application/Workflows/ScopeWorkflowSaveAndBindApplicationService.cs`;
- `src/platform/Aevatar.GAgentService.Application/Bindings/ScopeBindingCommandApplicationService.cs`;
- the three corresponding application service test files under `test/Aevatar.GAgentService.Tests/Application/`.

### Self-review and concerns

- Confirmation input is defensively cloned at the context boundary; callers cannot mutate the trusted admission input afterward.
- Bearer tokens and caller confirmations are transient admission inputs. Neither is copied into the command/artifact; only the typed, binder-owned grant enters the admitted plan.
- Matching grants use the normalized caller as owner and `AEVATAR_WORKFLOW_BINDER` as authority, preserving Phase A ownership semantics.
- No identity is inferred from another ID. Test fixtures use visibly distinct scope, workflow, published service, revision, and caller identities.
- This phase does not expose confirmations through transport DTOs. Those surfaces need their own explicit contracts and no-mutation tests in later phases.

## Phase C1 Review Fixes

### RED / GREEN: immutable confirmation snapshot

Strengthened the context ownership proof by mutating a confirmation returned from
`WorkflowCapabilityAdmissionContext.ExplicitRequestConfirmations` and then reading the context again.

RED result: the later admission observed `mutated-after-context-read`; the focused test failed
`0 passed / 1 failed`. The constructor clone protected only the original caller input, while the
property exposed the context-owned Protobuf instance.

GREEN result: the context now keeps a private cloned collection and returns a fresh deep clone on
every read. Each of the three Scope entry points captures exactly one confirmation snapshot; binding
upsert captures it before its first `await` and passes that snapshot explicitly through the remaining
admission helpers. Focused result: `1 passed / 0 failed`.

### RED / GREEN: invalid-input matrix and zero mutation

Extended all three entry-point matrices with:

- an unknown confirmation call-site;
- duplicate confirmations for one authored call-site.

Both cases must return `NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH`. Every invalid case
must also prove that no workflow, binding, service, governance, exposure, revision, deployment,
serving-target, or rollout mutation was requested.

RED result: the tightened tests failed to compile because two recording command ports did not expose
a complete unified `Calls` observation. That exposed a test-infrastructure gap: some mutation methods
could execute without appearing in the previous assertions.

GREEN result: all `IServiceCommandPort` and `IServiceGovernanceCommandPort` mutation methods now write
to their respective unified `Calls` collections, while existing specialized observations remain
available. The three invalid matrices pass `15 passed / 0 failed`.

### RED / GREEN: persisted plans do not consume fresh confirmations

Added one proof per entry point using a plan produced by the real
`WorkflowExternalCapabilityAdmissionService.AdmitAsync`, followed by a decorator that delegates
`RevalidatePersistedAsync` to another real admission service. The context contains only the existing
plan and no fresh confirmations.

RED result: the three tests initially failed to compile because the real-plan factory, delegating
admission service, and persisted-plan context helpers did not yet exist.

GREEN result: all three pass. Each asserts `RevalidatePersistedCallCount == 1`,
`AdmitCallCount == 0`, and that the intended downstream mutation continues. Result:
`3 passed / 0 failed`.

### RED / GREEN: persisted command and artifact isolation

Added a binding-upsert proof that executes two otherwise identical requests with different bearer
tokens and different opaque Protobuf unknown-field bytes on their confirmations. The test first
proves the two input confirmations contain their distinct raw markers, then verifies:

- serialized `CreateServiceRevisionCommand` bytes are identical;
- the command's reachable descriptor graph contains no field named `confirmation`;
- neither raw confirmation marker appears in command bytes;
- production `WorkflowServiceRevisionArtifactBuilder` produces identical artifact bytes and hashes;
- the artifact descriptor graph contains no field named `confirmation`;
- neither marker appears in artifact bytes;
- both persisted plans contain only the typed caller-owned binder grant.

RED result: the focused test initially failed to compile because the raw-marker builder, descriptor
walker, and production artifact construction helpers were absent.

GREEN result: `1 passed / 0 failed`. This replaces the weaker bearer-only string assertion with a
byte-level and hash-level persistence proof.

### Final verification

- C1 review-fix focused tests: `20 passed / 0 failed / 0 skipped`.
- The three affected application-service test classes: `75 passed / 0 failed / 0 skipped`.
- Full `Aevatar.GAgentService.Tests`: `1598 passed / 0 failed / 0 skipped`.
- `bash tools/ci/workflow_binding_boundary_guard.sh`: exit code `0`.
- `bash tools/ci/test_stability_guards.sh`: exit code `0`.
- `git diff --check`: passed after the final report update.

Existing NuGet `NU1507`, obsolete-member, and analyzer warnings remain; no new build, test, or guard
failure was observed.

### Remaining concerns

- Transport DTOs, Studio entry points, and direct service-revision entry points still require their
  own explicit confirmation contracts and zero-mutation proofs.
- Frontend confirmation capture and forwarding remain outside Phase C1.
- `WorkflowGAgent` selector/grant switching remains a later phase.

## Phase C2: Studio Admission Confirmation Propagation

### Scope

Implemented transient explicit-confirmation propagation only for these Studio application paths:

- `StudioWorkflowProvisioningService.ProvisionAsync`;
- `StudioMemberWorkflowBindingPort.BindAsync`, including unpublished member bind and published
  member save-and-bind;
- `StudioMemberService.BindAsync` workflow binding runs.

HTTP DTOs, frontend files, direct service endpoints, and `WorkflowGAgent` were not changed.

### RED / GREEN: matching confirmation propagation

Added one matching-confirmation test for each Studio entry point using the real `WorkflowParser`,
`ExternalWorkflowCapabilityReadinessService`, and `WorkflowExternalCapabilityAdmissionService`.
The readiness source is deterministic, but no admission plan is mocked.

RED result: all three tests failed (`0 passed / 3 failed`) because the Studio services omitted
`ExplicitRequestConfirmations` when constructing Phase A admission requests.

GREEN result: each entry point now clones all transient admission inputs before its first `await`
and passes the confirmation snapshot to `WorkflowExternalCapabilityAdmissionRequest`. Matching
confirmation materializes the caller-owned binder grant before any Studio mutation.

After admission, Studio passes only a credential-free trusted context containing the cloned plan.
`StudioMemberService` explicitly clears `CapabilityAdmission` before dispatching
`StudioMemberBindingRunStartRequest`, so raw bearer and confirmation objects do not enter the member
command or binding-run state.

### Invalid confirmation matrix and zero mutation

Each of the three Studio entry points covers missing, unknown, duplicate, stale-digest, and
stale-risk confirmation inputs through real parser/readiness/admission behavior. Expected typed
blockers are:

- missing: `NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED`;
- unknown or duplicate: `NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH`;
- stale digest: `NYXID_EXPLICIT_REQUEST_CONFIRMATION_DIGEST_MISMATCH`;
- stale risk: `NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH`.

All failure cases assert no reachable mutation:

- provisioning: no member get/create/bind and no schedule preflight/create;
- member workflow binding: no member bind, Scope save-and-bind, or published-binding record;
- member service bind: no binding-run command; service lifecycle and service command ports remain
  fail-loud stubs.

### Published, unpublished, and persisted-plan paths

- Unpublished member binding preserves distinct `m-alpha`, `wf-alpha`, and `rev-alpha` identities
  and dispatches only after matching admission.
- Published member binding preserves distinct member, workflow, published service, returned
  revision, and authenticated caller identities. Scope save-and-bind and the Studio published
  binding record receive the caller-owned grant only through the trusted plan; bearer and raw
  confirmations are absent.
- Each Studio entry point has a plan produced by the real admission service, then proves
  `RevalidatePersistedAsync` is called exactly once, `AdmitAsync` is not called, no fresh
  confirmation is required, and the intended mutation proceeds.

The Phase C1 persisted command/artifact isolation proof remains authoritative for byte-level
service revision command, prepared artifact, and hash equality across different bearer and raw
confirmation markers. C2 adds Studio-specific assertions that transient contexts are absent from
member commands and credential-free at unpublished/published Scope handoff boundaries; only the
actor-owned typed grant inside the admission plan continues.

### Verification

- Focused `StudioWorkflowProvisioningServiceTests`, `StudioMemberWorkflowBindingPortTests`, and
  `StudioMemberServiceBindingTests`: `72 passed / 0 failed / 0 skipped`.
- Full `Aevatar.Studio.Tests`: `1592 passed / 0 failed / 0 skipped`.
- `bash tools/ci/test_stability_guards.sh`: exit code `0`.
- `bash tools/ci/workflow_binding_boundary_guard.sh`: exit code `0`.
- `git diff --check`: passed.

Existing NuGet package-source and analyzer warnings remain unchanged; no new build, test, or guard
failure was observed.

### Remaining concerns

- Task 3 still owns the HTTP/frontend confirmation transport contract.
- Direct service endpoint admission and `WorkflowGAgent` switching remain intentionally outside C2.

## Phase C2 Review Fix: Opaque Identity And Credential Isolation Fixtures

### RED / GREEN

Reworked the Studio provisioning fixture around pairwise unrelated authoritative identities:
`m-alpha`, `wf-alpha`, `svc-alpha`, and `rev-alpha`, with `caller-alpha` kept separate. The focused
provisioning test first failed because the old binding recorder echoed the generated workflow
candidate (`workflow-9fc91a76ae5f3397f7977b3db6b4e716`) instead of returning the authoritative
`wf-alpha` receipt contract.

The GREEN fixture now records every create, bind, preflight, and schedule-create hop. Member create
uses an explicit request-to-result factory, binding returns explicit authoritative workflow and
revision IDs, and the schedule recorder derives the service target and receipt only from the
accepted binding context. Exact literal assertions cover create input/result, bind input/result,
preflight, schedule input/result, and the final service invocation, so swapping identity kinds
breaks the test.

Invalid confirmation cases assert zero schedule preflight calls. The C2 admission kit now supplies
both a caller bearer and an organization bearer. Matching tests prove both credentials reach only
the admission request, while unpublished binding, published save-and-bind, provisioning handoff,
and member commands retain neither bearer nor raw confirmation context; only the typed actor-owned
grant remains in the persisted admission plan.

### Verification

- Focused Studio provisioning/binding/member tests: `72 passed / 0 failed / 0 skipped`.
- Full `Aevatar.Studio.Tests`: `1592 passed / 0 failed / 0 skipped`.
- `bash tools/ci/test_stability_guards.sh`: exit code `0`.
- `bash tools/ci/workflow_binding_boundary_guard.sh`: exit code `0`.
- `git diff --check`: passed.

Existing package-source and analyzer warnings remain unchanged. No production, HTTP, frontend,
direct endpoint, or `WorkflowGAgent` files were changed by this review fix.

## Phase D: Explicit Selector Service-Grant Semantics

### Scope

Extended the existing backend service-grant and identity switches so authored `NyxIdRequest` /
admitted `NyxIdUserRequest` capabilities are treated as exact NyxID service dependencies. Published
`NyxIdOperation` behavior and workflows without external capabilities remain unchanged.

Provider-neutral tool mapping, `WorkflowOperationAdmissionToolContextMapper`, frontend files, and
runtime request construction/execution were not changed.

### RED

Added tests before production changes for parser-to-`WorkflowGAgent` aggregation, revision artifact
evidence, Studio provisioning evidence, scheduled authorization planning, readiness identity
ordering/fail-closed behavior, and safe chat-run error identity mapping.

The baseline failures proved each missing switch:

- Core binding reported `NotRequiredNoExternalService` for an explicit request;
- revision artifact and Studio evidence reported `NotRequired`;
- scheduled planning rejected explicit-only and mixed explicit/published capabilities and returned
  the generic capability-identity failure for a missing exact explicit service ID;
- readiness preserved source order for explicit selectors and did not reject an unknown selector;
- both chat-run explicit selector/capability cases serialized `selectedCapability` as null.

### GREEN

Minimal production changes:

- parent workflow dependency aggregation now reuses each compiled child dependency's typed
  `ServiceGrantPolicy`, so inline explicit requests cannot be lost by reinterpreting selectors;
- revision artifact and Studio scheduling evidence recognize both `NyxIdUserService` and
  `NyxIdUserRequest` as requiring a service grant;
- scheduled planning extracts only `NyxIdUserRequest.Request.UserServiceId`, then uses the existing
  exact-ID normalization and deduplication path; it does not copy slug, endpoint, operation, path,
  or digest fields into required-service identity;
- readiness computes every selector identity before sorting, orders explicit requests by exact
  `UserServiceId` plus the typed request-contract digest, and rejects unknown selector variants;
- chat-run errors expose only exact `userServiceId` and typed `requestContractDigest` for explicit
  requests, with endpoint, operation, and connector identities null. Request path, headers, body,
  bearer data, service slug, proof digest, and grant digest are not emitted.

Mixed published and explicit capabilities deduplicate only when their exact `UserServiceId` values
match. Different service IDs remain distinct. Fixtures keep `m-alpha`, `wf-alpha`, `svc-alpha`, and
`usvc-*` identities visibly separate.

### Verification

Focused tests:

- `WorkflowAuthorizationDependenciesTests`: `53 passed / 0 failed / 0 skipped`;
- `ExternalWorkflowCapabilityReadinessServiceTests`: `9 passed / 0 failed / 0 skipped`;
- scheduled planner plus revision artifact builder tests: `61 passed / 0 failed / 0 skipped`;
- `StudioWorkflowProvisioningServiceTests`: `33 passed / 0 failed / 0 skipped`;
- `ChatRunStartErrorMapperTests`: `29 passed / 0 failed / 0 skipped`.

Full affected projects:

- `Aevatar.Workflow.Core.Tests`: `836 passed / 0 failed / 0 skipped`;
- `Aevatar.Workflow.Application.Tests`: `534 passed / 0 failed / 0 skipped`;
- `Aevatar.GAgentService.Tests`: `1606 passed / 0 failed / 0 skipped`;
- `Aevatar.Studio.Tests`: `1592 passed / 0 failed / 0 skipped`;
- `Aevatar.Workflow.Host.Api.Tests`: `903 passed / 0 failed / 0 skipped`.

Guards and scope checks:

- `bash tools/ci/test_stability_guards.sh`: exit code `0`;
- `bash tools/ci/workflow_binding_boundary_guard.sh`: exit code `0`;
- `git diff --check`: passed;
- changed-file audit contains no frontend, provider-neutral mapper,
  `WorkflowOperationAdmissionToolContextMapper`, or runtime request/execution file.

Existing NuGet package-source, obsolete-member, nullable, and analyzer warnings remain; no new
build, test, or guard failure was observed.

### Concerns / Deferred Work

- Task 3 owns HTTP/frontend confirmation transport and remains outside this phase.
- Tasks 4/5 own provider-neutral tool mapping and runtime explicit-request construction/execution.
- Phase D intentionally changes only backend grant/evidence/identity interpretation; it does not
  make an explicit request executable through those deferred runtime surfaces.
