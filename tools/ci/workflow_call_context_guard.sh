#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

tool_proto="src/Aevatar.AI.ToolProviders.AevatarInvocation/aevatar_invocation_tools.proto"
dispatcher="src/Aevatar.AI.ToolProviders.AevatarInvocation/AevatarInvocationDispatcher.cs"
ai_proto="src/Aevatar.AI.Abstractions/ai_messages.proto"
workflow_messages="src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto"

start_workflow_message="$(
  awk '
    /^message StartWorkflowToolRequest[[:space:]]*\{/ { in_message = 1 }
    in_message { print }
    in_message && /^\}/ { exit }
  ' "${tool_proto}"
)"

if grep -Eq "(parent_actor_id|parent_run_id|parent_step_id|root_run_id|requested_depth|workflow_runtime_context|workflow_call_context)" <<<"${start_workflow_message}"; then
  echo "${tool_proto}"
  echo "StartWorkflowToolRequest must not expose workflow ancestry/depth fields; trusted workflow runtime context is host-stamped."
  exit 1
fi

if ! rg -n "AgentWorkflowRuntimeContextPayload workflow_runtime" "${ai_proto}" >/dev/null; then
  echo "${ai_proto}"
  echo "Agent tool execution context must carry workflow runtime as typed Protobuf context."
  exit 1
fi

if ! rg -n "string root_run_id = .*|int32 requested_depth = " "${workflow_messages}" >/dev/null; then
  echo "${workflow_messages}"
  echo "Sub-workflow invocation contract must carry typed root/depth fields."
  exit 1
fi

if ! rg -n "WorkflowToolRuntimeContextPayload workflow_runtime" "${workflow_messages}" >/dev/null; then
  echo "${workflow_messages}"
  echo "StartWorkflowEvent must hydrate child workflow runtime from a typed Protobuf field, not string parameters."
  exit 1
fi

if ! rg -n "RejectForbiddenRootFields" "${dispatcher}" >/dev/null; then
  echo "${dispatcher}"
  echo "aevatar_start_workflow must reject forged public workflow ancestry/depth fields before parsing."
  exit 1
fi

if ! rg -n "TryGetManagedWorkflowRuntimeContext|StartManagedSubWorkflowForChatRunAsync|SubWorkflowInvokeRequestedEvent" "${dispatcher}" >/dev/null; then
  echo "${dispatcher}"
  echo "Trusted workflow runtime context must route aevatar_start_workflow through parent actor managed child start."
  exit 1
fi

echo "Workflow call context guard passed."
