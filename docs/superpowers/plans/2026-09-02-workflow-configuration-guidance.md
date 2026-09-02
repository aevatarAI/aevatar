# Workflow Configuration Guidance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the novice-hostile `nyxid_proxy` text field with a guided connected-action picker, readiness feedback, and typed operation inputs while keeping runtime identity in the correct workflow fields.

**Architecture:** Add two authenticated scope HTTP endpoints that project existing external-capability application ports into strict browser DTOs. Extend the Studio document draft to round-trip typed step capability independently from parameters, then add a focused external Tool call editor inside the existing inspector shell. Generic node configuration remains the fallback, and technical/raw details stay progressively disclosed.

**Tech Stack:** ASP.NET Core minimal APIs, Protobuf contracts, xUnit/FluentAssertions, React 19, TypeScript, Ant Design, TanStack Query, Jest, Testing Library, Biome.

---

## File Map

- `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeWorkflowEndpoints.cs`: register list/readiness routes, enforce scope/caller context, and delegate to application ports.
- `src/platform/Aevatar.GAgentService.Hosting/Endpoints/WorkflowCapabilityHttpContracts.cs`: define narrow HTTP DTOs and exhaustive Protobuf-to-wire mappings.
- `test/Aevatar.GAgentService.Integration.Tests/ScopeWorkflowEndpointsTests.cs`: verify endpoint authority forwarding, response sanitization, mapping, and invalid input.
- `test/Aevatar.GAgentService.Integration.Tests/GAgentServiceHostingServiceCollectionExtensionsTests.cs`: verify both routes are registered.
- `apps/aevatar-console-web/src/shared/studio/models.ts`: define API DTOs and typed workflow capability document shapes.
- `apps/aevatar-console-web/src/shared/studio/api.ts`: strictly decode list/readiness responses and expose scoped API methods.
- `apps/aevatar-console-web/src/shared/studio/api.test.ts`: verify request paths, payloads, canonical responses, and decoder rejection.
- `apps/aevatar-console-web/src/shared/studio/document.ts`: round-trip capability in an inspector draft independently from parameters.
- `apps/aevatar-console-web/src/shared/studio/document.test.ts`: expose identity corruption with different selector/runtime values.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.ts`: pure selector keys, argument parsing/serialization, and contract field mapping.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.test.ts`: verify preservation, expressions, type coercion, and selector identity.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.tsx`: render discovery, readiness, risk, remediation, and generated operation inputs.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.test.tsx`: verify the novice path and recoverable states.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.tsx`: integrate the semantic editor, improve hierarchy/copy, and apply capability plus parameters atomically.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts`: accept the full configuration change from the inspector.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`: pass the active scope to the inspector.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`: style stable status, picker options, input groups, technical details, and narrow layouts with existing tokens.

### Task 1: Capability HTTP contract and endpoint tests

**Files:**
- Create: `src/platform/Aevatar.GAgentService.Hosting/Endpoints/WorkflowCapabilityHttpContracts.cs`
- Modify: `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeWorkflowEndpoints.cs`
- Test: `test/Aevatar.GAgentService.Integration.Tests/ScopeWorkflowEndpointsTests.cs`
- Test: `test/Aevatar.GAgentService.Integration.Tests/GAgentServiceHostingServiceCollectionExtensionsTests.cs`

- [ ] **Step 1: Write failing list endpoint tests**

Add a recording `IExternalWorkflowCapabilityListPort`, construct a discovery result with an exact selector whose IDs differ from its display text, invoke `HandleListWorkflowCapabilitiesAsync`, and assert:

```csharp
listPort.Request!.Access.ScopeId.Should().Be("user-1");
listPort.Request.Access.CallerId.Should().Be("caller-alpha");
body.Should().Contain("\"displayName\":\"PostHog / List dashboards\"");
body.Should().Contain("\"userServiceId\":\"us-posthog-alpha\"");
body.Should().Contain("\"endpointId\":\"list-dashboards\"");
body.Should().NotContain("transient-capability-bearer");
```

Add a forbidden-scope test and assert the recording port is not called.

- [ ] **Step 2: Write failing readiness endpoint tests**

Pass this HTTP request:

```csharp
new WorkflowCapabilityReadinessHttpRequest(
    new WorkflowCapabilitySelectorHttpRequest(
        "nyxid_operation",
        "us-posthog-alpha",
        "list-dashboards"),
    "interactive")
```

Return a readiness contract containing path/query/header parameters, a JSON request body, response modes, write risk, approval, blockers, and remediation. Assert the application request contains the exact selector and the JSON contains explicit string enums and safe messages.

- [ ] **Step 3: Write failing invalid request tests**

Cover empty IDs, unsupported selector kind, and execution mode `background`. Assert `400 INVALID_USER_WORKFLOW_REQUEST` and no readiness port invocation.

