#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="${AEVATAR_CI_WORKFLOW_GUARD_ROOT:-$(cd -- "${SCRIPT_DIR}/../.." && pwd)}"
cd "${REPO_ROOT}"

CI_WORKFLOW=".github/workflows/ci.yml"
REVIEW_POLICY_WORKFLOW=".github/workflows/fkst-review-policy.yml"

workflow_section_keys() {
  local section="$1"
  local path="$2"

  if [[ ! -f "${path}" ]]; then
    echo "${path} is required for CI workflow event guard." >&2
    exit 1
  fi

  awk -v section="${section}" '
    $0 ~ "^" section "[[:space:]]*:" {
      value = $0
      sub("^[^:]*:[[:space:]]*", "", value)
      if (value != "") {
        gsub(/[\[\],]/, " ", value)
        split(value, parts, /[[:space:]]+/)
        for (part_index in parts) {
          if (parts[part_index] ~ /^[A-Za-z_][A-Za-z0-9_-]*$/) {
            print parts[part_index]
          }
        }
        exit
      }
      in_section = 1
      next
    }
    in_section && /^[A-Za-z_][A-Za-z0-9_-]*[[:space:]]*:/ {
      exit
    }
    in_section && /^  [A-Za-z_][A-Za-z0-9_-]*[[:space:]]*:/ {
      key = $0
      sub(/^  /, "", key)
      sub(/[[:space:]]*:.*/, "", key)
      print key
    }
  ' "${path}"
}

contains_line() {
  local needle="$1"
  grep -Fxq "${needle}"
}

join_by_comma() {
  local IFS=", "
  echo "$*"
}

CI_EVENTS="$(workflow_section_keys "on" "${CI_WORKFLOW}")"
FORBIDDEN_MATCHES=()
for event_name in pull_request_review pull_request_review_comment issue_comment; do
  if printf '%s\n' "${CI_EVENTS}" | contains_line "${event_name}"; then
    FORBIDDEN_MATCHES+=("${event_name}")
  fi
done

if (( ${#FORBIDDEN_MATCHES[@]} > 0 )); then
  echo "${CI_WORKFLOW} must not subscribe to $(join_by_comma "${FORBIDDEN_MATCHES[@]}"). Review/comment policy checks must stay in ${REVIEW_POLICY_WORKFLOW} so required ci jobs are not recreated for comment events." >&2
  exit 1
fi

REVIEW_POLICY_EVENTS="$(workflow_section_keys "on" "${REVIEW_POLICY_WORKFLOW}")"
MISSING_REVIEW_POLICY_EVENTS=()
for event_name in pull_request_review pull_request_review_comment issue_comment; do
  if ! printf '%s\n' "${REVIEW_POLICY_EVENTS}" | contains_line "${event_name}"; then
    MISSING_REVIEW_POLICY_EVENTS+=("${event_name}")
  fi
done

if (( ${#MISSING_REVIEW_POLICY_EVENTS[@]} > 0 )); then
  echo "${REVIEW_POLICY_WORKFLOW} must handle $(join_by_comma "${MISSING_REVIEW_POLICY_EVENTS[@]}")." >&2
  exit 1
fi

REVIEW_POLICY_JOBS="$(workflow_section_keys "jobs" "${REVIEW_POLICY_WORKFLOW}")"
if ! printf '%s\n' "${REVIEW_POLICY_JOBS}" | contains_line "fkst-review-policy"; then
  echo "${REVIEW_POLICY_WORKFLOW} must expose the fkst-review-policy job." >&2
  exit 1
fi

REQUIRED_CHECK_CONFLICTS=()
for job_name in fast-gates console-web coverage-quality; do
  if printf '%s\n' "${REVIEW_POLICY_JOBS}" | contains_line "${job_name}"; then
    REQUIRED_CHECK_CONFLICTS+=("${job_name}")
  fi
done

if (( ${#REQUIRED_CHECK_CONFLICTS[@]} > 0 )); then
  echo "${REVIEW_POLICY_WORKFLOW} must not reuse required check job names: $(join_by_comma "${REQUIRED_CHECK_CONFLICTS[@]}")." >&2
  exit 1
fi

echo "required CI workflow event guard passed"
