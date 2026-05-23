#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

scan_roots=()
for root in src agents; do
  if [ -d "${root}" ]; then
    scan_roots+=("${root}")
  fi
done

if [ "${#scan_roots[@]}" -eq 0 ]; then
  exit 0
fi

failures=()
provider_files=()
while IFS= read -r file; do
  provider_files+=("${file}")
done < <(
  rg -l "IProjectionActivationPlanProvider" "${scan_roots[@]}" \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    | sort
)

all_tests="$(rg -n "CommittedStateProjectionActivationPlanProvider|ProjectionActivationPlanProvider" test -g '*.cs' || true)"

registered_materializers_in() {
  local composition_file="$1"
  local start_line="${2:-1}"

  awk -v start="${start_line}" '
    NR < start { next }
    /AddCurrentStateProjectionMaterializer</ {
      text = $0
      while (text !~ /\);/ && (getline next_line) > 0) {
        text = text next_line
      }
      gsub(/[[:space:]]/, "", text)
      sub(/^.*AddCurrentStateProjectionMaterializer</, "", text)
      sub(/>.*/, "", text)
      split(text, parts, ",")
      if (parts[1] != "" && parts[2] != "") {
        print parts[1] " " parts[2]
      }
    }
  ' "${composition_file}"
}

registration_file_contains_projector() {
  local composition_file="$1"
  local projector="$2"
  local registered_context registered_projector

  while read -r registered_context registered_projector; do
    if [ "${registered_projector}" = "${projector}" ]; then
      return 0
    fi
  done < <(registered_materializers_in "${composition_file}")

  return 1
}

