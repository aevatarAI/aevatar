#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/query_projection_priming_guard.sh"
TMP_DIR="$(mktemp -d)"
OWNER_ROOT="${TMP_DIR}/owner-root"
GUARD_OUTPUT=""
GUARD_STATUS=0

trap 'rm -rf "${TMP_DIR}"' EXIT

owner_query_file="${OWNER_ROOT}/src/Aevatar.Studio.Projection/QueryPorts/ProjectionScheduledInvocationAuthorityQueryPorts.cs"
owner_query_dir="$(dirname -- "${owner_query_file}")"
planner_file="${OWNER_ROOT}/src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationPlanner.cs"
planner_dir="$(dirname -- "${planner_file}")"
contracts_file="${OWNER_ROOT}/src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs"
contracts_dir="$(dirname -- "${contracts_file}")"

mkdir -p "${owner_query_dir}" "${planner_dir}" "${contracts_dir}"

write_owner_query_anchor() {
  printf '%s\n' \
    'namespace Aevatar.Studio.Projection.QueryPorts;' \
    'public sealed class ProjectionScheduledInvocationOwnerLLMQueryPort { }' \
    > "${owner_query_file}"
}

write_owner_query_anchor
printf '%s\n' \
  'namespace Aevatar.GAgentService.Application.Schedules.Authorization;' \
  'public sealed class ScheduledInvocationAuthorizationPlanner { }' \
  > "${planner_file}"
printf '%s\n' \
  'namespace Aevatar.GAgentService.Abstractions.Schedules.Authorization;' \
  'public sealed record ScheduledInvocationOwnerLLMEvidence;' \
  'public interface IScheduledInvocationOwnerLLMEvidenceQueryPort { }' \
  > "${contracts_file}"

run_guard() {
  local path_value="${1:-${PATH}}"
  set +e
  GUARD_OUTPUT="$(
    PATH="${path_value}" \
    AEVATAR_QUERY_PROJECTION_OWNER_ROOT="${OWNER_ROOT}" \
      bash "${GUARD}" 2>&1
  )"
  GUARD_STATUS=$?
  set -e
}

require_failure() {
  local expected="$1"
  if [[ ${GUARD_STATUS} -eq 0 ]]; then
    echo "query projection priming guard self-test expected failure: ${expected}" >&2
    exit 1
  fi
  if ! printf '%s\n' "${GUARD_OUTPUT}" | rg -Fq -- "${expected}"; then
    echo "query projection priming guard self-test missing diagnostic: ${expected}" >&2
    printf '%s\n' "${GUARD_OUTPUT}" >&2
    exit 1
  fi
}

run_guard
if [[ ${GUARD_STATUS} -ne 0 ]]; then
  echo "query projection priming guard self-test baseline failed" >&2
  printf '%s\n' "${GUARD_OUTPUT}" >&2
  exit 1
fi

rm "${owner_query_file}"
run_guard
require_failure "Missing owner LLM query anchor file"
write_owner_query_anchor

forbidden_file="${owner_query_dir}/ForbiddenOwnerLLMQuery.cs"
printf '%s\n' \
  'public sealed class ForbiddenOwnerLLMQuery' \
  '{' \
  '    private readonly IUserLlmCatalogPort _catalog;' \
  '    public Task IssueAccessTokenAsync() => Task.CompletedTask;' \
  '}' \
  > "${forbidden_file}"
run_guard
require_failure "ForbiddenOwnerLLMQuery.cs"
rm "${forbidden_file}"

real_rg="$(command -v rg)"
fake_bin="${TMP_DIR}/bin"
fake_rg="${fake_bin}/rg"
mkdir -p "${fake_bin}"
{
  printf '%s\n' '#!/usr/bin/env bash'
  printf 'owner_root=%q\n' "${OWNER_ROOT}"
  printf 'real_rg=%q\n' "${real_rg}"
  printf '%s\n' \
    'if [[ "${PWD}" == "${owner_root}"* ]]; then' \
    '  echo "forced owner scan failure" >&2' \
    '  exit 2' \
    'fi' \
    'for arg in "$@"; do' \
    '  if [[ "${arg}" == "${owner_root}"* ]]; then' \
    '    echo "forced owner scan failure" >&2' \
    '    exit 2' \
    '  fi' \
    'done' \
    'exec "${real_rg}" "$@"'
} > "${fake_rg}"
chmod +x "${fake_rg}"

run_guard "${fake_bin}:${PATH}"
if [[ ${GUARD_STATUS} -ne 2 ]]; then
  echo "query projection priming guard self-test expected rg exit 2 propagation, got ${GUARD_STATUS}" >&2
  printf '%s\n' "${GUARD_OUTPUT}" >&2
  exit 1
fi
require_failure "rg exit 2"

if rg -Fq '|| true' "${GUARD}"; then
  echo "query projection priming guard must not mask rg failures with '|| true'" >&2
  exit 1
fi

echo "query projection priming guard tests passed"
