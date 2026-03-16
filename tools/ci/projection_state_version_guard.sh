#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

FILES=(
  "src/Aevatar.Scripting.Projection/Projectors/ScriptReadModelProjector.cs"
  "src/Aevatar.Scripting.Projection/Projectors/ScriptEvolutionReadModelProjector.cs"
  "src/Aevatar.Scripting.Projection/Projectors/ScriptDefinitionSnapshotProjector.cs"
  "src/Aevatar.Scripting.Projection/Projectors/ScriptCatalogEntryProjector.cs"
  "src/Aevatar.Scripting.Projection/Projectors/ScriptNativeDocumentProjector.cs"
  "src/Aevatar.Scripting.Projection/Projectors/ScriptNativeGraphProjector.cs"
  "src/Aevatar.Scripting.Projection/Materialization/ScriptNativeDocumentMaterializer.cs"
  "src/Aevatar.Scripting.Projection/Materialization/ScriptNativeGraphMaterializer.cs"
  "src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionCurrentStateProjector.cs"
)

version_hits="$(
  rg -n "StateVersion[[:space:]]*(\\+\\+|--|\\+=|-=)|\\+\\+[[:space:]]*[A-Za-z0-9_.]+\\.StateVersion|--[[:space:]]*[A-Za-z0-9_.]+\\.StateVersion|StateVersion[[:space:]]*=[[:space:]]*1\\b" \
    "${FILES[@]}" \
    || true
)"

if [[ -n "${version_hits}" ]]; then
  echo "${version_hits}"
  echo "Current-state projection paths must not invent local StateVersion values."
  exit 1
fi

event_id_hits="$(
  rg -n "LastEventId[[:space:]]*=[[:space:]]*string\\.Concat|LastEventId[[:space:]]*=.*EventType|LastEventId[[:space:]]*=.*TypeUrl" \
    "${FILES[@]}" \
    || true
)"

if [[ -n "${event_id_hits}" ]]; then
  echo "${event_id_hits}"
  echo "Current-state projection paths must use committed event ids, not synthetic ids or type urls."
  exit 1
fi

echo "Projection state version guard passed."
