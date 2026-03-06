#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

workflow_definition_state="src/workflow/Aevatar.Workflow.Core/workflow_state.proto"
workflow_definition_actor="src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs"
workflow_core_pack="src/workflow/Aevatar.Workflow.Core/WorkflowCoreModulePack.cs"
workflow_module_pack_contract="src/workflow/Aevatar.Workflow.Core/IWorkflowModulePack.cs"
workflow_composition_dir="src/workflow/Aevatar.Workflow.Core/Composition"
workflow_run_coverage="test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs"
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

if rg -n "WorkflowModuleRegistration.Create<(WorkflowLoopModule|WhileModule|WorkflowCallModule|ParallelFanOutModule|ForEachModule|RaceModule|MapReduceModule|LLMCallModule|WaitSignalModule|EvaluateModule|ReflectModule|DelayModule|CacheModule|HumanApprovalModule|HumanInputModule)>" "${workflow_core_pack}"; then
  echo "WorkflowCoreModulePack must not register stateful runtime modules; those are owned by WorkflowRunGAgent."
  exit 1
fi

if rg -n "DependencyExpanders|Configurators" "${workflow_module_pack_contract}" "${workflow_core_pack}" \
  "src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/MakerModulePack.cs" \
  "demos/Aevatar.Demos.Workflow.Web/DemoWorkflowModulePack.cs"
then
  echo "Workflow module packs must expose only stateless Modules registrations."
  exit 1
fi

if [ -d "${workflow_composition_dir}" ] && rg --files "${workflow_composition_dir}" | rg -q "\.cs$"; then
  echo "Legacy workflow composition extension points are forbidden."
  exit 1
fi

legacy_stateful_modules="$(
  rg --files src/workflow/Aevatar.Workflow.Core/Modules \
    | rg '/(WorkflowLoopModule|LLMCallModule|ParallelFanOutModule|ForEachModule|WhileModule|WaitSignalModule|DelayModule|HumanApprovalModule|HumanInputModule|MapReduceModule|RaceModule|ReflectModule|EvaluateModule|CacheModule)\\.cs$' || true
)"

if [ -n "${legacy_stateful_modules}" ]; then
  echo "${legacy_stateful_modules}"
  echo "Legacy stateful workflow runtime modules must stay deleted."
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