runtime_lease_for_context_in() {
  local composition_file="$1"
  local context="$2"

  awk -v context="${context}" '
    /AddProjectionMaterializationRuntimeCore</ {
      text = $0
      while (text !~ />[[:space:]]*\(/ && (getline next_line) > 0) {
        text = text next_line
      }
      gsub(/[[:space:]]/, "", text)
      sub(/^.*AddProjectionMaterializationRuntimeCore</, "", text)
      sub(/>.*/, "", text)
      split(text, parts, ",")
      if (parts[1] == context && parts[2] != "") {
        print parts[2]
        exit
      }
    }
    /AddServiceProjectionRuntime</ {
      text = $0
      while (text !~ />[[:space:]]*\(/ && (getline next_line) > 0) {
        text = text next_line
      }
      gsub(/[[:space:]]/, "", text)
      sub(/^.*AddServiceProjectionRuntime</, "", text)
      sub(/>.*/, "", text)
      split(text, parts, ",")
      if (parts[1] == context) {
        print "ServiceProjectionRuntimeLease<" context ">"
        exit
      }
    }
  ' "${composition_file}"
}

provider_returns_plan_for_context() {
  local provider_file="$1"
  local context="$2"
  local lease="$3"
  local compact

  compact="$(tr -d '[:space:]' < "${provider_file}")"

  if [[ "${compact}" == *"DurablePlan<${context}>"* ]] ||
     [[ "${compact}" == *"ServiceProjectionRuntimeLease<${context}>"* ]]; then
    return 0
  fi

  if [ -n "${lease}" ] &&
     { [[ "${compact}" == *"typeof(${lease})"* ]] ||
       [[ "${compact}" == *"DurablePlan<${lease}>"* ]]; }; then
    return 0
  fi

  return 1
}

has_provider_coverage_for_context() {
  local composition_file="$1"
  local context="$2"
  local composition_dir provider_file provider_name test_name lease
  composition_dir="$(dirname "${composition_file}")"

  if ! rg -q "IProjectionActivationPlanProvider" "${composition_file}"; then
    return 1
  fi
  if ! rg -q "ProjectionActivationPlanDispatcher" "${composition_file}"; then
    return 1
  fi
  if ! rg -q "CommittedStateProjectionActivationHook" "${composition_file}"; then
    return 1
  fi

  lease="$(runtime_lease_for_context_in "${composition_file}" "${context}")"

  for provider_file in "${provider_files[@]}"; do
    if [[ "${provider_file}" == "${composition_dir}"/* ]] ||
       [[ "${provider_file}" == "$(dirname "${composition_dir}")"/* ]] ||
       [[ "${provider_file}" == "$(dirname "$(dirname "${composition_dir}")")"/* ]]; then
      provider_name="$(basename "${provider_file}" .cs)"
      if [[ "${provider_name}" == *CommittedStateProjectionActivationPlanProvider ]] &&
         rg -q "${provider_name}" "${composition_file}"; then
        test_name="${provider_name}Tests"
        if grep -q "${test_name}" <<< "${all_tests}" &&
           provider_returns_plan_for_context "${provider_file}" "${context}" "${lease}"; then
          return 0
        fi
      fi
    fi
  done

  return 1
}

has_exemption() {
  local projector="$1"
  local projector_file

  projector_file="$(
    rg -l "(class|record)[[:space:]]+${projector}([[:space:]:]|$)" "${scan_roots[@]}" \
      -g '*.cs' \
      -g '!**/bin/**' \
      -g '!**/obj/**' \
      | awk 'NR == 1 { first = $0 } END { if (first != "") print first }'
  )"

  if [ -z "${projector_file}" ]; then
    return 1
  fi

  if ! rg -q "ProjectionExempt" "${projector_file}"; then
    return 1
  fi
  if ! rg -q "Reason[[:space:]]*=[[:space:]]*\"[^\"]{12,}\"" "${projector_file}"; then
    failures+=("${projector_file}: [ProjectionExempt] must include a specific Reason.")
    return 1
  fi
  if ! rg -q "Category[[:space:]]*=[[:space:]]*ProjectionExemptionCategory\\.(StartupBootstrap|SessionObservation|ArtifactNotCurrentState|ProjectionCoreStatus|TestOnly|LegacyToDelete)" "${projector_file}"; then
    failures+=("${projector_file}: [ProjectionExempt] must include an approved ProjectionExemptionCategory.")
    return 1
  fi

  return 0
}

has_registered_projector_provider_coverage() {
  local projector="$1"
  local composition_file registered_context registered_projector

  while IFS= read -r composition_file; do
    while read -r registered_context registered_projector; do
      if [ "${registered_projector}" = "${projector}" ] &&
         has_provider_coverage_for_context "${composition_file}" "${registered_context}"; then
        return 0
      fi
    done < <(registered_materializers_in "${composition_file}")
  done < <(
    rg -l "AddCurrentStateProjectionMaterializer<" "${scan_roots[@]}" \
      -g '*.cs' \
      -g '!src/Aevatar.CQRS.Projection.Core/DependencyInjection/ProjectionMaterializerRegistration.cs' \
      -g '!**/bin/**' \
      -g '!**/obj/**'
  )

  return 1
}

while IFS= read -r match; do
  file="${match%%:*}"
  line="${match#*:}"
  line="${line%%:*}"
  materializer="$(registered_materializers_in "${file}" "${line}" | awk 'NR == 1 { print }')"
  context="${materializer%% *}"
  projector="${materializer#* }"

  if [ -z "${materializer}" ] || [ "${context}" = "${projector}" ]; then
    failures+=("${file}:${line}: unable to parse AddCurrentStateProjectionMaterializer projector type.")
    continue
  fi

  if has_provider_coverage_for_context "${file}" "${context}"; then
    continue
  fi
  if has_exemption "${projector}"; then
    continue
  fi

  failures+=("${file}:${line}: ${projector} current-state materializer for ${context} requires same-composition IProjectionActivationPlanProvider + ProjectionActivationPlanDispatcher + CommittedStateProjectionActivationHook + provider test, and the provider must return a ${context} activation plan, or [ProjectionExempt(Category=..., Reason=\"...\")] on the projector.")
done < <(
  rg -n "AddCurrentStateProjectionMaterializer<" "${scan_roots[@]}" \
    -g '*.cs' \
    -g '!src/Aevatar.CQRS.Projection.Core/DependencyInjection/ProjectionMaterializerRegistration.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**'
)

while IFS= read -r match; do
  file="${match%%:*}"
  class_name="${match##*:}"
  class_name="$(sed -E 's/.*class[[:space:]]+([A-Za-z_][A-Za-z0-9_]*).*/\1/' <<< "${class_name}")"
  if [ -z "${class_name}" ]; then
    continue
  fi

  if rg -q "AddCurrentStateProjectionMaterializer<[^>]*,[[:space:]]*${class_name}[[:space:]]*>" "${scan_roots[@]}" -g '*.cs'; then
    continue
  fi

  if has_exemption "${class_name}"; then
    continue
  fi

  failures+=("${file}: ${class_name} implements ICurrentStateProjectionMaterializer but is not registered through AddCurrentStateProjectionMaterializer and is not explicitly [ProjectionExempt].")
done < <(
  rg -n "class[[:space:]]+[A-Za-z_][A-Za-z0-9_]*.*ICurrentStateProjectionMaterializer<" "${scan_roots[@]}" \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**'
)

while IFS= read -r file; do
  while IFS= read -r class_name; do
    if [[ "${class_name}" == *CurrentStateProjector ]]; then
      if has_registered_projector_provider_coverage "${class_name}"; then
        continue
      fi
      if has_exemption "${class_name}"; then
        continue
      fi

      failures+=("${file}: ${class_name} current-state projector requires registered provider coverage or [ProjectionExempt(Category=..., Reason=\"...\")] on the projector.")
      continue
    fi

    if [[ "${class_name}" == *CurrentStateDocument ]]; then
      projector="${class_name%Document}Projector"
      if has_registered_projector_provider_coverage "${projector}"; then
        continue
      fi
      if has_exemption "${projector}" || has_exemption "${class_name}"; then
        continue
      fi

      failures+=("${file}: ${class_name} current-state document requires associated ${projector} provider coverage or [ProjectionExempt(Category=..., Reason=\"...\")].")
    fi
  done < <(
    rg -o "class[[:space:]]+[A-Za-z_][A-Za-z0-9_]*(CurrentStateProjector|CurrentStateDocument)([[:space:]:]|$)" "${file}" |
      sed -E 's/.*class[[:space:]]+([A-Za-z_][A-Za-z0-9_]*(CurrentStateProjector|CurrentStateDocument)).*/\1/' |
      sort -u
  )
done < <(
  {
    rg --files "${scan_roots[@]}" \
      -g '!**/bin/**' \
      -g '!**/obj/**' |
      rg 'CurrentState(Document|Projector).*\.cs$'
    rg -l "class[[:space:]]+[A-Za-z_][A-Za-z0-9_]*(CurrentStateDocument|CurrentStateProjector)([[:space:]:]|$)" "${scan_roots[@]}" \
      -g '*.cs' \
      -g '!**/bin/**' \
      -g '!**/obj/**'
  } | sort -u
)

if [ "${#failures[@]}" -gt 0 ]; then
  printf '%s\n' "${failures[@]}"
  echo "Projection activation provider coverage guard failed."
  exit 1
fi
