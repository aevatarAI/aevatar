#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

script_lifecycle_contract="src/Aevatar.Scripting.Core/Ports/IScriptLifecyclePort.cs"
script_lifecycle_facade="src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptLifecyclePort.cs"
evolution_service_file="src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptEvolutionLifecycleService.cs"
hosting_di_file="src/Aevatar.Scripting.Hosting/DependencyInjection/ServiceCollectionExtensions.cs"

if [ -f "${script_lifecycle_contract}" ] || [ -f "${script_lifecycle_facade}" ]; then
  echo "Scripting capability must not reintroduce private lifecycle total-port facades."
  exit 1
fi

if ! rg -n "ICommandInteractionService<ScriptEvolutionProposal,\s*ScriptEvolutionAcceptedReceipt,\s*ScriptEvolutionStartError,\s*ScriptEvolutionSessionCompletedEvent,\s*ScriptEvolutionInteractionCompletion>" "${evolution_service_file}" >/dev/null; then
  echo "${evolution_service_file}"
  echo "RuntimeScriptEvolutionLifecycleService must depend on the generic CQRS interaction service."
  exit 1
fi

if rg -n "IScriptEvolutionProjectionLifecyclePort|IScriptEvolutionDecisionFallbackPort|RuntimeScriptActorAccessor|IScriptingActorAddressResolver|EnsureAndAttachAsync|TryResolveAsync|DispatchAsync\(" "${evolution_service_file}"; then
  echo "${evolution_service_file}"
  echo "RuntimeScriptEvolutionLifecycleService must not manually orchestrate projection/fallback/dispatch concerns."
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

echo "Scripting interaction boundary guard passed."
