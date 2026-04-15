#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

MAINNET_BOOT_SCRIPT="${REPO_ROOT}/src/Aevatar.Mainnet.Host.Api/boot.sh"
CLI_REINSTALL_SCRIPT="${REPO_ROOT}/tools/Aevatar.Tools.Cli/reinstall-tool.sh"

API_HOST="${AEVATAR_LOCAL_CLI_DEBUG_API_HOST:-127.0.0.1}"
API_PORT="${AEVATAR_LOCAL_CLI_DEBUG_API_PORT:-5080}"
API_CONFIGURATION="${AEVATAR_LOCAL_CLI_DEBUG_API_CONFIGURATION:-Debug}"
CLI_CONFIGURATION="${AEVATAR_LOCAL_CLI_DEBUG_CLI_CONFIGURATION:-Release}"
APP_PORT="${AEVATAR_LOCAL_CLI_DEBUG_APP_PORT:-6688}"
OPEN_BROWSER="${AEVATAR_LOCAL_CLI_DEBUG_OPEN_BROWSER:-0}"
APP_LOG_FILE="${AEVATAR_LOCAL_CLI_DEBUG_APP_LOG_FILE:-${SCRIPT_DIR}/local-cli-debug.app.log}"

usage() {
  cat <<'EOF'
Usage:
  ./scripts/local-cli-debug.sh [options]

Description:
  Starts the local Mainnet Host API, reinstalls the local `aevatar` global tool,
  and then launches `aevatar app` against that local API.

Options:
  --host HOST                     Mainnet API bind host. Default: 127.0.0.1
  --api-port PORT                 Mainnet API port. Default: 5080
  --app-port PORT                 Local aevatar app port. Default: 6688
  --api-configuration CONFIG      Mainnet API dotnet configuration. Default: Debug
  --cli-configuration CONFIG      CLI pack/install configuration. Default: Release
  --browser                       Open browser after app starts
  --no-browser                    Do not open browser after app starts (default)
  -h, --help                      Show this help

Environment:
  AEVATAR_LOCAL_CLI_DEBUG_API_HOST
  AEVATAR_LOCAL_CLI_DEBUG_API_PORT
  AEVATAR_LOCAL_CLI_DEBUG_API_CONFIGURATION
  AEVATAR_LOCAL_CLI_DEBUG_CLI_CONFIGURATION
  AEVATAR_LOCAL_CLI_DEBUG_APP_PORT
  AEVATAR_LOCAL_CLI_DEBUG_OPEN_BROWSER
  AEVATAR_LOCAL_CLI_DEBUG_APP_LOG_FILE

Notes:
  - This script deliberately keeps API port and app port separate.
  - It does not persist Cli:App:ApiBaseUrl; the app is launched with --url only.
EOF
}

while [[ $# -gt 0 ]]; do
  case "${1}" in
    --host)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      API_HOST="${2}"
      shift 2
      ;;
    --host=*)
      API_HOST="${1#--host=}"
      shift
      ;;
    --api-port)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      API_PORT="${2}"
      shift 2
      ;;
    --api-port=*)
      API_PORT="${1#--api-port=}"
      shift
      ;;
    --app-port)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      APP_PORT="${2}"
      shift 2
      ;;
    --app-port=*)
      APP_PORT="${1#--app-port=}"
      shift
      ;;
    --api-configuration)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      API_CONFIGURATION="${2}"
      shift 2
      ;;
    --api-configuration=*)
      API_CONFIGURATION="${1#--api-configuration=}"
      shift
      ;;
    --cli-configuration)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      CLI_CONFIGURATION="${2}"
      shift 2
      ;;
    --cli-configuration=*)
      CLI_CONFIGURATION="${1#--cli-configuration=}"
      shift
      ;;
    --browser)
      OPEN_BROWSER="1"
      shift
      ;;
    --no-browser)
      OPEN_BROWSER="0"
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

API_URL="http://${API_HOST}:${API_PORT}"
APP_URL="http://localhost:${APP_PORT}"

resolve_tool_path() {
  local tool_path

  tool_path="$(command -v aevatar 2>/dev/null || true)"
  if [[ -n "${tool_path}" ]]; then
    echo "${tool_path}"
    return 0
  fi

  tool_path="${HOME}/.dotnet/tools/aevatar"
  if [[ -x "${tool_path}" ]]; then
    echo "${tool_path}"
    return 0
  fi

  echo "Unable to resolve installed aevatar tool path." >&2
  return 1
}