- [ ] **Step 4: Write failing route registration assertions**

Assert the endpoint data source contains:

```text
/api/scopes/{scopeId}/workflow-capabilities
/api/scopes/{scopeId}/workflow-capabilities:readiness
```

- [ ] **Step 5: Run the focused tests and confirm failure**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ScopeWorkflowEndpointsTests|FullyQualifiedName~GAgentServiceHostingServiceCollectionExtensionsTests"
```

Expected: compilation/test failure because the endpoint handlers and HTTP contracts do not exist.

- [ ] **Step 6: Implement the HTTP DTO mapper**

Create discriminated selector request/response records and response records for descriptors, diagnostics, source stamps, readiness, blockers, remediation, selected operation, schema, response policy, and execution policy. Map every supported enum through exhaustive switch expressions such as:

```csharp
private static string ToWireValue(NyxIdOperationValueKind value) => value switch
{
    NyxIdOperationValueKind.String => "string",
    NyxIdOperationValueKind.Integer => "integer",
    NyxIdOperationValueKind.Number => "number",
    NyxIdOperationValueKind.Boolean => "boolean",
    NyxIdOperationValueKind.Object => "object",
    NyxIdOperationValueKind.Array => "array",
    _ => "unspecified",
};
```

Reject unspecified/unsupported selector kinds and empty selector identities rather than constructing partial Protobuf messages. Omit credentials, grants, digests not required by input authoring, and deprecated capability payloads.

- [ ] **Step 7: Implement endpoint handlers**

Register:

```csharp
group.MapGet("/{scopeId}/workflow-capabilities", HandleListWorkflowCapabilitiesAsync);
group.MapPost("/{scopeId}/workflow-capabilities:readiness", HandleWorkflowCapabilityReadinessAsync);
```

Each handler must run `AevatarScopeAccessGuard`, call `WorkflowCapabilityAdmissionHttpContext.Create`, build `ExternalWorkflowCapabilityAccessContext`, delegate to the appropriate port, and map credential/invalid-operation failures through existing safe responses.

- [ ] **Step 8: Run the focused backend tests and commit**

Run the Step 5 command. Expected: PASS.

Commit:

```bash
git add src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeWorkflowEndpoints.cs src/platform/Aevatar.GAgentService.Hosting/Endpoints/WorkflowCapabilityHttpContracts.cs test/Aevatar.GAgentService.Integration.Tests/ScopeWorkflowEndpointsTests.cs test/Aevatar.GAgentService.Integration.Tests/GAgentServiceHostingServiceCollectionExtensionsTests.cs
git commit -m "Expose workflow capability guidance endpoints"
```

### Task 2: Strict frontend capability API

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/studio/models.ts`
- Modify: `apps/aevatar-console-web/src/shared/studio/api.ts`
- Test: `apps/aevatar-console-web/src/shared/studio/api.test.ts`

- [ ] **Step 1: Write failing discovery API tests**

Mock a canonical list response, call `studioApi.listWorkflowCapabilities('scope-alpha')`, and assert the exact GET path plus the decoded descriptor. Add malformed cases for an unknown selector kind, empty `userServiceId`, and an unsupported source kind; each must reject before returning data.

- [ ] **Step 2: Write failing readiness API tests**

Call:

```ts
studioApi.inspectWorkflowCapabilityReadiness({
  scopeId: 'scope-alpha',
  executionMode: 'interactive',
  selector: {
    kind: 'nyxid_operation',
    userServiceId: 'us-posthog-alpha',
    endpointId: 'list-dashboards',
  },
});
```

Assert the exact POST body and decode nested schemas, parameters, policies, blockers, and remediations. Reject selector mismatch and unsupported readiness/schema enum values.

- [ ] **Step 3: Run the frontend API test and confirm failure**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --selectProjects jsdom --runInBand src/shared/studio/api.test.ts
```

Expected: FAIL because the types and methods do not exist.

- [ ] **Step 4: Add narrow TypeScript contracts and strict decoders**

Define string unions for selector/source/readiness/schema/location/risk/approval/response enums and readonly interfaces for both responses. Reuse existing `expectRecord`, `expectString`, `decodeArray`, and enum-decoder patterns. Validate non-empty identities and selector equality after decoding readiness.

- [ ] **Step 5: Add API methods**

Implement one GET and one POST through `requestDecodedJson`; normalize the scope ID, serialize only the typed selector and execution mode, and never accept credential material in either input type.

- [ ] **Step 6: Run the focused API test and commit**

Run the Step 3 command. Expected: PASS.

Commit:

```bash
git add apps/aevatar-console-web/src/shared/studio/models.ts apps/aevatar-console-web/src/shared/studio/api.ts apps/aevatar-console-web/src/shared/studio/api.test.ts
git commit -m "Add typed workflow capability client"
```

### Task 3: Preserve step capability in inspector drafts

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/studio/models.ts`
- Modify: `apps/aevatar-console-web/src/shared/studio/document.ts`
- Modify: `apps/aevatar-console-web/src/shared/studio/document.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts`

