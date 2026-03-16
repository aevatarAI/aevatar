#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

runtime_provisioning_file="src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptProvisioningService.cs"
runtime_port_file="src/Aevatar.Scripting.Core/Ports/IScriptRuntimeProvisioningPort.cs"

hits="$(
  rg -n "IScriptDefinitionSnapshotPort|WaitForSnapshotAsync|ProjectionObservationTimeout|ProjectionObservationPollInterval|Task\\.Delay\\(" \
    "${runtime_provisioning_file}" \
    || true
)"

if [[ -n "${hits}" ]]; then
  echo "${hits}"
  echo "Runtime script provisioning must not query/poll definition snapshot read models."
  exit 1
fi

if ! rg -n "ScriptDefinitionSnapshot definitionSnapshot" "${runtime_port_file}" >/dev/null; then
  echo "${runtime_port_file}"
  echo "IScriptRuntimeProvisioningPort must require an explicit ScriptDefinitionSnapshot."
  exit 1
fi

echo "Scripting runtime snapshot guard passed."
