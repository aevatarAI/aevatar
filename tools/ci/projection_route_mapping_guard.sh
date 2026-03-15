#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

route_mapping_violations=""

# Reducer base contracts: EventTypeUrl must come from protobuf Any.TypeUrl and compare with ordinal equality.
route_mapping_base_files=(
  "src/workflow/Aevatar.Workflow.Projection/Reducers/WorkflowExecutionReportArtifactReducerBase.cs"
  "src/Aevatar.AI.Projection/Reducers/ProjectionEventApplierReducerBase.cs"
  "demos/Aevatar.Demos.CaseProjection/Reducers/CaseProjectionEventReducerBase.cs"
)

for base_file in "${route_mapping_base_files[@]}"; do
  if [ ! -f "${base_file}" ]; then
    continue
  fi

  if ! rg -n "Any\.Pack\(new TEvent\(\)\)\.TypeUrl" "${base_file}" >/dev/null; then
    route_mapping_violations="${route_mapping_violations}${base_file}:missing Any.Pack(new TEvent()).TypeUrl contract\n"
  fi

  if ! rg -n "string\.Equals\(payload\.TypeUrl.*StringComparison\.Ordinal\)" "${base_file}" >/dev/null; then
    route_mapping_violations="${route_mapping_violations}${base_file}:missing ordinal TypeUrl equality check\n"
  fi
done

# Concrete reducers must inherit approved reducer base classes and must not define manual EventTypeUrl.
reducer_route_contracts=(
  "src/workflow/Aevatar.Workflow.Projection/Reducers|WorkflowExecutionReportArtifactReducerBase<"
  "src/Aevatar.AI.Projection/Reducers|ProjectionEventApplierReducerBase<"
  "demos/Aevatar.Demos.CaseProjection/Reducers|CaseProjectionEventReducerBase<"
  "demos/Aevatar.Demos.CaseProjection.Extensions.Sla/Reducers|CaseProjectionEventReducerBase<"
)

for contract in "${reducer_route_contracts[@]}"; do
  root="${contract%%|*}"
  expected_base="${contract#*|}"

  if [ ! -d "${root}" ]; then
    continue
  fi

  while IFS= read -r file; do
    [ -z "${file}" ] && continue

    concrete_reducer_lines="$(rg -n "^\s*(public|internal)\s+(sealed\s+)?class\s+[A-Za-z0-9_]*Reducer[A-Za-z0-9_]*(<[^>]+>)?\b" "${file}" || true)"
    if [ -z "${concrete_reducer_lines}" ]; then
      continue
    fi

    if ! rg -n "${expected_base}" "${file}" >/dev/null; then
      line_no="$(echo "${concrete_reducer_lines}" | head -n1 | cut -d: -f1)"
      route_mapping_violations="${route_mapping_violations}${file}:${line_no}:must inherit ${expected_base}\n"
    fi

    if rg -n "EventTypeUrl\s*=>" "${file}" >/dev/null; then
      route_mapping_violations="${route_mapping_violations}${file}:manual EventTypeUrl declaration is forbidden in concrete reducer\n"
    fi
  done < <(rg --files "${root}" -g '*Reducer*.cs' | rg -v 'ReducerBase\.cs$')
done

# Projectors that keep reducer route tables must use exact EventTypeUrl keying and exact-key lookup.
while IFS= read -r projector_file; do
  [ -z "${projector_file}" ] && continue

  if ! rg -n "GroupBy\(\s*x\s*=>\s*x\.EventTypeUrl\s*,\s*StringComparer\.Ordinal\s*\)" "${projector_file}" >/dev/null; then
    route_mapping_violations="${route_mapping_violations}${projector_file}:missing GroupBy(EventTypeUrl, StringComparer.Ordinal)\n"
  fi

  if ! rg -n -U "ToDictionary\([\s\S]{0,300}StringComparer\.Ordinal" "${projector_file}" >/dev/null; then
    route_mapping_violations="${route_mapping_violations}${projector_file}:missing ToDictionary(..., StringComparer.Ordinal) for route map\n"
  fi

  if ! rg -n "TryGetValue\(\s*typeUrl\s*,\s*out\s+var\s+reducers\s*\)" "${projector_file}" >/dev/null; then
    route_mapping_violations="${route_mapping_violations}${projector_file}:missing TryGetValue(typeUrl, out reducers) exact route lookup\n"
  fi
done < <(
  while IFS= read -r file; do
    if rg -n "_reducersByType|IProjectionEventReducer<" "${file}" >/dev/null; then
      echo "${file}"
    fi
  done < <(rg --files src demos -g '*Projector*.cs')
)

if [ -n "${route_mapping_violations}" ]; then
  printf '%b' "${route_mapping_violations}"
  echo "Projection route-mapping guard failed: event type -> reducer mapping must be TypeUrl-derived and exact-key routed."
  exit 1
fi

echo "Projection route-mapping guard passed."