wait_for_app_ready() {
  local url="$1"
  local app_port="$2"

  for _ in $(seq 1 40); do
    if command -v curl >/dev/null 2>&1; then
      if curl -fsS "${url}/api/health" >/dev/null 2>&1; then
        return 0
      fi
    elif lsof -tiTCP:"${app_port}" -sTCP:LISTEN >/dev/null 2>&1; then
      return 0
    fi

    sleep 0.25
  done

  return 1
}

if [[ ! -f "${MAINNET_BOOT_SCRIPT}" ]]; then
  echo "Mainnet boot script not found: ${MAINNET_BOOT_SCRIPT}" >&2
  exit 1
fi

if [[ ! -f "${CLI_REINSTALL_SCRIPT}" ]]; then
  echo "CLI reinstall script not found: ${CLI_REINSTALL_SCRIPT}" >&2
  exit 1
fi

echo "==> local-cli-debug"
echo "==> Mainnet API: ${API_URL} (${API_CONFIGURATION})"
echo "==> Local app:   ${APP_URL} (${CLI_CONFIGURATION})"
echo "==> App log:     ${APP_LOG_FILE}"

echo "==> Booting local Mainnet Host API..."
AEVATAR_APP_HOST="${API_HOST}" \
AEVATAR_APP_PORT="${API_PORT}" \
AEVATAR_APP_CONFIGURATION="${API_CONFIGURATION}" \
AEVATAR_ActorRuntime__OrleansStreamBackend=InMemory \
AEVATAR_ActorRuntime__OrleansPersistenceBackend=InMemory \
AEVATAR_ActorRuntime__Policies__Environment=Development \
AEVATAR_Projection__Policies__Environment=Development \
AEVATAR_Projection__Policies__DenyInMemoryDocumentReadStore=false \
AEVATAR_Projection__Policies__DenyInMemoryGraphFactStore=false \
Projection__Document__Providers__Elasticsearch__Enabled=false \
Projection__Document__Providers__InMemory__Enabled=true \
Projection__Graph__Providers__Neo4j__Enabled=false \
Projection__Graph__Providers__InMemory__Enabled=true \
bash "${MAINNET_BOOT_SCRIPT}" \
  --port "${API_PORT}" \
  --configuration "${API_CONFIGURATION}"

echo "==> Reinstalling local aevatar CLI..."
AEVATAR_APP_PORT="${APP_PORT}" \
AEVATAR_REINSTALL_RESTART_APP=0 \
AEVATAR_REINSTALL_APP_LOG_FILE="${APP_LOG_FILE}" \
bash "${CLI_REINSTALL_SCRIPT}" "${CLI_CONFIGURATION}" "${APP_PORT}"

TOOL_PATH="$(resolve_tool_path)"
echo "==> Using aevatar tool: ${TOOL_PATH}"

echo "==> Launching aevatar app against ${API_URL}..."
APP_ARGS=(app --port "${APP_PORT}" --url "${API_URL}")
if [[ "${OPEN_BROWSER}" != "1" ]]; then
  APP_ARGS+=(--no-browser)
fi

nohup "${TOOL_PATH}" "${APP_ARGS[@]}" > "${APP_LOG_FILE}" 2>&1 &
APP_PID=$!

if ! wait_for_app_ready "${APP_URL}" "${APP_PORT}"; then
  echo "aevatar app failed to start. Last log lines:" >&2
  tail -n 40 "${APP_LOG_FILE}" >&2 || true
  exit 1
fi

if ! kill -0 "${APP_PID}" 2>/dev/null; then
  echo "aevatar app process exited unexpectedly. Last log lines:" >&2
  tail -n 40 "${APP_LOG_FILE}" >&2 || true
  exit 1
fi

echo "==> Ready"
echo "    Mainnet API: ${API_URL}"
echo "    App UI:      ${APP_URL}"
echo "    App PID:     ${APP_PID}"
echo "    API log:     ${REPO_ROOT}/src/Aevatar.Mainnet.Host.Api/boot.log"
echo "    App log:     ${APP_LOG_FILE}"
