#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_ROOT="$(cd "${APP_DIR}/../.." && pwd)"
RUNTIME_DIR="${APP_DIR}/.temp/dev-stack"

API_PORT="${AEVATAR_CONSOLE_API_PORT:-5080}"
CONFIG_PORT="${AEVATAR_CONSOLE_CONFIG_PORT:-6688}"
STUDIO_PORT="${AEVATAR_CONSOLE_STUDIO_PORT:-6690}"
FRONTEND_PORT="${AEVATAR_CONSOLE_FRONTEND_PORT:-5173}"
APP_SCOPE_ID="${AEVATAR_CONSOLE_SCOPE_ID:-aevatar}"
STUDIO_NYXID_ENABLED="${AEVATAR_CONSOLE_STUDIO_NYXID_ENABLED:-true}"

API_URL="http://127.0.0.1:${API_PORT}"
CONFIG_URL="http://127.0.0.1:${CONFIG_PORT}"
STUDIO_URL="http://127.0.0.1:${STUDIO_PORT}"
FRONTEND_URL="http://127.0.0.1:${FRONTEND_PORT}"

API_PROJECT="${REPO_ROOT}/src/workflow/Aevatar.Workflow.Host.Api"
CONFIG_PROJECT="${REPO_ROOT}/tools/Aevatar.Tools.Config"
STUDIO_PROJECT="${REPO_ROOT}/tools/Aevatar.Tools.Cli"

mkdir -p "${RUNTIME_DIR}"

usage() {
  cat <<EOF
Usage: $(basename "$0") [start|stop|restart|status]

Starts the local stack required by aevatar-console-web:
  - Workflow Host API     ${API_URL}
  - Configuration API     ${CONFIG_URL}
  - Studio sidecar        ${STUDIO_URL}
  - Frontend dev server   ${FRONTEND_URL}

Environment overrides:
  AEVATAR_CONSOLE_API_PORT
  AEVATAR_CONSOLE_CONFIG_PORT
  AEVATAR_CONSOLE_STUDIO_PORT
  AEVATAR_CONSOLE_FRONTEND_PORT
  AEVATAR_CONSOLE_SCOPE_ID
  AEVATAR_CONSOLE_STUDIO_NYXID_ENABLED
EOF
}

service_port() {
  case "$1" in
    api) echo "${API_PORT}" ;;
    config) echo "${CONFIG_PORT}" ;;
    studio) echo "${STUDIO_PORT}" ;;
    frontend) echo "${FRONTEND_PORT}" ;;
    *) return 1 ;;
  esac
}

service_url() {
  case "$1" in
    api) echo "${API_URL}/api/workflows" ;;
    config) echo "${CONFIG_URL}/api/configuration/health" ;;
    studio) echo "${STUDIO_URL}/api/app/health" ;;
    frontend) echo "${FRONTEND_URL}" ;;
    *) return 1 ;;
  esac
}

service_log_file() {
  echo "${RUNTIME_DIR}/$1.log"
}

service_pid_file() {
  echo "${RUNTIME_DIR}/$1.pid"
}

service_process_line() {
  local port
  port="$(service_port "$1")"
  lsof -nP -iTCP:"${port}" -sTCP:LISTEN 2>/dev/null | tail -n +2 || true
}

service_pid() {
  local port
  port="$(service_port "$1")"
  lsof -tiTCP:"${port}" -sTCP:LISTEN 2>/dev/null | head -n 1 || true
}