- [ ] **Step 1: Write the failing round-trip test**

Use distinct values:

```ts
const capability = {
  nyxid_operation: {
    user_service_id: 'us-posthog-alpha',
    endpoint_id: 'list-dashboards',
  },
};
```

Assert `createStepInspectorDraft` clones this value, `applyStepInspectorDraft` writes a changed selector while retaining `parameters.tool = 'nyxid_proxy'`, and setting `capability: null` removes only the capability field.

- [ ] **Step 2: Run the document test and confirm failure**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --selectProjects node --runInBand src/shared/studio/document.test.ts
```

Expected: FAIL because inspector drafts do not expose capability.

- [ ] **Step 3: Implement the typed draft boundary**

Add `StudioWorkflowCapability`, add `capability?: StudioWorkflowCapability | null` to the step document, and add `capability: StudioWorkflowCapability | null` to `StudioStepInspectorDraft`. Clone and validate only the supported `nyxid_operation` document shape; preserve unrelated step fields through existing spreads.

Change the editor callback from a parameters string to:

```ts
type StudioStepConfigurationChange = Pick<
  StudioStepInspectorDraft,
  'parametersText' | 'capability'
>;
```

Apply both fields in one `applyStepInspectorDraft` call before YAML serialization.

- [ ] **Step 4: Update existing test fixtures explicitly**

Every literal `StudioStepInspectorDraft` in changed test paths gets `capability: null`; avoid making the new draft property optional because omission would obscure whether capability should be retained or removed.

- [ ] **Step 5: Run the document test and commit**

Run the Step 2 command. Expected: PASS.

Commit:

```bash
git add apps/aevatar-console-web/src/shared/studio/models.ts apps/aevatar-console-web/src/shared/studio/document.ts apps/aevatar-console-web/src/shared/studio/document.test.ts apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts
git commit -m "Preserve workflow capability in inspector drafts"
```

### Task 4: Pure external-operation configuration mapping

**Files:**
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.ts`
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.test.ts`

- [ ] **Step 1: Write failing selector and argument tests**

Cover:

- selector keys do not rely on display names;
- exact API selector to document capability conversion;
- parsing canonical argument JSON into path/query/header/body/response values;
- updating one declared field preserves unknown top-level keys and undeclared location keys;
- `${steps.lookup.output}` stays a string for an integer schema;
- literal integer/number/boolean values are coerced only when valid;
- empty optional values are removed while required empty values produce a field error;
- invalid JSON returns an explicit parse result without silently replacing the original text.

- [ ] **Step 2: Run the pure test and confirm failure**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --selectProjects jsdom --runInBand src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.test.ts
```

Expected: FAIL because the module does not exist.

- [ ] **Step 3: Implement immutable mapping helpers**

Expose focused functions:

```ts
export function capabilitySelectorKey(selector: StudioWorkflowCapabilitySelector): string;
export function toDocumentCapability(selector: StudioWorkflowCapabilitySelector): StudioWorkflowCapability;
export function parseToolArguments(text: unknown): ToolArgumentParseResult;
export function writeToolArgument(input: WriteToolArgumentInput): ToolArgumentWriteResult;
export function listOperationFields(readiness: StudioWorkflowCapabilityReadiness): readonly OperationField[];
```

Use `JSON.parse`/`JSON.stringify`, clone only affected objects, and preserve unknown keys. Expressions bypass literal coercion. Do not manipulate JSON with string replacement.

- [ ] **Step 4: Run the pure test and commit**

Run the Step 2 command. Expected: PASS.

Commit:

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.ts apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.test.ts
git commit -m "Model guided tool call inputs"
```

### Task 5: Guided Tool call component

**Files:**
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.tsx`
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.test.tsx`

- [ ] **Step 1: Write the novice-path component test**

Render with `scopeId="scope-alpha"`, `parameters.tool = 'nyxid_proxy'`, and no capability. Mock discovery and readiness methods. Assert:

```ts
expect(screen.getByLabelText('Action')).toBeInTheDocument();
expect(screen.queryByDisplayValue('nyxid_proxy')).not.toBeInTheDocument();
```

Select `PostHog / List dashboards`; assert the change callback receives the exact document selector and `parameters.tool = 'nyxid_proxy'`. Then resolve readiness and assert required contract fields render.

- [ ] **Step 2: Add state and accessibility tests**

Cover loading, empty, retryable discovery failure, setup blockers, selected action missing from the refreshed catalog, read-only/write/destructive badges, stale readiness responses, allowed-values select, boolean switch, JSON object editor, response mode, and disabled state. Use role/label queries rather than class selectors.

- [ ] **Step 3: Run the component test and confirm failure**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --selectProjects jsdom --runInBand src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.test.tsx
```

