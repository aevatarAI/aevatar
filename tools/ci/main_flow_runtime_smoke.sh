#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"
source "${SCRIPT_DIR}/distributed_smoke_common.sh"

HTTP_PORT="${AEVATAR_MAIN_FLOW_SMOKE_HTTP_PORT:-18082}"
SILO_PORT="${AEVATAR_MAIN_FLOW_SMOKE_SILO_PORT:-11112}"
GATEWAY_PORT="${AEVATAR_MAIN_FLOW_SMOKE_GATEWAY_PORT:-30001}"
WAIT_SECONDS="${AEVATAR_MAIN_FLOW_SMOKE_WAIT_SECONDS:-120}"
PUBLISH_DIR="${AEVATAR_MAIN_FLOW_SMOKE_PUBLISH_DIR:-/tmp/aevatar-main-flow-smoke-publish}"
APP_DLL="${PUBLISH_DIR}/Aevatar.Mainnet.Host.Api.dll"
LOCK_OWNER="main_flow_runtime_smoke"

timestamp="$(date +%Y%m%d-%H%M%S)"
run_id="${AEVATAR_MAIN_FLOW_SMOKE_RUN_ID:-${timestamp}}"
scope_id="main-flow-${run_id}"
team_id="team-${run_id}"
member_id="member-${run_id}"
workflow_id="workflow-${run_id}"
cluster_id="aevatar-main-flow-smoke-cluster-${timestamp}"
service_id="aevatar-mainnet-host-api-main-flow-smoke"
log_dir="${AEVATAR_MAIN_FLOW_SMOKE_LOG_DIR:-/tmp/aevatar-main-flow-smoke-${timestamp}}"
log_file="${log_dir}/host.log"
mkdir -p "${log_dir}"

declare -a pids=()

cleanup() {
  for pid in "${pids[@]-}"; do
    if kill -0 "${pid}" 2>/dev/null; then
      kill "${pid}" 2>/dev/null || true
    fi
  done

  sleep 1
  for pid in "${pids[@]-}"; do
    if kill -0 "${pid}" 2>/dev/null; then
      kill -9 "${pid}" 2>/dev/null || true
    fi
  done

  release_distributed_smoke_lock
}
trap cleanup EXIT INT TERM

start_host() {
  (
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="http://127.0.0.1:${HTTP_PORT}" \
    AEVATAR_NYXID_AUTHORITY="http://127.0.0.1:${HTTP_PORT}" \
    AEVATAR_Aevatar__NyxId__ApiBaseUrl="http://127.0.0.1:${HTTP_PORT}" \
    AEVATAR_Aevatar__NyxId__AssistantActions__Enabled=false \
    AEVATAR_OAUTH_REDIRECT_BASE_URL="http://127.0.0.1:${HTTP_PORT}" \
    Aevatar__Authentication__Enabled=false \
    AEVATAR_ActorRuntime__Provider=Orleans \
    AEVATAR_ActorRuntime__OrleansStreamBackend=InMemory \
    AEVATAR_ActorRuntime__OrleansPersistenceBackend=InMemory \
    AEVATAR_ActorRuntime__SecretStoreBackend=InMemory \
    AEVATAR_Orleans__ClusteringMode=Development \
    AEVATAR_Orleans__ClusterId="${cluster_id}" \
    AEVATAR_Orleans__ServiceId="${service_id}" \
    AEVATAR_Orleans__SiloHost=127.0.0.1 \
    AEVATAR_Orleans__PrimarySiloEndpoint="127.0.0.1:${SILO_PORT}" \
    AEVATAR_Orleans__SiloPort="${SILO_PORT}" \
    AEVATAR_Orleans__GatewayPort="${GATEWAY_PORT}" \
    AEVATAR_Orleans__ListenOnAnyHostAddress=true \
    Projection__Document__Providers__InMemory__Enabled=true \
    Projection__Document__Providers__Elasticsearch__Enabled=false \
    Projection__Graph__Providers__InMemory__Enabled=true \
    Projection__Graph__Providers__Neo4j__Enabled=false \
    Projection__Policies__DenyInMemoryDocumentReadStore=false \
    Projection__Policies__DenyInMemoryGraphFactStore=false \
    Projection__Policies__Environment=Development \
    Audit__ActorIdentityHasher__ActiveKeyId=main-flow-smoke-key \
    Audit__ActorIdentityHasher__Keys__0__KeyId=main-flow-smoke-key \
    Audit__ActorIdentityHasher__Keys__0__Key="main-flow-smoke-audit-hasher-key-0001" \
    dotnet "${APP_DLL}" >"${log_file}" 2>&1
  ) &

  pids+=("$!")
}