is_pid_running() {
  local pid="$1"
  [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null
}

is_service_listening() {
  local name="$1"
  [[ -n "$(service_pid "${name}")" ]]
}

wait_for_url() {
  local url="$1"
  local attempts="$2"
  local delay_seconds="$3"
  local attempt

  for (( attempt=1; attempt<=attempts; attempt+=1 )); do
    if curl -fsS "${url}" >/dev/null 2>&1; then
      return 0
    fi
    sleep "${delay_seconds}"
  done

  return 1
}

start_service() {
  local name="$1"
  local log_file pid_file pid url
  log_file="$(service_log_file "${name}")"
  pid_file="$(service_pid_file "${name}")"
  url="$(service_url "${name}")"

  if is_service_listening "${name}"; then
    echo "[skip] ${name} already listening"
    service_process_line "${name}"
    return 0
  fi

  rm -f "${pid_file}"
  echo "[start] ${name} -> ${log_file}"

  case "${name}" in
    api)
      nohup env ASPNETCORE_URLS="${API_URL}" \
        dotnet run --project "${API_PROJECT}" >"${log_file}" 2>&1 &
      ;;
    config)
      nohup dotnet run --project "${CONFIG_PROJECT}" -- --port "${CONFIG_PORT}" --no-browser \
        >"${log_file}" 2>&1 &
      ;;
    studio)
      nohup env Cli__App__NyxId__Enabled="${STUDIO_NYXID_ENABLED}" \
        Cli__App__ScopeId="${APP_SCOPE_ID}" \
        dotnet run --project "${STUDIO_PROJECT}" -- app --no-browser --port "${STUDIO_PORT}" --api-base "${API_URL}" \
        >"${log_file}" 2>&1 &
      ;;
    frontend)
      pid="$(
        AEVATAR_API_TARGET="${API_URL}" \
        AEVATAR_CONFIGURATION_API_TARGET="${CONFIG_URL}" \
        AEVATAR_STUDIO_API_TARGET="${STUDIO_URL}" \
        node "${APP_DIR}/scripts/start-frontend-detached.mjs" "${log_file}"
      )"
      echo "${pid}" >"${pid_file}"
      ;;
    *)
      echo "[error] unknown service: ${name}" >&2
      return 1
      ;;
  esac

  if [[ "${name}" != "frontend" ]]; then
    pid=$!
    echo "${pid}" >"${pid_file}"
  fi

  if ! wait_for_url "${url}" 120 1; then
    echo "[error] ${name} did not become ready: ${url}" >&2
    if [[ -f "${pid_file}" ]]; then
      pid="$(cat "${pid_file}")"
      if is_pid_running "${pid}"; then
        kill "${pid}" 2>/dev/null || true
      fi
      rm -f "${pid_file}"
    fi
    echo "[hint] inspect log: ${log_file}" >&2
    return 1
  fi

  pid="$(service_pid "${name}")"
  if [[ -n "${pid}" ]]; then
    echo "${pid}" >"${pid_file}"
  fi

  echo "[ready] ${name} -> ${url}"
}

stop_service() {
  local name="$1"
  local pid_file pid observed_pid
  pid_file="$(service_pid_file "${name}")"

  pid=""
  if [[ -f "${pid_file}" ]]; then
    pid="$(cat "${pid_file}")"
  fi

  if is_pid_running "${pid}"; then
    echo "[stop] ${name} pid=${pid}"
    kill "${pid}" 2>/dev/null || true
  else
    observed_pid="$(service_pid "${name}")"
    if [[ -n "${observed_pid}" ]]; then
      echo "[skip] ${name} is listening on port $(service_port "${name}") but was not started by this script"
    else
      echo "[skip] ${name} is not running"
    fi
    rm -f "${pid_file}"
    return 0
  fi

  for _ in $(seq 1 30); do
    if ! is_pid_running "${pid}"; then
      break
    fi
    sleep 1
  done

  if is_pid_running "${pid}"; then
    echo "[warn] ${name} did not exit in time, sending SIGKILL"
    kill -9 "${pid}" 2>/dev/null || true
  fi

  rm -f "${pid_file}"
}

status_service() {
  local name="$1"
  local pid_file pid
  pid_file="$(service_pid_file "${name}")"
  pid="$(service_pid "${name}")"

  if [[ -n "${pid}" ]]; then
    echo "[up]   ${name} port=$(service_port "${name}") pid=${pid}"
    service_process_line "${name}"
  elif [[ -f "${pid_file}" ]]; then
    echo "[down] ${name} stale-pid=$(cat "${pid_file}")"
  else
    echo "[down] ${name}"
  fi
}

command="${1:-start}"

case "${command}" in
  start)
    start_service api
    start_service config
    start_service studio
    start_service frontend
    echo
    echo "Console URLs:"
    echo "  frontend: ${FRONTEND_URL}"
    echo "  api:      ${API_URL}"
    echo "  config:   ${CONFIG_URL}"
    echo "  studio:   ${STUDIO_URL}"
    echo "  scope:    ${APP_SCOPE_ID}"
    echo "  nyxid:    ${STUDIO_NYXID_ENABLED}"
    echo
    echo "Logs:"
    echo "  ${RUNTIME_DIR}/api.log"
    echo "  ${RUNTIME_DIR}/config.log"
    echo "  ${RUNTIME_DIR}/studio.log"
    echo "  ${RUNTIME_DIR}/frontend.log"
    ;;
  stop)
    stop_service frontend
    stop_service studio
    stop_service config
    stop_service api
    ;;
  restart)
    stop_service frontend
    stop_service studio
    stop_service config
    stop_service api
    start_service api
    start_service config
    start_service studio
    start_service frontend
    ;;
  status)
    status_service api
    status_service config
    status_service studio
    status_service frontend
    ;;
  -h|--help|help)
    usage
    ;;
  *)
    echo "[error] unknown command: ${command}" >&2
    usage >&2
    exit 1
    ;;
esac
