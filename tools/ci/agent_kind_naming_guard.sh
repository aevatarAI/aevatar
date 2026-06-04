#!/usr/bin/env bash

# Agent kind acceptance: every primary [GAgent("...")] token must match
# ^[a-z0-9]+(\.[a-z0-9]+(-[a-z0-9]+)*)+$ and must NOT end with -v\d+.
# Hyphens are only legal inside non-prefix segments. Kind tokens are stable
# business identifiers; CLR identity is diagnostic only.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Kept in sync with Aevatar.Foundation.Abstractions.TypeSystem.AgentKindToken.FormatPattern.
KIND_REGEX='^[a-z0-9]+(\.[a-z0-9]+(-[a-z0-9]+)*)+$'
VERSIONED_TAIL_REGEX='-v[0-9]+$'

violations=0

emit_violation() {
  local file="$1"
  local line="$2"
  local kind="$3"
  local reason="$4"
  echo "::error file=${file},line=${line}::Agent kind '${kind}' invalid: ${reason}"
  violations=$((violations + 1))
}

trim_line() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "${value}"
}

has_local_gagent_attribute() {
  local file="$1"
  local class_line="$2"
  local raw=""
  local trimmed=""
  local attribute_block=""

  while IFS= read -r raw; do
    trimmed="$(trim_line "${raw}")"

    if [[ -z "${trimmed}" || "${trimmed}" == //* ]]; then
      if [[ -n "${attribute_block}" ]]; then
        attribute_block+=$'\n'"${raw}"
      fi
      continue
    fi

    if [[ "${trimmed}" == \[* ]]; then
      attribute_block+=$'\n'"${raw}"
      continue
    fi

    if [[ -n "${attribute_block}" &&
          "${trimmed}" != *";"* &&
          "${trimmed}" != *"{"* &&
          "${trimmed}" != *"}"* &&
          "${trimmed}" != using[[:space:]]* &&
          "${trimmed}" != namespace[[:space:]]* &&
          "${trimmed}" != class[[:space:]]* &&
          "${trimmed}" != *[[:space:]]class[[:space:]]* ]]; then
      attribute_block+=$'\n'"${raw}"
      continue
    fi

    attribute_block=""
  done < <(sed -n "1,$((class_line - 1))p" "${file}")

  rg -q '\[GAgent\(' <<< "${attribute_block}"
}

run_class_local_self_test() {
  local temp_dir=""
  temp_dir="$(mktemp -d)"
  local fixture="${temp_dir}/ClassLocalFixture.cs"
  {
    printf '%s\n' 'using Aevatar.Foundation.Abstractions;'
    printf '%s\n' ''
    printf '%s\n' '[GAgent("tests.decorated-fixture")]'
    printf '%s\n' 'public sealed class DecoratedFixtureAgent : GAgentBase<object> { }'
    printf '%s\n' ''
    printf '%s\n' 'public sealed class UndecoratedFixtureAgent : GAgentBase<object> { }'
  } > "${fixture}"

  local decorated_line=""
  local undecorated_line=""
  decorated_line="$(rg -n 'DecoratedFixtureAgent' "${fixture}" | sed -E 's/:.*//')"
  undecorated_line="$(rg -n 'UndecoratedFixtureAgent' "${fixture}" | sed -E 's/:.*//')"

  if ! has_local_gagent_attribute "${fixture}" "${decorated_line}"; then
    echo "agent_kind_naming_guard: self-test failed; decorated fixture was not recognized." >&2
    rm -rf "${temp_dir}"
    exit 1
  fi

  if has_local_gagent_attribute "${fixture}" "${undecorated_line}"; then
    echo "agent_kind_naming_guard: self-test failed; previous class attribute leaked to an undecorated class." >&2
    rm -rf "${temp_dir}"
    exit 1
  fi

  rm -rf "${temp_dir}"
}

# Files that may declare kinds: any C# under production source roots.
candidate_search_paths=("src" "agents")
SEARCH_PATHS=()
for candidate in "${candidate_search_paths[@]}"; do
  if [[ -d "${candidate}" ]]; then
    SEARCH_PATHS+=("${candidate}")
  fi
done

if (( ${#SEARCH_PATHS[@]} == 0 )); then
  echo "agent_kind_naming_guard: no source roots found, skipping."
  exit 0
fi

run_class_local_self_test

attribute_pattern='\[GAgent\("(?P<kind>[^"]+)"\)\]'

while IFS=: read -r file line content; do
  # `rg -o` already returns just the matched substring (the whole attribute),
  # so the embedded sed extracts the kind argument from one match per line.
  kind_token="$(printf '%s' "${content}" | sed -E 's/^\[GAgent\("([^"]+)"\)\]$/\1/')"
  if [[ -z "${kind_token}" || "${kind_token}" == "${content}" ]]; then
    continue
  fi

  if [[ "${kind_token}" =~ ${VERSIONED_TAIL_REGEX} ]]; then
    emit_violation "${file}" "${line}" "${kind_token}" \
      "kinds are never versioned; use proto3 field rules or state-version migration instead of '-vN' suffix"
    continue
  fi

  if ! [[ "${kind_token}" =~ ${KIND_REGEX} ]]; then
    emit_violation "${file}" "${line}" "${kind_token}" \
      "must match ${KIND_REGEX} (e.g. 'scheduled.skill-runner', 'channels.bot-registration')"
  fi
done < <(rg -o --no-heading -n "${attribute_pattern}" "${SEARCH_PATHS[@]}" --glob '*.cs' || true)

for forbidden in \
  "LegacyAgentKind" \
  "ILegacyAgentClrTypeResolver" \
  "TryResolveKindByClrTypeName" \
  "BindAgentByLegacyClrTypeAsync" \
  "IActorTypeProbe" \
  "IAgentTypeVerifier" \
  "GetAgentTypeNameAsync"; do
  while IFS=: read -r file line content; do
    echo "::error file=${file},line=${line}::Forbidden legacy agent-kind identity symbol '${forbidden}' remains: ${content}"
    violations=$((violations + 1))
  done < <(rg --no-heading -n "${forbidden}" "${SEARCH_PATHS[@]}" --glob '*.cs' || true)
done

agent_class_pattern='public[[:space:]]+.*class[[:space:]]+[A-Za-z_][A-Za-z0-9_]*([[:space:]]|$).*:[^{]*(GAgentBase|AIGAgentBase|RoleGAgent)'

while IFS=: read -r file line content; do
  [[ "${content}" == *"abstract class"* ]] && continue
  [[ "${content}" =~ class[[:space:]]+[A-Za-z_][A-Za-z0-9_]*\< ]] && continue
  if [[ "${file}" == "src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgents.cs" ]]; then
    continue
  fi

  if ! has_local_gagent_attribute "${file}" "${line}"; then
    echo "::error file=${file},line=${line}::Concrete production agent class is missing a primary [GAgent(\"module.entity\")] kind."
    violations=$((violations + 1))
  fi
done < <(rg --no-heading -n "${agent_class_pattern}|public[[:space:]]+class[[:space:]]+HouseholdEntity" "${SEARCH_PATHS[@]}" --glob '*.cs' || true)

if (( violations > 0 )); then
  echo "agent_kind_naming_guard: ${violations} invalid kind token(s) found." >&2
  exit 1
fi

echo "agent_kind_naming_guard: ok"
