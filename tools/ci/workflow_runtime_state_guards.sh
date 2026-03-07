#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

workflow_definition_state="src/workflow/Aevatar.Workflow.Core/workflow_state.proto"
workflow_definition_actor="src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs"
workflow_core_pack="src/workflow/Aevatar.Workflow.Core/WorkflowCorePrimitivePack.cs"
workflow_primitive_pack_contract="src/workflow/Aevatar.Workflow.Core/IWorkflowPrimitivePack.cs"
workflow_composition_dir="src/workflow/Aevatar.Workflow.Core/Composition"
workflow_run_coverage="test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs"
workflow_primitive_executor_dir="src/workflow/Aevatar.Workflow.Core/PrimitiveExecutors"
current_workflow_docs=(
  "docs/WORKFLOW.md"
  "src/workflow/README.md"
  "src/workflow/Aevatar.Workflow.Core/README.md"
)

if ! rg -n "message WorkflowStateUpdatedEvent" "${workflow_definition_state}" >/dev/null; then
  echo "Workflow definition actor state must be persisted via WorkflowStateUpdatedEvent."
  exit 1
fi

if rg -n "pending_sub_workflow|pending_child_run_ids_by_parent_run_id|SubWorkflowBinding|PendingSubWorkflowInvocation|ChildRunIdSet" "${workflow_definition_state}"; then
  echo "WorkflowState must not contain run-scope sub-workflow pending facts."
  exit 1
fi

if rg -n "SetModules\\(|HandleSubWorkflowInvokeRequested|SubWorkflowOrchestrator|InitializeRoleAgentEvent|StepRequestEvent|StepCompletedEvent" "${workflow_definition_actor}"; then
  echo "WorkflowGAgent must stay definition-only and must not execute workflow steps directly."
  exit 1
fi

if ! rg -n "CreateAsync<WorkflowRunGAgent>" "${workflow_definition_actor}" >/dev/null; then
  echo "WorkflowGAgent must create WorkflowRunGAgent for accepted runs."
  exit 1
fi

if rg -n "WorkflowPrimitiveRegistration.Create<(WorkflowLoopPrimitiveExecutor|WhilePrimitiveExecutor|WorkflowCallPrimitiveExecutor|ParallelFanOutPrimitiveExecutor|ForEachPrimitiveExecutor|RacePrimitiveExecutor|MapReducePrimitiveExecutor|LLMCallPrimitiveExecutor|WaitSignalPrimitiveExecutor|EvaluatePrimitiveExecutor|ReflectPrimitiveExecutor|DelayPrimitiveExecutor|CachePrimitiveExecutor|HumanApprovalPrimitiveExecutor|HumanInputPrimitiveExecutor)>" "${workflow_core_pack}"; then
  echo "WorkflowCorePrimitivePack must not register stateful runtime executors; those are owned by WorkflowRunGAgent."
  exit 1
fi

if rg -n "DependencyExpanders|Configurators" "${workflow_primitive_pack_contract}" "${workflow_core_pack}" \
  "src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/MakerPrimitivePack.cs" \
  "demos/Aevatar.Demos.Workflow.Web/DemoWorkflowPrimitivePack.cs"
then
  echo "Workflow primitive packs must expose only stateless executor registrations."
  exit 1
fi

if [ -d "${workflow_composition_dir}" ] && rg --files "${workflow_composition_dir}" | rg -q "\.cs$"; then
  echo "Legacy workflow composition extension points are forbidden."
  exit 1
fi

legacy_stateful_modules="$(
  rg --files "${workflow_primitive_executor_dir}" \
    | rg '/(WorkflowLoopPrimitiveExecutor|LLMCallPrimitiveExecutor|ParallelFanOutPrimitiveExecutor|ForEachPrimitiveExecutor|WhilePrimitiveExecutor|WaitSignalPrimitiveExecutor|DelayPrimitiveExecutor|HumanApprovalPrimitiveExecutor|HumanInputPrimitiveExecutor|MapReducePrimitiveExecutor|RacePrimitiveExecutor|ReflectPrimitiveExecutor|EvaluatePrimitiveExecutor|CachePrimitiveExecutor)\\.cs$' || true
)"

if [ -n "${legacy_stateful_modules}" ]; then
  echo "${legacy_stateful_modules}"
  echo "Legacy stateful workflow runtime executors must stay deleted."
  exit 1
fi

if rg -n "WorkflowLoopModule|IWorkflowModuleDependencyExpander|IWorkflowModuleConfigurator|WorkflowLoopModuleConfigurator|WorkflowLoopModuleDependencyExpander" "${current_workflow_docs[@]}"; then
  echo "Current workflow docs must describe the actorized run architecture, not the removed loop-module/composition model."
  exit 1
fi

if ! rg -n "WorkflowRunGAgent|runActorId|resumeToken|waitToken" "${current_workflow_docs[@]}" >/dev/null; then
  echo "Current workflow docs must explain run-actor ownership and tokenized resume/signal semantics."
  exit 1
fi

if ! rg -n "WorkflowRunGAgentCoverageTests|WorkflowGAgentCoverageTests" "${workflow_run_coverage}" >/dev/null; then
  echo "Missing workflow actor split coverage tests."
  exit 1
fi

echo "Workflow runtime state guards passed."