print_key_logs() {
  echo "HOST_KEYLOGS"
  if [[ -f "${log_file}" ]]; then
    grep -nE "Duplicate workflow definition name|Loaded [0-9]+ workflow definition|Orleans Silo started|Now listening on|Unhandled exception|Application startup exception|InvalidOperationException|Error" "${log_file}" || true
  else
    echo "Host log not found: ${log_file}"
  fi
  echo
}

wait_for_readiness() {
  local ready=0

  for _ in $(seq 1 "${WAIT_SECONDS}"); do
    if ! kill -0 "${pids[0]}" 2>/dev/null; then
      echo "Host process exited before readiness." >&2
      return 1
    fi

    local code
    code="$(curl --max-time 2 -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:${HTTP_PORT}/api/status" || true)"
    if [[ "${code}" == "200" ]]; then
      ready=1
      break
    fi

    sleep 1
  done

  echo "READY=${ready}"
  if [[ "${ready}" != "1" ]]; then
    return 1
  fi
}

request_json() {
  local method="$1"
  local path="$2"
  local body="$3"
  local expected_codes="$4"
  local output_file="$5"
  local max_attempts="${6:-1}"
  local attempt
  local code

  for attempt in $(seq 1 "${max_attempts}"); do
    code="$({
      if [[ -n "${body}" ]]; then
        curl --max-time 20 -sS \
          -H "Content-Type: application/json" \
          -H "Authorization: Bearer main-flow-smoke-token" \
          -X "${method}" \
          -d "${body}" \
          -o "${output_file}" \
          -w "%{http_code}" \
          "http://127.0.0.1:${HTTP_PORT}${path}"
      else
        curl --max-time 20 -sS \
          -H "Content-Type: application/json" \
          -H "Authorization: Bearer main-flow-smoke-token" \
          -X "${method}" \
          -o "${output_file}" \
          -w "%{http_code}" \
          "http://127.0.0.1:${HTTP_PORT}${path}"
      fi
    } || true)"

    case ",${expected_codes}," in
      *",${code},"*)
        echo "${method} ${path} -> ${code}"
        return 0
        ;;
    esac

    if (( attempt < max_attempts )); then
      echo "${method} ${path} attempt ${attempt}/${max_attempts} returned ${code}; retrying." >&2
      sleep 1
    fi
  done

  echo "Expected ${method} ${path} to return one of ${expected_codes} after ${max_attempts} attempt(s), last got ${code}." >&2
  echo "Response body:" >&2
  if [[ -f "${output_file}" ]]; then
    python3 -m json.tool "${output_file}" >&2 2>/dev/null || cat "${output_file}" >&2
  fi
  print_key_logs
  return 1
}

wait_for_status_code() {
  local path="$1"
  local expected_code="$2"
  local output_file="$3"
  local code="000"

  for _ in $(seq 1 "${WAIT_SECONDS}"); do
    code="$(curl --max-time 5 -sS \
      -H "Content-Type: application/json" \
      -H "Authorization: Bearer main-flow-smoke-token" \
      -o "${output_file}" \
      -w "%{http_code}" \
      "http://127.0.0.1:${HTTP_PORT}${path}" || true)"
    if [[ "${code}" == "${expected_code}" ]]; then
      echo "GET ${path} -> ${code}"
      return 0
    fi
    sleep 1
  done

  echo "Expected GET ${path} to become HTTP ${expected_code}, last got ${code}." >&2
  if [[ -f "${output_file}" ]]; then
    python3 -m json.tool "${output_file}" >&2 2>/dev/null || cat "${output_file}" >&2
  fi
  return 1
}

