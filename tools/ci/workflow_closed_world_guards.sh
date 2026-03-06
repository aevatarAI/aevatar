#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

closed_world_files="$(
  rg -l "closed_world_mode:\s*true" workflows -g '*.yaml' || true
)"

if [ -n "${closed_world_files}" ]; then
  blocked_type_pattern='type:\s*(llm_call|tool_call|connector_call|bridge_call|evaluate|judge|reflect|human_input|human_approval|wait_signal|wait|emit|publish|parallel|parallel_fanout|fan_out|race|select|map_reduce|mapreduce|vote_consensus|vote|foreach|for_each|dynamic_workflow)\b'
  while IFS= read -r wf_file; do
    [ -z "${wf_file}" ] && continue
    hits="$(rg -n "${blocked_type_pattern}" "${wf_file}" || true)"
    if [ -n "${hits}" ]; then
      echo "${hits}"
      echo "closed_world_mode workflow must not use blocked primitive types."
      exit 1
    fi
  done <<< "${closed_world_files}"
fi

workflow_run_state_proto="src/workflow/Aevatar.Workflow.Core/workflow_run_state.proto"
workflow_run_agent_file="src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs"
workflow_run_actor_port="src/workflow/Aevatar.Workflow.Infrastructure/Runs/WorkflowRunActorPort.cs"

if [ ! -f "${workflow_run_state_proto}" ]; then
  echo "Workflow run actor architecture requires workflow_run_state.proto."
  exit 1
fi

if ! rg -n "message WorkflowRunState" "${workflow_run_state_proto}" >/dev/null; then
  echo "WorkflowRunState proto definition is missing."
  exit 1
fi

if ! rg -n "pending_sub_workflows|pending_child_run_ids_by_parent_run_id" "${workflow_run_state_proto}" >/dev/null; then
  echo "WorkflowRunState must persist sub-workflow child-run facts."
  exit 1
fi

if [ ! -f "${workflow_run_agent_file}" ] || ! rg -n "class WorkflowRunGAgent : GAgentBase<WorkflowRunState>" "${workflow_run_agent_file}" >/dev/null; then
  echo "WorkflowRunGAgent must remain the single-run persistent fact owner."
  exit 1
fi

if ! rg -n "CreateAsync<WorkflowRunGAgent>" "${workflow_run_actor_port}" >/dev/null; then
  echo "Workflow capability path must create WorkflowRunGAgent for accepted runs."
  exit 1
fi

if ! rg -n "SubWorkflowInvokeRequestedEvent" test -g '*Tests.cs' >/dev/null; then
  echo "Missing workflow_call request-path coverage assertion for sub-workflow invocation."
  exit 1
fi

if ! rg -n "workflow_call.child_run_id" test -g '*Tests.cs' >/dev/null; then
  echo "Missing workflow_call return-path coverage assertion for workflow run child-run tracking."
  exit 1
fi

if ! rg -n "WorkflowTuringCompletenessTests" test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs >/dev/null; then
  echo "Missing closed-world turing-completeness integration proof tests."
  exit 1
fi

echo "Closed-world workflow guards passed."
