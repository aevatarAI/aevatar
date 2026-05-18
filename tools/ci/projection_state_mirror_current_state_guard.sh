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
  "src/Aevatar.CQRS.Projection.Core/Orchestration/CurrentStateProjectionMaterializer.cs"
)

EXISTING_FILES=()
for file in "${FILES[@]}"; do
  if [[ -f "${file}" ]]; then
    EXISTING_FILES+=("${file}")
  fi
done

legacy_reader_hits="$(
  rg -n "IProjectionDocumentReader<|_documentReader|IProjectionEventReducer<|_reducersByType|EventEnvelopeTimestampResolver\\.Resolve\\(" \
    "${EXISTING_FILES[@]}" \
    || true
)"

if [[ -n "${legacy_reader_hits}" ]]; then
  echo "${legacy_reader_hits}"
  echo "Current-state projector paths must not read old readmodels, use reducers, or rely on raw envelope timestamp helpers."
  exit 1
fi

missing_committed_hits="$(
  for file in "${EXISTING_FILES[@]}"; do
    if ! rg -q "CommittedStateEventEnvelope\\.(TryUnpackState<|TryGetObservedPayload\\()" "${file}"; then
      echo "${file}"
    fi
  done
)"

if [[ -n "${missing_committed_hits}" ]]; then
  echo "${missing_committed_hits}"
  echo "Current-state projectors must materialize from committed state envelopes."
  exit 1
fi

echo "Projection state mirror current-state guard passed."
