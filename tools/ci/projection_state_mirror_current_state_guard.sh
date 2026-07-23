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
  "src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionCurrentStateProjector.cs"
  "src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileNamespaceCurrentStateProjector.cs"
  "src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileOwnerCurrentStateProjector.cs"
  "src/platform/Aevatar.GAgentService.Projection/AgentProfiles/AgentProfileExecutionCurrentStateProjector.cs"
)
MAPPED_CURRENT_STATE_HELPER="src/Aevatar.CQRS.Projection.Core/Orchestration/MappedCurrentStateProjectionMaterializer.cs"
WORKFLOW_BINDING_PROJECTOR="src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowActorBindingProjector.cs"

legacy_reader_hits="$(
  rg -n "IProjectionDocumentReader<|_documentReader|IProjectionEventReducer<|_reducersByType|EventEnvelopeTimestampResolver\\.Resolve\\(" \
    "${FILES[@]}" \
    || true
)"

if [[ -n "${legacy_reader_hits}" ]]; then
  echo "${legacy_reader_hits}"
  echo "Current-state projector paths must not read old readmodels, use reducers, or rely on raw envelope timestamp helpers."
  exit 1
fi

binding_reader_hits="$(
  rg -n "IProjectionDocumentReader<|_documentReader|GetOrCreateAsync|\\.GetAsync\\(" \
    "${WORKFLOW_BINDING_PROJECTOR}" \
    || true
)"

if [[ -n "${binding_reader_hits}" ]]; then
  echo "${binding_reader_hits}"
  echo "Workflow actor binding projector must construct full documents from committed event payloads without reading prior readmodels."
  exit 1
fi

missing_committed_hits="$(
  for file in "${FILES[@]}"; do
    if rg -q "CommittedStateEventEnvelope\\.(TryUnpackState<|TryGetObservedPayload\\()" "${file}"; then
      continue
    fi

    if rg -q "MappedCurrentStateProjectionMaterializer<" "${file}" &&
       rg -q "CommittedStateEventEnvelope\\.TryUnpackState<" "${MAPPED_CURRENT_STATE_HELPER}"; then
      continue
    fi

    echo "${file}"
  done
)"

if [[ -n "${missing_committed_hits}" ]]; then
  echo "${missing_committed_hits}"
  echo "Current-state projectors must materialize from committed state envelopes."
  exit 1
fi

echo "Projection state mirror current-state guard passed."
