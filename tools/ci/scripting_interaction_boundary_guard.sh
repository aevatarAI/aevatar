#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

script_lifecycle_contract="src/Aevatar.Scripting.Core/Ports/IScriptLifecyclePort.cs"
script_lifecycle_facade="src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptLifecyclePort.cs"
script_command_adapter_base="src/Aevatar.Scripting.Infrastructure/Ports/ScriptActorCommandPortBase.cs"
script_definition_lifecycle="src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptDefinitionLifecycleService.cs"
script_execution_lifecycle="src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptExecutionLifecycleService.cs"
script_definition_actor_request="src/Aevatar.Scripting.Application/Application/UpsertScriptDefinitionActorRequest.cs"
script_runtime_actor_request="src/Aevatar.Scripting.Application/Application/RunScriptActorRequest.cs"
script_catalog_promote_actor_request="src/Aevatar.Scripting.Application/Application/PromoteScriptRevisionActorRequest.cs"
script_catalog_rollback_actor_request="src/Aevatar.Scripting.Application/Application/RollbackScriptRevisionActorRequest.cs"
script_definition_actor_request_adapter="src/Aevatar.Scripting.Application/Application/UpsertScriptDefinitionActorRequestAdapter.cs"
script_runtime_actor_request_adapter="src/Aevatar.Scripting.Application/Application/RunScriptActorRequestAdapter.cs"
script_catalog_promote_actor_request_adapter="src/Aevatar.Scripting.Application/Application/PromoteScriptRevisionActorRequestAdapter.cs"
script_catalog_rollback_actor_request_adapter="src/Aevatar.Scripting.Application/Application/RollbackScriptRevisionActorRequestAdapter.cs"
script_definition_query_adapter="src/Aevatar.Scripting.Application/Application/QueryScriptDefinitionSnapshotRequestAdapter.cs"
script_catalog_query_adapter="src/Aevatar.Scripting.Application/Application/QueryScriptCatalogEntryRequestAdapter.cs"
script_evolution_query_adapter="src/Aevatar.Scripting.Application/Application/QueryScriptEvolutionDecisionRequestAdapter.cs"
evolution_service_file="src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionInteractionService.cs"
hosting_di_file="src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs"

if [ -f "${script_lifecycle_contract}" ] || [ -f "${script_lifecycle_facade}" ] || [ -f "${script_command_adapter_base}" ] || [ -f "${script_definition_lifecycle}" ] || [ -f "${script_execution_lifecycle}" ]; then
  echo "Scripting capability must not reintroduce private lifecycle total-port facades or direct-dispatch adapter bases."
  exit 1
fi

if [ -f "${script_definition_actor_request}" ] || [ -f "${script_runtime_actor_request}" ] || [ -f "${script_catalog_promote_actor_request}" ] || [ -f "${script_catalog_rollback_actor_request}" ] || [ -f "${script_definition_actor_request_adapter}" ] || [ -f "${script_runtime_actor_request_adapter}" ] || [ -f "${script_catalog_promote_actor_request_adapter}" ] || [ -f "${script_catalog_rollback_actor_request_adapter}" ]; then
  echo "Scripting capability must not reintroduce legacy ActorRequest/ActorRequestAdapter command wrappers."
  exit 1
fi

if [ -f "${script_definition_query_adapter}" ] || [ -f "${script_catalog_query_adapter}" ] || [ -f "${script_evolution_query_adapter}" ]; then
  echo "Scripting capability must not reintroduce per-query request adapter wrappers."
  exit 1
fi

if ! rg -n "ICommandInteractionService<ScriptEvolutionProposal,\s*ScriptEvolutionAcceptedReceipt,\s*ScriptEvolutionStartError,\s*ScriptEvolutionSessionCompletedEvent,\s*ScriptEvolutionInteractionCompletion>" "${evolution_service_file}" >/dev/null; then
  echo "${evolution_service_file}"
  echo "RuntimeScriptEvolutionInteractionService must depend on the generic CQRS interaction service."
  exit 1
fi

if rg -n "IScriptEvolutionProjectionPort|IScriptEvolutionDecisionFallbackPort|RuntimeScriptActorAccessor|IScriptingActorAddressResolver|EnsureAndAttachAsync|TryResolveAsync|DispatchAsync\(" "${evolution_service_file}"; then
  echo "${evolution_service_file}"
  echo "RuntimeScriptEvolutionInteractionService must not manually orchestrate projection/fallback/dispatch concerns."
  exit 1
fi

if ! rg -n "services\.AddCqrsCore\(" "${hosting_di_file}" >/dev/null; then
  echo "${hosting_di_file}"
  echo "Scripting hosting must compose CQRS Core before wiring evolution interaction."
  exit 1
fi

if ! rg -n "ICommandInteractionService<ScriptEvolutionProposal,\s*ScriptEvolutionAcceptedReceipt,\s*ScriptEvolutionStartError,\s*ScriptEvolutionSessionCompletedEvent,\s*ScriptEvolutionInteractionCompletion>" "${hosting_di_file}" >/dev/null; then
  echo "${hosting_di_file}"
  echo "Scripting hosting must register the generic evolution interaction service."
  exit 1
fi

if ! rg -n "IScriptRuntimeProvisioningPort" "${hosting_di_file}" >/dev/null; then
  echo "${hosting_di_file}"
  echo "Scripting hosting must register the runtime provisioning port separately from runtime command dispatch."
  exit 1
fi

echo "Scripting interaction boundary guard passed."
