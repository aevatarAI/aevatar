# Workflow Tool Failure Outcome Design

## Problem

`ToolCallModule` currently treats every normally returned
`WorkflowToolExecutionResult` as successful. This conflates transport completion
with business success. NyxID proxy tools return non-2xx responses as JSON such as
`{"error":true,"status":503,...}` and expose a provider-owned
`AgentToolReceiptStatus.Error`, but `AgentWorkflowToolSourceAdapter` discards that
status when it maps the agent tool result into the workflow contract. Several
workflow-native tools have the same defect because their `Error(...)` helpers
return `WorkflowToolExecutionResult.Success(...)`.

The result is a false-success chain: `WorkflowToolCallCompletedEvent` and
`StepCompletedEvent` are published with `Success=true`, so the execution kernel
never enters retry, `on_error`, compensation, or terminal failure handling.

## Semantic Decision

Tool execution and workflow execution use one explicit typed outcome. A tool
adapter owns interpretation of its external protocol and returns either a typed
success or a typed failure. `ToolCallModule` consumes that outcome without
inspecting arbitrary result JSON.

`WorkflowToolExecutionResult` gains an optional strongly named failure outcome
with a stable error code and safe error message. Its factories are:

- `Success(resultJson, managedHandoff)` for a successful tool result;
- `Failed(resultJson, errorCode, errorMessage)` for a callee-confirmed failure;
- the existing pending-approval representation remains neither success nor
  failure until the approval continuation executes the tool.

The result JSON remains an output payload. It is not a control-plane status and
must not be parsed by Workflow Core to infer success. A legitimate successful
payload containing an `error` field therefore remains successful unless its
provider or workflow-native tool explicitly returns a typed failure.

## Boundary Mapping

`AgentWorkflowToolSourceAdapter` finalizes the provider-owned
`AgentToolReceipt` and maps receipt status as follows. Providers whose
classification depends on execution-only facts may return the receipt together
with the result; existing tools retain post-result `CreateResultReceipt`.
Both paths use the same finalizer:

- `Success` becomes `WorkflowToolExecutionResult.Success`;
- `Error`, `Denied`, and `AuthorizationRequired` become
  `WorkflowToolExecutionResult.Failed`;
- a failure uses the receipt's sanitized `ResultJson`, `ErrorCode`, and
  `ErrorMessage`; it never copies a raw downstream response into the run error;
- approval pending continues through the existing typed suspension path.

NyxID's legacy proxy path implements `CreateResultReceipt` from explicit route
arguments and the documented outer proxy envelope. Proof-bound proxy execution
instead returns an execution-time receipt because the proof owns route identity
and only the HTTP/file-ingress boundary knows whether dispatch succeeded. This
design reuses that provider boundary and does not duplicate NyxID or HTTP rules
in Workflow Core.

Workflow-native document extraction, spreadsheet extraction, connected-service
resource fetch, and file submission tools change their existing `Error(...)`
helpers to return the typed failure factory. Their current structured JSON error
payloads remain available as step output while their safe detail becomes the run
error.

## Runtime Flow

For both initial execution and approved replay, `ToolCallModule` handles the
typed result through one outcome publication path:

1. A success publishes `WorkflowToolCallCompletedEvent.Success=true` and, unless
   it is a managed handoff, `StepCompletedEvent.Success=true`.
2. A failure publishes `WorkflowToolCallCompletedEvent.Success=false` with the
   safe error and result JSON.
3. The same failure publishes `StepCompletedEvent.Success=false`, preserving the
   result JSON as output and the safe message as error.
4. `WorkflowExecutionKernel` consumes the failed step through its existing
   `retry -> on_error -> compensation/terminal failure` ordering.
5. Existing committed step and workflow events drive projections and UI status;
   no frontend or projector parses `output.error` to override success.

The error shown on a failed run includes the tool name and the typed safe error.
For example, a NyxID 503 produces a failed step and run whose error identifies
the tool failure without exposing the downstream response body.

## Schedule Semantics

`ScheduledDispatchGAgent.Dispatched` remains a transport-admission fact. Its
receipt only promises that the target actor inbox accepted the invocation, in
accordance with the repository's ACK contract. This change makes the resulting
workflow run truthful but does not redefine schedule `failureCount` as an
execution-outcome counter.

Associating an asynchronous run completion back to a schedule fire requires a
separate typed continuation/observation contract with fire correlation. That is
outside this fix and must not be approximated with synchronous waiting, read
model polling, or JSON inspection.

## Compatibility And Migration

This is an intentional upgrade-forward behavior correction. Runs whose tools
already declare a typed failure will change from completed/success to the
configured retry or `on_error` behavior, or to failed when no recovery policy
handles the error. Successful tool results remain unchanged.

The `IWorkflowTool.ExecuteAsync` signature does not change. Repository-owned
producers migrate from ambiguous success-wrapped error payloads to the failure
factory. External implementations continue to compile when they construct a
normal success result, but must opt into the typed failure outcome to affect
workflow control flow.

## Tests And Verification

Regression coverage will prove:

- a direct typed failure publishes failed tool and step completion events with
  safe error and preserved result output;
- an approved replay that returns a typed failure follows the same failed path;
- an agent tool error receipt maps to a typed workflow failure;
- a normal agent tool result, including arbitrary JSON fields, remains success;
- a provider-classified NyxID-style 503 reaches the workflow run as a terminal
  failure when no recovery policy is configured;
- ordinary 2xx/normal results remain successful;
- workflow-native error helpers return typed failures.

Verification includes the focused workflow tests, affected infrastructure and
NyxID tests, the complete solution build and test suite, test stability guards,
architecture guards, and documentation lint.
