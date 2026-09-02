#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/query_projection_priming_guard.sh"
TMP_DIR="$(mktemp -d)"
OWNER_ROOT="${TMP_DIR}/owner-root"
NYXID_ROOT="${TMP_DIR}/nyxid-root"
GUARD_OUTPUT=""
GUARD_STATUS=0

trap 'rm -rf "${TMP_DIR}"' EXIT

owner_query_file="${OWNER_ROOT}/src/Aevatar.Studio.Projection/QueryPorts/ProjectionScheduledInvocationAuthorityQueryPorts.cs"
owner_query_dir="$(dirname -- "${owner_query_file}")"
planner_file="${OWNER_ROOT}/src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationPlanner.cs"
planner_dir="$(dirname -- "${planner_file}")"
catalog_repair_file="${planner_dir}/NyxIdAuthorizationCatalogVersionRegressionRepairService.cs"
contracts_file="${OWNER_ROOT}/src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs"
contracts_dir="$(dirname -- "${contracts_file}")"
nyxid_endpoint_file="${NYXID_ROOT}/agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.State.cs"
nyxid_endpoint_dir="$(dirname -- "${nyxid_endpoint_file}")"
nyxid_query_file="${NYXID_ROOT}/src/Aevatar.Studio.Infrastructure/ActorBacked/ProjectionNyxIdChatConversationStateQueryPort.cs"
nyxid_query_dir="$(dirname -- "${nyxid_query_file}")"
nyxid_contract_file="${NYXID_ROOT}/src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/INyxIdChatConversationStateQueryPort.cs"
nyxid_contract_dir="$(dirname -- "${nyxid_contract_file}")"

mkdir -p \
  "${owner_query_dir}" \
  "${planner_dir}" \
  "${contracts_dir}" \
  "${nyxid_endpoint_dir}" \
  "${nyxid_query_dir}" \
  "${nyxid_contract_dir}"

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
  'namespace Aevatar.GAgentService.Application.Schedules.Authorization;' \
  'public sealed class NyxIdAuthorizationCatalogVersionRegressionRepairService' \
  '{' \
  '    public string Refresh(RepairRequest request) => request.BearerToken;' \
  '}' \
  > "${catalog_repair_file}"
printf '%s\n' \
  'namespace Aevatar.GAgentService.Abstractions.Schedules.Authorization;' \
  'public sealed record ScheduledInvocationOwnerLLMEvidence;' \
  'public interface IScheduledInvocationOwnerLLMEvidenceQueryPort { }' \
  > "${contracts_file}"

write_nyxid_query_anchors() {
  printf '%s\n' \
    'public static partial class NyxIdChatEndpoints' \
    '{' \
    '    private static async Task<IResult> HandleGetStateAsync(' \
    '        INyxIdChatConversationStateQueryPort stateQueryPort) =>' \
    '        Results.Ok(await stateQueryPort.GetAsync(default!));' \
    '}' \
    > "${nyxid_endpoint_file}"
  printf '%s\n' \
    'internal sealed class ProjectionNyxIdChatConversationStateQueryPort' \
    '    : INyxIdChatConversationStateQueryPort' \
    '{' \
    '    private readonly IProjectionDocumentReader<NyxIdChatConversationCurrentStateDocument, string> _documentReader;' \
    '}' \
    > "${nyxid_query_file}"
  printf '%s\n' \
    'public interface INyxIdChatConversationStateQueryPort' \
    '{' \
    '    Task<object> GetAsync(object query);' \
    '}' \
    > "${nyxid_contract_file}"
}

write_nyxid_query_anchors

run_guard() {
  local path_value="${1:-${PATH}}"
  set +e
  GUARD_OUTPUT="$(
    PATH="${path_value}" \
    AEVATAR_QUERY_PROJECTION_OWNER_ROOT="${OWNER_ROOT}" \
    AEVATAR_QUERY_PROJECTION_NYXID_ROOT="${NYXID_ROOT}" \
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

printf '%s\n' \
  'internal sealed class ProjectionNyxIdChatConversationStateQueryPort' \
  '    : INyxIdChatConversationStateQueryPort' \
  '{' \
  '    private readonly IActorRuntime _runtime;' \
  '    private readonly IEventStore _eventStore;' \
  '}' \
  > "${nyxid_query_file}"
run_guard
require_failure "ProjectionNyxIdChatConversationStateQueryPort.cs"
write_nyxid_query_anchors

printf '%s\n' \
  'public static partial class NyxIdChatEndpoints' \
  '{' \
  '    private static async Task<IResult> HandleGetStateAsync(IProjectionPort projection)' \
  '    {' \
  '        await projection.PrimeAsync();' \
  '        return Results.Ok();' \
  '    }' \
  '}' \
  > "${nyxid_endpoint_file}"
run_guard
require_failure "NyxIdChatEndpoints.State.cs"
write_nyxid_query_anchors

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

forbidden_authorization_file="${planner_dir}/ForbiddenScheduledOwnerLLMQuery.cs"
printf '%s\n' \
  'public sealed class ForbiddenScheduledOwnerLLMQuery' \
  '{' \
  '    public string Read(QueryRequest request) => request.BearerToken;' \
  '}' \
  > "${forbidden_authorization_file}"
run_guard
require_failure "ForbiddenScheduledOwnerLLMQuery.cs"
rm "${forbidden_authorization_file}"

same_basename_query_file="${owner_query_dir}/NyxIdAuthorizationCatalogVersionRegressionRepairService.cs"
printf '%s\n' \
  'namespace Aevatar.Studio.Projection.QueryPorts;' \
  'public sealed class NyxIdAuthorizationCatalogVersionRegressionRepairService' \
  '{' \
  '    public string Read(QueryRequest request) => request.BearerToken;' \
  '}' \
  > "${same_basename_query_file}"
run_guard
require_failure "NyxIdAuthorizationCatalogVersionRegressionRepairService.cs"
rm "${same_basename_query_file}"

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
