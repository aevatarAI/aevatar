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

if ! rg -n "SubWorkflowInvokeRequestedEvent" test/Aevatar.Integration.Tests/WorkflowCoreModulesCoverageTests.cs >/dev/null; then
  echo "Missing workflow_call request-path coverage assertion in WorkflowCoreModulesCoverageTests."
  exit 1
fi

if ! rg -n "workflow_call.child_run_id" test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs >/dev/null; then
  echo "Missing workflow_call return-path coverage assertion in WorkflowGAgentCoverageTests."
  exit 1
fi

if ! rg -n "WorkflowTuringCompletenessTests" test/Aevatar.Integration.Tests/WorkflowTuringCompletenessTests.cs >/dev/null; then
  echo "Missing closed-world turing-completeness integration proof tests."
  exit 1
fi

echo "Closed-world workflow guards passed."
