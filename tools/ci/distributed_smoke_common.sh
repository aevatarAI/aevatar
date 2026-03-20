#!/usr/bin/env bash

set -euo pipefail

DISTRIBUTED_SMOKE_LOCK_DIR="${AEVATAR_DISTRIBUTED_SMOKE_LOCK_DIR:-${TMPDIR:-/tmp}/aevatar-distributed-smoke.lock}"
DISTRIBUTED_SMOKE_LOCK_WAIT_SECONDS="${AEVATAR_DISTRIBUTED_SMOKE_LOCK_WAIT_SECONDS:-900}"
DISTRIBUTED_SMOKE_LOCK_POLL_SECONDS="${AEVATAR_DISTRIBUTED_SMOKE_LOCK_POLL_SECONDS:-1}"
DISTRIBUTED_SMOKE_LOCK_ACQUIRED=0

_distributed_smoke_lock_pid() {
  sed -n '1p' "${DISTRIBUTED_SMOKE_LOCK_DIR}/pid" 2>/dev/null || true
}

_distributed_smoke_lock_owner() {
  sed -n '1p' "${DISTRIBUTED_SMOKE_LOCK_DIR}/owner" 2>/dev/null || true
}

_distributed_smoke_lock_is_live() {
  local pid="$1"
  [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null
}

acquire_distributed_smoke_lock() {
  local owner="$1"
  local waited=0

  while ! mkdir "${DISTRIBUTED_SMOKE_LOCK_DIR}" 2>/dev/null; do
    local pid
    local active_owner
    pid="$(_distributed_smoke_lock_pid)"
    active_owner="$(_distributed_smoke_lock_owner)"

    if ! _distributed_smoke_lock_is_live "${pid}"; then
      echo "Removing stale distributed smoke lock: ${DISTRIBUTED_SMOKE_LOCK_DIR} (owner=${active_owner:-unknown}, pid=${pid:-unknown})"
      rm -rf "${DISTRIBUTED_SMOKE_LOCK_DIR}"
      continue
    fi

    if (( waited >= DISTRIBUTED_SMOKE_LOCK_WAIT_SECONDS )); then
      echo "Another distributed smoke is still running (owner=${active_owner:-unknown}, pid=${pid:-unknown}, lock=${DISTRIBUTED_SMOKE_LOCK_DIR})." >&2
      echo "Increase AEVATAR_DISTRIBUTED_SMOKE_LOCK_WAIT_SECONDS or wait for the other run to finish." >&2
      return 1
    fi

    if (( waited == 0 )); then
      echo "Waiting for distributed smoke lock: ${DISTRIBUTED_SMOKE_LOCK_DIR} (owner=${active_owner:-unknown}, pid=${pid:-unknown})"
    fi

    sleep "${DISTRIBUTED_SMOKE_LOCK_POLL_SECONDS}"
    waited=$((waited + DISTRIBUTED_SMOKE_LOCK_POLL_SECONDS))
  done

  printf '%s\n' "$$" > "${DISTRIBUTED_SMOKE_LOCK_DIR}/pid"
  printf '%s\n' "${owner}" > "${DISTRIBUTED_SMOKE_LOCK_DIR}/owner"
  printf '%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "${DISTRIBUTED_SMOKE_LOCK_DIR}/started_at"
  DISTRIBUTED_SMOKE_LOCK_ACQUIRED=1
  echo "Acquired distributed smoke lock: ${DISTRIBUTED_SMOKE_LOCK_DIR} (owner=${owner}, pid=$$)"
}

release_distributed_smoke_lock() {
  if [[ "${DISTRIBUTED_SMOKE_LOCK_ACQUIRED}" != "1" ]]; then
    return 0
  fi

  if [[ -d "${DISTRIBUTED_SMOKE_LOCK_DIR}" ]]; then
    local pid
    pid="$(_distributed_smoke_lock_pid)"
    if [[ "${pid}" == "$$" ]]; then
      rm -rf "${DISTRIBUTED_SMOKE_LOCK_DIR}"
    fi
  fi

  DISTRIBUTED_SMOKE_LOCK_ACQUIRED=0
}

_distributed_smoke_can_bind_local_port() {
  local port="$1"
  python3 - "${port}" <<'PY'
import socket
import sys

port = int(sys.argv[1])
sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
try:
    sock.bind(("127.0.0.1", port))
except OSError:
    sys.exit(1)
finally:
    sock.close()
PY
}

_distributed_smoke_describe_listener() {
  local port="$1"
  if ! command -v lsof >/dev/null 2>&1; then
    return 0
  fi

  (lsof -nP -iTCP:"${port}" -sTCP:LISTEN 2>/dev/null | awk 'NR==2 {print $1 " pid=" $2 " user=" $3 " endpoint=" $9}') || true
}

ensure_local_tcp_ports_free() {
  local scope="$1"
  shift

  local failed=0
  local port
  for port in "$@"; do
    if ! _distributed_smoke_can_bind_local_port "${port}"; then
      local listener
      listener="$(_distributed_smoke_describe_listener "${port}")"
      echo "Port preflight failed for ${scope}: TCP port ${port} is already in use${listener:+ (${listener})}." >&2
      failed=1
    fi
  done

  if (( failed != 0 )); then
    echo "Refusing to start ${scope}. Wait for the conflicting process to exit before retrying." >&2
    return 1
  fi
}

wait_kafka_health() {
  local container_name="$1"
  local attempts="${2:-30}"
  local sleep_seconds="${3:-2}"
  local try=0

  while (( try < attempts )); do
    local status
    status="$(docker inspect --format='{{.State.Health.Status}}' "${container_name}" 2>/dev/null || true)"
    if [[ "${status}" == "healthy" ]]; then
      echo "Kafka is healthy."
      return 0
    fi

    echo "Kafka status: ${status:-unknown}"
    sleep "${sleep_seconds}"
    try=$((try + 1))
  done

  echo "Kafka failed to become healthy."
  docker logs "${container_name}" || true
  return 1
}

require_bootstrapped_docker_infra_defaults() {
  local scope="$1"
  shift

  local failed=0
  local entry
  for entry in "$@"; do
    local name="${entry%%=*}"
    local remainder="${entry#*=}"
    local actual="${remainder%%|*}"
    local expected="${remainder#*|}"

    if [[ "${actual}" != "${expected}" ]]; then
      echo "Docker-bootstrap validation failed for ${scope}: ${name}='${actual}' must stay '${expected}' while AEVATAR_DISTRIBUTED_SMOKE_BOOTSTRAP_DOCKER_INFRA=true." >&2
      failed=1
    fi
  done

  if (( failed != 0 )); then
    echo "Use the compose defaults above, or set AEVATAR_DISTRIBUTED_SMOKE_BOOTSTRAP_DOCKER_INFRA=false and point the script at external infrastructure explicitly." >&2
    return 1
  fi
}

wait_tcp_endpoint() {
  local host="$1"
  local port="$2"
  local label="$3"
  local attempts="${4:-30}"
  local sleep_seconds="${5:-1}"
  local try=0

  while (( try < attempts )); do
    if python3 - "${host}" "${port}" <<'PY'
import socket
import sys

host = sys.argv[1]
port = int(sys.argv[2])

sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.settimeout(1.0)
try:
    sock.connect((host, port))
except OSError:
    sys.exit(1)
finally:
    sock.close()
PY
    then
      echo "${label} is reachable on ${host}:${port}."
      return 0
    fi

    echo "Waiting for ${label} on ${host}:${port}..."
    sleep "${sleep_seconds}"
    try=$((try + 1))
  done

  echo "${label} failed to become reachable on ${host}:${port}."
  return 1
}

wait_garnet_health() {
  local host="${1:-127.0.0.1}"
  local port="${2:-6379}"
  local attempts="${3:-30}"
  local sleep_seconds="${4:-1}"

  wait_tcp_endpoint "${host}" "${port}" "Garnet" "${attempts}" "${sleep_seconds}"
}

wait_elasticsearch_health() {
  local endpoint="${1:-http://127.0.0.1:9200}"
  local attempts="${2:-90}"
  local sleep_seconds="${3:-2}"
  local try=0

  while (( try < attempts )); do
    local status
    status="$(curl --max-time 2 -s "${endpoint}/_cluster/health" | rg -o "\"status\":\"[^\"]+\"" || true)"
    if [[ "${status}" == "\"status\":\"green\"" || "${status}" == "\"status\":\"yellow\"" ]]; then
      echo "Elasticsearch is ready: ${status}"
      return 0
    fi

    echo "Waiting for Elasticsearch on ${endpoint}..."
    sleep "${sleep_seconds}"
    try=$((try + 1))
  done

  echo "Elasticsearch failed to become ready."
  return 1
}

wait_neo4j_bolt() {
  local host="${1:-127.0.0.1}"
  local port="${2:-7687}"
  local attempts="${3:-90}"
  local sleep_seconds="${4:-2}"

  wait_tcp_endpoint "${host}" "${port}" "Neo4j bolt endpoint" "${attempts}" "${sleep_seconds}"
}
