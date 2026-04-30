#!/usr/bin/env bash

# Issue #498 acceptance: every [GAgent("...")] / [LegacyAgentKind("...")]
# token must match ^[a-z0-9]+(\.[a-z0-9]+(-[a-z0-9]+)*)+$ and must NOT end
# with -v\d+. Hyphens are only legal inside non-prefix segments.
# Kind tokens are stable business identifiers; CLR identity is incidental.

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

# Files that may declare kinds: any C# under src/, agents/, tools/ excluding tests.
SEARCH_PATHS=("src" "agents" "tools/Aevatar.Tools.Cli")

# `rg -o` emits one line per match (not per matching line); this is required
# because a single source line may legally carry both `[GAgent("a.b")]` and
# `[LegacyAgentKind("c.d")]` and we need to validate every kind token.
# Capture group `kind` extracts just the token.
attribute_pattern='\[(GAgent|LegacyAgentKind)\("(?P<kind>[^"]+)"\)\]'

while IFS=: read -r file line content; do
  # `rg -o` already returns just the matched substring (the whole attribute),
  # so the embedded sed extracts the kind argument from one match per line.
  kind_token="$(printf '%s' "${content}" | sed -E 's/^\[(GAgent|LegacyAgentKind)\("([^"]+)"\)\]$/\2/')"
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

if (( violations > 0 )); then
  echo "agent_kind_naming_guard: ${violations} invalid kind token(s) found." >&2
  exit 1
fi

echo "agent_kind_naming_guard: ok"
