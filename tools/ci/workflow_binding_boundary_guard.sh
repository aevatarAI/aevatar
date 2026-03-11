#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

workflow_src_root="src/workflow"
workflow_port_file="src/workflow/Aevatar.Workflow.Application.Abstractions/Runs/IWorkflowRunActorPort.cs"
workflow_resolver_file="src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunActorResolver.cs"
workflow_registry_file="src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionRegistry.cs"
workflow_endpoint_file="src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatEndpoints.cs"
workflow_application_di_file="src/workflow/Aevatar.Workflow.Application/DependencyInjection/ServiceCollectionExtensions.cs"

if rg -n "IActorStateProbe|ActorStateSnapshot|AgentStateSnapshotInspector|GetStateSnapshotAsync\\(" "${workflow_src_root}"; then
  echo "Workflow main path must not depend on generic raw-state probing. Use narrow workflow binding contracts."
  exit 1
fi

if rg -n "Task<IActor\\?>\\s+GetAsync\\(|DescribeAsync\\(|IsWorkflowDefinitionActorAsync\\(|IsWorkflowRunActorAsync\\(|GetBoundWorkflowNameAsync\\(" "${workflow_port_file}"; then
  echo "IWorkflowRunActorPort must remain write-only. Read-side binding inspection belongs to IWorkflowActorBindingReader."
  exit 1
fi

if ! rg -n "IWorkflowActorBindingReader" "${workflow_resolver_file}" >/dev/null; then
  echo "${workflow_resolver_file}"
  echo "WorkflowRunActorResolver must depend on IWorkflowActorBindingReader."
  exit 1
fi

if rg -n "_actorPort\\.(GetAsync|DescribeAsync|IsWorkflowDefinitionActorAsync|IsWorkflowRunActorAsync|GetBoundWorkflowNameAsync)\\(" "${workflow_resolver_file}"; then
  echo "WorkflowRunActorResolver must not read actor binding through write-side port methods."
  exit 1
fi

if ! rg -n "WorkflowDefinitionActorId\\.Format\\(normalizedName\\)" "${workflow_registry_file}" >/dev/null; then
  echo "${workflow_registry_file}"
  echo "Registry-backed workflow definitions must allocate canonical reusable definition actor ids."
  exit 1
fi

if rg -n "\\[FromServices\\] IActorRuntime|\\[FromServices\\] IActorDispatchPort|dispatchPort\\.DispatchAsync\\(" "${workflow_endpoint_file}" >/dev/null; then
  echo "${workflow_endpoint_file}"
  echo "Workflow capability endpoints must not bypass CQRS command dispatch with direct runtime/dispatch usage."
  exit 1
fi

if ! rg -n "ICommandDispatchService<WorkflowResumeCommand,\s*WorkflowRunControlAcceptedReceipt,\s*WorkflowRunControlStartError>" "${workflow_endpoint_file}" >/dev/null; then
  echo "${workflow_endpoint_file}"
  echo "Workflow resume endpoint must depend on the CQRS command dispatch service."
  exit 1
fi

if ! rg -n "ICommandDispatchService<WorkflowSignalCommand,\s*WorkflowRunControlAcceptedReceipt,\s*WorkflowRunControlStartError>" "${workflow_endpoint_file}" >/dev/null; then
  echo "${workflow_endpoint_file}"
  echo "Workflow signal endpoint must depend on the CQRS command dispatch service."
  exit 1
fi

if ! rg -n "ICommandDispatchService<WorkflowResumeCommand,\s*WorkflowRunControlAcceptedReceipt,\s*WorkflowRunControlStartError>" "${workflow_application_di_file}" >/dev/null; then
  echo "${workflow_application_di_file}"
  echo "Workflow application DI must register the CQRS resume command path."
  exit 1
fi

if ! rg -n "ICommandDispatchService<WorkflowSignalCommand,\s*WorkflowRunControlAcceptedReceipt,\s*WorkflowRunControlStartError>" "${workflow_application_di_file}" >/dev/null; then
  echo "${workflow_application_di_file}"
  echo "Workflow application DI must register the CQRS signal command path."
  exit 1
fi

echo "Workflow binding boundary guards passed."
