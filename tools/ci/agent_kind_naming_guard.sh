#!/usr/bin/env bash

# Issue #498 acceptance: every [GAgent("...")] / [LegacyAgentKind("...")]
# token must match ^[a-z0-9]+(\.[a-z0-9-]+)+$ and must NOT end with -v\d+.
# Kind tokens are stable business identifiers; CLR identity is incidental.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Match valid kind tokens (kept in sync with AgentKindToken.FormatPattern):
#   ^[a-z0-9]+(\.[a-z0-9]+(-[a-z0-9]+)*)+$
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

# Files that may declare kinds: any C# under src/, agents/, tools/ excluding tests.
SEARCH_PATHS=("src" "agents" "tools/Aevatar.Tools.Cli")

# Walk every [GAgent("...")] and [LegacyAgentKind("...")] occurrence.
attribute_pattern='\[(GAgent|LegacyAgentKind)\("([^"]+)"\)\]'

while IFS=: read -r file line content; do
  # Strip the file:line prefix; extract the kind argument.
  kind_token="$(printf '%s' "${content}" | sed -E "s/.*\[(GAgent|LegacyAgentKind)\(\"([^\"]+)\"\)\].*/\2/")"
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
done < <(rg --no-heading -n "${attribute_pattern}" "${SEARCH_PATHS[@]}" --glob '*.cs' || true)

if (( violations > 0 )); then
  echo "agent_kind_naming_guard: ${violations} invalid kind token(s) found." >&2
  exit 1
fi

echo "agent_kind_naming_guard: ok"