Expected: FAIL because the component does not exist.

- [ ] **Step 4: Implement discovery and readiness state**

Use TanStack Query with keys containing scope and exact selector. Keep discovery independent from inspector opening. Treat selection absence, network failure, empty success, setup blockers, unavailable selector, and ready as distinct rendered states. Keep any saved selector visible while discovery is pending.

- [ ] **Step 5: Implement generated inputs**

Render simple leaves with `Input`, `InputNumber`, `Switch`, or `Select`; render complex object/array values with `Input.TextArea`. Connect helper/error IDs through `aria-describedby`. Use the pure mapping helpers so every change returns a complete parameters object without deleting unknown values.

- [ ] **Step 6: Run the component and mapping tests and commit**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --selectProjects jsdom --runInBand src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.test.tsx src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.test.ts
```

Expected: PASS.

Commit:

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.test.tsx
git commit -m "Guide external tool call configuration"
```

### Task 6: Inspector information hierarchy and integration

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.test.tsx`

- [ ] **Step 1: Write failing inspector hierarchy tests**

Assert the header title is action/type-oriented, purpose text is present, raw step ID appears only after opening `Technical details`, the advanced section is named `Advanced JSON`, the footer says `Apply step`, and closing an edited inspector still prompts before discard.

- [ ] **Step 2: Run the inspector test and confirm failure**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --selectProjects jsdom --runInBand src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.test.tsx
```

Expected: FAIL against the current generic hierarchy.

- [ ] **Step 3: Integrate atomic capability changes**

Add `scopeId` to inspector props. Hold capability beside parameter text in the local draft hook. For direct `tool_call`, render `WorkflowToolCallConfiguration`; for all other node shapes, render the existing schema form. Pass `{ parametersText, capability }` to the editor callback in one apply operation.

- [ ] **Step 4: Implement presentation metadata and hierarchy**

Keep a small local registry of purpose sentences keyed by normalized step type. Use action display name when resolved, otherwise `Configure {nodeTypeLabel}`. Move the step ID, type, role, flow routing, runtime adapter, and selector IDs under `Technical details`. Rename raw disclosure to `Advanced JSON` and clarify that it edits runtime parameters.

- [ ] **Step 5: Style with existing tokens**

Extend `styles.ts` with compact status rows, risk badges, service options, grouped fields, inline help/errors, and technical values. Keep the panel width responsive, avoid nested card styling, use existing `--wa-*` tokens, and ensure narrow screens remain single-column with visible footer actions.

- [ ] **Step 6: Run all directly related frontend tests**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --selectProjects node --runInBand src/shared/studio/document.test.ts
pnpm --dir apps/aevatar-console-web jest --selectProjects jsdom --runInBand src/shared/studio/api.test.ts src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.test.ts src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.test.tsx src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.test.tsx
```

Expected: PASS.

- [ ] **Step 7: Commit the inspector integration**

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.test.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts
git commit -m "Reshape workflow step configuration"
```

### Task 7: Focused validation and pull request delivery

**Files:**
- Modify only files already listed if validation exposes task-related defects.

- [ ] **Step 1: Run the frontend scope analyzer**

Run from the repository root:

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

Use its `relatedTests` and `staticCheckFiles` output. Do not run full frontend test, lint, `tsc`, or build.

- [ ] **Step 2: Run changed-file frontend static checks**

After reading `frontend-incremental-pr/references/framework-commands.md`, run Biome only against the analyzer's `staticCheckFiles`. Run a typecheck only if the repository provides a reliable affected target; otherwise record that GitHub CI owns typecheck.

- [ ] **Step 3: Run focused backend and stability checks**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --filter "FullyQualifiedName~ScopeWorkflowEndpointsTests|FullyQualifiedName~GAgentServiceHostingServiceCollectionExtensionsTests"
bash tools/ci/test_stability_guards.sh
```

Expected: PASS.

- [ ] **Step 4: Review the final diff**

Run `git diff --check`, inspect `git diff origin/feat/2026-08-04_workflow-activity-vnext...HEAD` plus remaining working-tree changes, confirm no credentials/logging leaks, and verify only task files changed.

- [ ] **Step 5: Commit any validation fixes**

Stage only explicit task paths and use an imperative, single-purpose commit message. Do not use `git add .`.

- [ ] **Step 6: Push and create the pull request**

Push `feat/2026-09-02_workflow-configuration-guidance` and create a PR targeting `feat/2026-08-04_workflow-activity-vnext`. The PR body must include problem/solution, impact paths, exact focused commands/results, and:

```markdown
- Full frontend suite/build: deferred to GitHub CI by personal local workflow policy
```

Stop after reporting the PR URL; do not babysit CI unless requested.
