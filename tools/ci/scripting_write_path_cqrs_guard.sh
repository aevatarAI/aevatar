#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

write_path_files=(
  "src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptDefinitionCommandService.cs"
  "src/Aevatar.Scripting.Infrastructure/Ports/RuntimeScriptCatalogCommandService.cs"
  "src/Aevatar.Scripting.Application/Runtime/ScriptBehaviorRuntimeCapabilities.cs"
  "src/Aevatar.Scripting.Application/Runtime/ScriptBehaviorRuntimeCapabilityFactory.cs"
)

hits="$(
  rg -n \
    "IScriptAuthorityReadModelActivationPort|IScriptAuthorityProjectionPrimingPort|IScriptCatalogQueryPort|IScriptReadModelQueryPort|GetCatalogEntryAsync\(|WaitForSnapshotAsync|ProjectionObservationTimeout|ProjectionObservationPollInterval|Task\\.Delay\\(" \
    "${write_path_files[@]}" \
    || true
)"

if [[ -n "${hits}" ]]; then
  echo "${hits}"
  echo "Scripting write paths must not depend on authority projection activation or query-port catch-up."
  exit 1
fi

echo "Scripting write-path CQRS guard passed."
