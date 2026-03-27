#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_JSON="${SCRIPT_DIR}/package.json"
FRONTEND_PORT="${AEVATAR_CONSOLE_FRONTEND_PORT:-5173}"
API_TARGET="${AEVATAR_API_TARGET:-http://127.0.0.1:5080}"
STUDIO_API_TARGET="${AEVATAR_STUDIO_API_TARGET:-${API_TARGET}}"
LOG_FILE="${SCRIPT_DIR}/boot.log"
PID_FILE="${SCRIPT_DIR}/boot.pid"

usage() {
  cat <<'EOF'
Usage:
  ./boot.sh [--port PORT] [--api-target URL] [--studio-api-target URL]

Description:
  Stops the previous aevatar-console-web dev server, frees the configured
  frontend port, removes generated frontend build artifacts, and starts a
  fresh instance in the background.

Environment:
  AEVATAR_CONSOLE_FRONTEND_PORT   dev server port, default: 5173
  AEVATAR_API_TARGET              proxy target for /api/*, default: http://127.0.0.1:5080
  AEVATAR_STUDIO_API_TARGET       proxy target for Studio endpoints, default: AEVATAR_API_TARGET

Files:
  boot.log    runtime output
  boot.pid    started process id
EOF
}

while [[ $# -gt 0 ]]; do
  case "${1}" in
    --port)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      FRONTEND_PORT="${2}"
      shift 2
      ;;
    --port=*)
      FRONTEND_PORT="${1#--port=}"
      shift
      ;;
    --api-target)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      API_TARGET="${2}"
      shift 2
      ;;
    --api-target=*)
      API_TARGET="${1#--api-target=}"
      shift
      ;;
    --studio-api-target)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      STUDIO_API_TARGET="${2}"
      shift 2
      ;;
    --studio-api-target=*)
      STUDIO_API_TARGET="${1#--studio-api-target=}"
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

if [[ ! -f "${PACKAGE_JSON}" ]]; then
  echo "package.json not found: ${PACKAGE_JSON}" >&2
  exit 1
fi

list_listening_pids() {
  lsof -tiTCP:"${1}" -sTCP:LISTEN 2>/dev/null | sort -u || true
}

collect_target_pids() {
  {
    if [[ -f "${PID_FILE}" ]]; then
      cat "${PID_FILE}"
    fi
    list_listening_pids "${FRONTEND_PORT}"
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

  echo "==> Stopping existing frontend process(es): $(echo "${pids}" | tr '\n' ' ')"

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

clean_generated_artifacts() {
  echo "==> Cleaning generated frontend artifacts..."
  rm -rf \
    "${SCRIPT_DIR}/dist" \
    "${SCRIPT_DIR}/node_modules/.cache" \
    "${SCRIPT_DIR}/src/.umi" \
    "${SCRIPT_DIR}/src/.umi-production"
}

wait_for_port_ready() {
  local pid="$1"
  local frontend_url="http://127.0.0.1:${FRONTEND_PORT}"

  for _ in $(seq 1 120); do
    if ! kill -0 "${pid}" 2>/dev/null; then
      return 1
    fi

    if curl -sf -o /dev/null "${frontend_url}/"; then
      return 0
    fi

    sleep 1
  done

  return 1
}

kill_existing_processes
clean_generated_artifacts

if [[ -n "$(list_listening_pids "${FRONTEND_PORT}")" ]]; then
  echo "Port ${FRONTEND_PORT} is still occupied after cleanup." >&2
  exit 1
fi

echo "==> Starting aevatar-console-web"
echo "==> Frontend: http://127.0.0.1:${FRONTEND_PORT}"
echo "==> API target: ${API_TARGET}"
echo "==> Studio API target: ${STUDIO_API_TARGET}"
echo "==> Log: ${LOG_FILE}"

(
  cd "${SCRIPT_DIR}"
  export PORT="${FRONTEND_PORT}"
  export AEVATAR_CONSOLE_FRONTEND_PORT="${FRONTEND_PORT}"
  export AEVATAR_API_TARGET="${API_TARGET}"
  export AEVATAR_STUDIO_API_TARGET="${STUDIO_API_TARGET}"
  nohup pnpm start:dev > "${LOG_FILE}" 2>&1 &
  echo $! > "${PID_FILE}"
)

NEW_PID="$(cat "${PID_FILE}")"

if ! wait_for_port_ready "${NEW_PID}"; then
  echo "aevatar-console-web failed to start. Last log lines:" >&2
  tail -n 40 "${LOG_FILE}" >&2 || true
  exit 1
fi

echo "==> Started aevatar-console-web (pid ${NEW_PID})"