assert_json_field() {
  local file="$1"
  local field="$2"
  local expected="$3"
  python3 - "$file" "$field" "$expected" <<'PY'
import json
import sys

path, field, expected = sys.argv[1:]
with open(path, encoding="utf-8") as handle:
    data = json.load(handle)
value = data
for part in field.split('.'):
    value = value[part]
if str(value) != expected:
    raise SystemExit(f"expected {field}={expected!r}, got {value!r}")
PY
}

workflow_yaml='name: main-flow-smoke
roles: []
steps:
  - id: smoke_assign
    type: assign
    parameters:
      target: smoke_result
      value: "ok"
'

team_body="$(python3 - "${team_id}" <<'PY'
import json
import sys
team_id = sys.argv[1]
print(json.dumps({
    "teamId": team_id,
    "displayName": "Main Flow Smoke Team",
    "description": "CI main-flow runtime smoke team",
}))
PY
)"

member_body="$(python3 - "${member_id}" "${team_id}" <<'PY'
import json
import sys
member_id, team_id = sys.argv[1:]
print(json.dumps({
    "memberId": member_id,
    "displayName": "Main Flow Smoke Member",
    "implementationKind": "workflow",
    "description": "CI main-flow runtime smoke member",
    "teamId": team_id,
}))
PY
)"

bind_body="$(python3 - "${workflow_id}" "${workflow_yaml}" <<'PY'
import json
import sys
workflow_id, workflow_yaml = sys.argv[1:]
print(json.dumps({
    "revisionId": "rev-main-flow-smoke",
    "workflow": {
        "workflowId": workflow_id,
        "workflowYamls": [workflow_yaml],
    },
}))
PY
)"

save_bind_body="$(python3 - "${workflow_id}" "${workflow_yaml}" <<'PY'
import json
import sys
workflow_id, workflow_yaml = sys.argv[1:]
print(json.dumps({
    "workflowId": workflow_id,
    "workflowYaml": workflow_yaml,
    "workflowName": "main-flow-smoke",
    "displayName": "Main Flow Smoke Workflow",
    "appId": "studio",
    "serviceId": "svc-main-flow-smoke",
    "exposureDesired": True,
}))
PY
)"

provision_body="$(python3 - "${team_id}" "${workflow_yaml}" <<'PY'
import json
import sys
team_id, workflow_yaml = sys.argv[1:]
print(json.dumps({
    "teamId": team_id,
    "displayName": "Main Flow Smoke Provisioned Workflow",
    "workflowYaml": workflow_yaml,
    "prompt": "main-flow smoke manual trigger",
    "runImmediately": True,
    "timezone": "UTC",
    "caller": {
        "platform": "nyxid",
        "externalUserId": "main-flow-smoke-user",
        "scope": "proxy",
    },
}))
PY
)"

preview_body='{"cronExpression":"*/5 * * * *","timezone":"UTC","count":1}'

echo "Starting main-flow runtime smoke with in-memory infrastructure..."
acquire_distributed_smoke_lock "${LOCK_OWNER}"
ensure_local_tcp_ports_free \
  "${LOCK_OWNER}" \
  "${HTTP_PORT}" \
  "${SILO_PORT}" \
  "${GATEWAY_PORT}"

echo "Publishing Mainnet host app..."
dotnet publish src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj \
  -c Release \
  -o "${PUBLISH_DIR}" \
  --nologo \
  --tl:off \
  -m:1 \
  -p:UseSharedCompilation=false \
  -p:NuGetAudit=false

if [[ ! -f "${APP_DLL}" ]]; then
  echo "Published host application not found: ${APP_DLL}" >&2
  exit 1
fi

echo "Starting Mainnet host for main-flow smoke..."
start_host

echo "Log directory: ${log_dir}"
if ! wait_for_readiness; then
  print_key_logs
  echo "Main-flow smoke host did not become ready." >&2
  exit 1
