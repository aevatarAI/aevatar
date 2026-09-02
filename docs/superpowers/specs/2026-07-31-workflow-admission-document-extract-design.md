# Workflow Admission and Document Extraction Context Design

## Problem

Two production workflow paths still accept or dispatch incomplete contracts:

1. A workflow can author a direct connected-service tool name such as
   `nyxid_api-lark-bot-2__bitable_records_list`. Binding succeeds because the
   authorization dependency compiler treats every static tool other than
   `nyxid_proxy` as an internal tool. Runtime later fails with `tool not found`
   because direct connected-service tool names are no longer executable.
2. `document_extract` invokes its image-capable LLM provider without the
   workflow caller credential or the workflow LLM controls. The NyxID provider
   therefore receives neither the caller bearer nor the model/route selection
   that was already resolved for the workflow run.

## Design

### Reject legacy direct NyxID tool names during admission compilation

Keep `nyxid_proxy` as the only admitted NyxID connected-service execution
surface. In the shared workflow external-invocation compiler, detect a static
tool name whose normalized form begins with `nyxid_` and contains `__`. Reject
that call site with the existing typed readiness blocker
`NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED` and the existing rebind
remediation.

The check belongs after static tool-name validation and before the compiler
classifies a non-`nyxid_proxy` tool as internal. Ordinary steps, nested steps,
and synthesized foreach/while sub-steps already converge on this compiler, so
one check covers every authoring shape. The canonical `nyxid_proxy` path and
unrelated internal tools remain unchanged.

### Carry the run's typed LLM context into `document_extract`

Extend `WorkflowToolExecutionRequest` with an optional cloned
`WorkflowLlmControlContext`. `ToolCallModule`, which already resolves the
trusted workflow caller credential, reads the run's existing LLM execution
context and creates this request field from the typed model override, route
preference, max-tool-round override, and user-memory prompt. No new state or
serialization format is introduced.

`WorkflowDocumentExtractToolSource` applies the execution request context to
both provider-backed request builders: plain image text extraction and
schema-bound extraction. Each resulting `LLMRequest` receives:

- `CallerContext.ScopeId` and `CallerContext.OwnerSubject` from the workflow
  request scope;
- `CallerContext.Credentials.NyxIdBearer` from the already resolved
  `WorkflowCallerCredential.BearerToken`;
- `LlmControl.ModelOverride`, `NyxIdRoutePreference`,
  `MaxToolRoundsOverride`, and `UserMemoryPrompt` from the workflow LLM
  context; and
- `LlmControl.SenderNyxIdAccessToken` from the typed workflow LLM context.

Blank values normalize to `null`. The bearer is not copied to metadata or
logs. Provider failures keep the existing sanitized workflow error codes and
messages. Providers that do not require a bearer continue to receive a valid
request with absent credentials.

## Boundaries

- Do not add another provider pipeline, provider factory, host-token fallback,
  credential store, or string-keyed metadata convention.
- Do not make Workflow Core depend on the NyxID tool-provider assembly. The
  legacy-name admission rule is a workflow authoring contract.
- Do not make Workflow Infrastructure depend on the internal
  `WorkflowCallerCredentialToolContextMapper` in Workflow Integration AI. The
  document tool maps only the narrow `LLMRequest` fields it consumes.
- Do not change non-provider-backed PDF, DOCX, or UTF-8 text extraction.

## Error Behavior

- Binding a direct `nyxid_*__*` tool fails closed with
  `NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED`; it must not create a serving
  revision.
- Canonical `nyxid_proxy` declarations retain their existing validation and
  admission behavior.
- `document_extract` keeps its existing `image_extraction_failed` and
  `schema_bound_extraction_failed` envelopes when a provider rejects or fails
  the request. Secrets are never included in the result.

## Verification

- Workflow Core tests cover ordinary and synthesized direct connected-service
  tool names, assert the typed migration blocker, and preserve canonical
  `nyxid_proxy` behavior.
- Tool-call module tests prove that the run's typed LLM execution context is
  copied into `WorkflowToolExecutionRequest`.
- Document extraction tests capture provider `LLMRequest` values for plain
  image and schema-bound image paths and assert scope, owner subject, caller
  bearer, model override, route preference, max-tool-round override, user
  memory prompt, and sender token.
- Run affected project tests, test stability and workflow binding guards,
  architecture/doc guards, solution build/test, and `git diff --check`.
- After rebasing and pushing to `origin/feature/integrate`, use `nyxid` CLI to
  verify that legacy binding fails with the typed blocker and a real PNG
  attachment completes image extraction.
