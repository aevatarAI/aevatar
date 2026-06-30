#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"
source "${SCRIPT_DIR}/distributed_smoke_common.sh"

HTTP_PORT="${AEVATAR_SERVICE_SMOKE_HTTP_PORT:-18081}"
SILO_PORT="${AEVATAR_SERVICE_SMOKE_SILO_PORT:-11111}"
GATEWAY_PORT="${AEVATAR_SERVICE_SMOKE_GATEWAY_PORT:-30000}"
WAIT_SECONDS="${AEVATAR_SERVICE_SMOKE_WAIT_SECONDS:-120}"
PUBLISH_DIR="${AEVATAR_SERVICE_SMOKE_PUBLISH_DIR:-/tmp/aevatar-mainnet-service-smoke-publish}"
APP_DLL="${PUBLISH_DIR}/Aevatar.Mainnet.Host.Api.dll"
LOCK_OWNER="mainnet_single_node_service_smoke"

timestamp="$(date +%Y%m%d-%H%M%S)"
cluster_id="aevatar-mainnet-service-smoke-cluster-${timestamp}"
service_id="aevatar-mainnet-host-api-service-smoke"
log_dir="${AEVATAR_SERVICE_SMOKE_LOG_DIR:-/tmp/aevatar-mainnet-service-smoke-${timestamp}}"
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
    ASPNETCORE_ENVIRONMENT=Distributed \
    ASPNETCORE_URLS="http://127.0.0.1:${HTTP_PORT}" \
    AEVATAR_ActorRuntime__Provider=Orleans \
    AEVATAR_ActorRuntime__OrleansStreamBackend=InMemory \
    AEVATAR_ActorRuntime__OrleansPersistenceBackend=InMemory \
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
    dotnet "${APP_DLL}" >"${log_file}" 2>&1
  ) &

  pids+=("$!")
}

probe_http_ok() {
  local path="$1"
  local code
  code="$(curl --max-time 5 -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:${HTTP_PORT}${path}" || true)"
  if [[ "${code}" != "200" ]]; then
    echo "Expected ${path} to return HTTP 200, got ${code}." >&2
    return 1
  fi
}

print_key_logs() {
  echo "HOST_KEYLOGS"
  if [[ -f "${log_file}" ]]; then
    grep -nE "Duplicate workflow definition name|Loaded [0-9]+ workflow definition|Orleans Silo started|Now listening on|Unhandled exception|Application startup exception" "${log_file}" || true
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
  code="$(curl --max-time 2 -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:${HTTP_PORT}/api/status" || true)"
  echo "HTTP_${HTTP_PORT}_API_STATUS=${code}"

  if [[ "${ready}" != "1" ]]; then
    return 1
  fi
}

echo "Starting single-node Mainnet service smoke with in-memory infrastructure..."
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

echo "Starting single-node Mainnet host..."
start_host

echo "Log directory: ${log_dir}"
if ! wait_for_readiness; then
  print_key_logs
  echo "Single-node Mainnet host did not become ready." >&2
  exit 1
fi

probe_http_ok "/api/status"
probe_http_ok "/status"
print_key_logs

if grep -qE "Duplicate workflow definition name|Unhandled exception|Application startup exception" "${log_file}"; then
  echo "Host log contains a fatal startup marker." >&2
  exit 1
fi

if ! grep -q "Orleans Silo started." "${log_file}"; then
  echo "Host did not start Orleans Silo." >&2
  exit 1
fi

echo "Single-node Mainnet service smoke test passed."
