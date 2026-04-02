#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_FILE="${SCRIPT_DIR}/Aevatar.Mainnet.Host.Api.csproj"
PROJECT_PATTERN='Aevatar\.Mainnet\.Host\.Api(\.dll|\.csproj)'
CONFIGURATION="${AEVATAR_APP_CONFIGURATION:-Debug}"
API_HOST="${AEVATAR_APP_HOST:-127.0.0.1}"
API_PORT="${AEVATAR_APP_PORT:-5080}"
API_URL="http://${API_HOST}:${API_PORT}"
LOG_FILE="${SCRIPT_DIR}/boot.log"
PID_FILE="${SCRIPT_DIR}/boot.pid"

usage() {
  cat <<'EOF'
Usage:
  ./boot.sh [--port PORT] [--configuration CONFIGURATION]

Description:
  Stops the previous Aevatar.Mainnet.Host.Api process, frees the configured
  port, and starts a fresh instance in the background.

Environment:
  AEVATAR_APP_CONFIGURATION   dotnet configuration, default: Debug
  AEVATAR_APP_HOST            bind host, default: 127.0.0.1
  AEVATAR_APP_PORT            bind port, default: 5080

Files:
  boot.log    runtime output
  boot.pid    started process id
EOF
}

while [[ $# -gt 0 ]]; do
  case "${1}" in
    --port)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      API_PORT="${2}"
      API_URL="http://${API_HOST}:${API_PORT}"
      shift 2
      ;;
    --port=*)
      API_PORT="${1#--port=}"
      API_URL="http://${API_HOST}:${API_PORT}"
      shift
      ;;
    --configuration)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      CONFIGURATION="${2}"
      shift 2
      ;;
    --configuration=*)
      CONFIGURATION="${1#--configuration=}"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: ${1}" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ ! -f "${PROJECT_FILE}" ]]; then
  echo "Project file not found: ${PROJECT_FILE}" >&2
  exit 1
fi

list_listening_pids() {
  lsof -tiTCP:"${1}" -sTCP:LISTEN 2>/dev/null | sort -u || true
}

list_project_pids() {
  pgrep -f "${PROJECT_PATTERN}" 2>/dev/null | sort -u || true
}

collect_target_pids() {
  {
    if [[ -f "${PID_FILE}" ]]; then
      cat "${PID_FILE}"
    fi
    list_project_pids
    list_listening_pids "${API_PORT}"
  } | awk '/^[0-9]+$/ { print $1 }' | sort -u
}

wait_for_pids_to_exit() {
  local pids="$1"

  for _ in $(seq 1 20); do
    local remaining=""
    while IFS= read -r pid; do
      [[ -z "${pid}" ]] && continue
      if kill -0 "${pid}" 2>/dev/null; then
        remaining="${remaining}${pid}"$'\n'
      fi
    done <<< "${pids}"

    if [[ -z "${remaining}" ]]; then
      return 0
    fi

    sleep 0.25
  done

  return 1
}

kill_existing_processes() {
  local pids
  pids="$(collect_target_pids)"

  if [[ -z "${pids}" ]]; then
    return 0
  fi

  echo "==> Stopping existing Mainnet Host API process(es): $(echo "${pids}" | tr '\n' ' ')"

  while IFS= read -r pid; do
    [[ -z "${pid}" ]] && continue
    if [[ "${pid}" == "$$" ]]; then
      continue
    fi

    kill "${pid}" 2>/dev/null || true
  done <<< "${pids}"

  if wait_for_pids_to_exit "${pids}"; then
    return 0
  fi

  echo "==> Force killing remaining process(es)..."
  while IFS= read -r pid; do
    [[ -z "${pid}" ]] && continue
    if [[ "${pid}" == "$$" ]]; then
      continue
    fi

    kill -9 "${pid}" 2>/dev/null || true
  done <<< "${pids}"

  sleep 1
}

wait_for_port_ready() {
  local pid="$1"

  for _ in $(seq 1 120); do
    if ! kill -0 "${pid}" 2>/dev/null; then
      return 1
    fi

    if [[ -n "$(list_listening_pids "${API_PORT}")" ]]; then
      return 0
    fi

    sleep 1
  done

  return 1
}

kill_existing_processes

if [[ -n "$(list_listening_pids "${API_PORT}")" ]]; then
  echo "Port ${API_PORT} is still occupied after cleanup." >&2
  exit 1
fi

echo "==> Starting Aevatar.Mainnet.Host.Api"
echo "==> URL: ${API_URL}"
echo "==> Log: ${LOG_FILE}"

nohup dotnet run \
  --project "${PROJECT_FILE}" \
  -c "${CONFIGURATION}" \
  --urls "${API_URL}" \
  > "${LOG_FILE}" 2>&1 &

NEW_PID=$!
echo "${NEW_PID}" > "${PID_FILE}"

if ! wait_for_port_ready "${NEW_PID}"; then
  echo "Aevatar.Mainnet.Host.Api failed to start. Last log lines:" >&2
  tail -n 40 "${LOG_FILE}" >&2 || true
  exit 1
fi

echo "==> Started Aevatar.Mainnet.Host.Api (pid ${NEW_PID})"
