#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

targets=(
  "src/platform/Aevatar.GAgentService.Infrastructure/Adapters/StaticServiceImplementationAdapter.cs"
  "src/platform/Aevatar.GAgentService.Infrastructure/Activation/DefaultServiceRuntimeActivator.cs"
)

set +e
report="$(
  rg -n "Type\.GetType|AppDomain\.GetAssemblies" "${targets[@]}"
)"
status=$?
set -e

if [[ ${status} -ne 0 && ${status} -ne 1 ]]; then
  echo "Static service activation guard execution failed."
  exit "${status}"
fi

if [[ -n "${report}" ]]; then
  echo "${report}"
  echo "Static service activation must use IAgentKindRegistry + CreateByKindAsync, not CLR reflection."
  exit 1
fi

echo "Static service activation guard passed."