fi

team_response="${log_dir}/team.json"
team_readmodel_response="${log_dir}/team-readmodel.json"
member_response="${log_dir}/member.json"
bind_response="${log_dir}/bind.json"
save_bind_response="${log_dir}/save-bind.json"
workflow_list_response="${log_dir}/workflow-list.json"
preview_response="${log_dir}/schedule-preview.json"
provision_response="${log_dir}/provision.json"
schedule_readmodel_response="${log_dir}/schedule-readmodel.json"
run_now_response="${log_dir}/run-now.json"

request_json POST "/api/scopes/${scope_id}/teams" "${team_body}" "201" "${team_response}"
assert_json_field "${team_response}" "teamId" "${team_id}"
wait_for_status_code "/api/scopes/${scope_id}/teams/${team_id}" "200" "${team_readmodel_response}"

request_json POST "/api/scopes/${scope_id}/members" "${member_body}" "201" "${member_response}"
assert_json_field "${member_response}" "memberId" "${member_id}"

request_json PUT "/api/scopes/${scope_id}/members/${member_id}/binding" "${bind_body}" "202" "${bind_response}"
assert_json_field "${bind_response}" "memberId" "${member_id}"

request_json POST "/api/scopes/${scope_id}/workflows:save-and-bind" "${save_bind_body}" "202" "${save_bind_response}"
request_json GET "/api/scopes/${scope_id}/workflows?includeSource=true" "" "200" "${workflow_list_response}"

request_json POST "/api/schedules/preview" "${preview_body}" "200" "${preview_response}"
# Provisioning uses deterministic identities and explicitly guarantees retry
# convergence after an ambiguous transport timeout.
request_json POST "/api/scopes/${scope_id}/provision-workflow" "${provision_body}" "202" "${provision_response}" 3
assert_json_field "${provision_response}" "teamId" "${team_id}"

schedule_id="$(python3 - "${provision_response}" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as handle:
    data = json.load(handle)
print(data.get("scheduleId") or "")
PY
)"
provisioned_member_id="$(python3 - "${provision_response}" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as handle:
    data = json.load(handle)
print(data.get("memberId") or "")
PY
)"
if [[ -z "${schedule_id}" ]]; then
  echo "Provisioning response did not include scheduleId." >&2
  python3 -m json.tool "${provision_response}" >&2 2>/dev/null || cat "${provision_response}" >&2
  exit 1
fi
if [[ -z "${provisioned_member_id}" ]]; then
  echo "Provisioning response did not include memberId." >&2
  python3 -m json.tool "${provision_response}" >&2 2>/dev/null || cat "${provision_response}" >&2
  exit 1
fi

schedule_owner_query="ownerKind=studio_member_automation&ownerScopeId=${scope_id}&ownerTeamId=${team_id}&ownerMemberId=${provisioned_member_id}"
wait_for_status_code "/api/schedules/${schedule_id}?${schedule_owner_query}" "200" "${schedule_readmodel_response}"
assert_json_field "${schedule_readmodel_response}" "schedule.scheduleId" "${schedule_id}"

run_now_body="$(python3 - "${scope_id}" "${team_id}" "${provisioned_member_id}" <<'PY'
import json
import sys
scope_id, team_id, member_id = sys.argv[1:]
print(json.dumps({
    "owner": {
        "kind": "studio_member_automation",
        "scopeId": scope_id,
        "teamId": team_id,
        "memberId": member_id,
    },
}))
PY
)"
request_json POST "/api/schedules/${schedule_id}:run-now" "${run_now_body}" "202" "${run_now_response}"

print_key_logs
if grep -qE "Duplicate workflow definition name|Unhandled exception|Application startup exception" "${log_file}"; then
  echo "Host log contains a fatal startup marker." >&2
  exit 1
fi

if ! grep -q "Orleans Silo started." "${log_file}"; then
  echo "Host did not start Orleans Silo." >&2
  exit 1
fi

echo "Main-flow runtime smoke test passed."
