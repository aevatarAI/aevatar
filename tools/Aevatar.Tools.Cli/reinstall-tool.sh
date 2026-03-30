#!/usr/bin/env bash

set -euo pipefail

TOOL_PACKAGE_ID="aevatar"
CONFIGURATION="${1:-Release}"
APP_PORT="${2:-${AEVATAR_APP_PORT:-6688}}"
RESTART_ON_PORT_CONFLICT="${AEVATAR_REINSTALL_RESTART_ON_PORT_CONFLICT:-1}"
APP_LOG_FILE="${AEVATAR_REINSTALL_APP_LOG_FILE:-/tmp/aevatar-app-${APP_PORT}.log}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT_PATH="${REPO_ROOT}/tools/Aevatar.Tools.Cli/Aevatar.Tools.Cli.csproj"
PACKAGE_SOURCE_DIR="${REPO_ROOT}/tools/Aevatar.Tools.Cli/bin/${CONFIGURATION}"
APP_RESTART_NEEDED=0

echo "==> Tool package: ${TOOL_PACKAGE_ID}"
echo "==> Build configuration: ${CONFIGURATION}"
echo "==> Project: ${PROJECT_PATH}"
echo "==> App port watch: ${APP_PORT}"

list_listening_pids() {
  local port="$1"
  lsof -tiTCP:"${port}" -sTCP:LISTEN 2>/dev/null | sort -u
}

port_has_listener() {
  local port="$1"
  local pids
  pids="$(list_listening_pids "${port}")"
  [[ -n "${pids}" ]]
}

kill_processes_on_port() {
  local port="$1"
  local pids
  local i
  local remaining

  pids="$(list_listening_pids "${port}")"
  if [[ -z "${pids}" ]]; then
    return 1
  fi

  echo "==> Port ${port} is occupied. PID(s): $(echo "${pids}" | tr '\n' ' ')"
  echo "==> Stopping process(es) on port ${port}..."
  while IFS= read -r pid; do
    [[ -z "${pid}" ]] && continue
    kill "${pid}" 2>/dev/null || true
  done <<< "${pids}"

  i=0
  while [[ "${i}" -lt 20 ]]; do
    if ! port_has_listener "${port}"; then
      break
    fi
    sleep 0.25
    i=$((i + 1))
  done

  if port_has_listener "${port}"; then
    remaining="$(list_listening_pids "${port}")"
    echo "==> Force killing remaining PID(s): $(echo "${remaining}" | tr '\n' ' ')"
    while IFS= read -r pid; do
      [[ -z "${pid}" ]] && continue
      kill -9 "${pid}" 2>/dev/null || true
    done <<< "${remaining}"

    i=0
    while [[ "${i}" -lt 20 ]]; do
      if ! port_has_listener "${port}"; then
        break
      fi
      sleep 0.25
      i=$((i + 1))
    done
  fi

  if port_has_listener "${port}"; then
    echo "Failed to free port ${port}. Please release it manually and retry." >&2
    exit 1
  fi

  return 0
}

if [[ ! -f "${PROJECT_PATH}" ]]; then
  echo "Project file not found: ${PROJECT_PATH}" >&2
  exit 1
fi

if [[ "${RESTART_ON_PORT_CONFLICT}" == "1" ]]; then
  if kill_processes_on_port "${APP_PORT}"; then
    APP_RESTART_NEEDED=1
    echo "==> Port ${APP_PORT} has been released. App will be restarted after reinstall."
  else
    echo "==> Port ${APP_PORT} is free."
  fi
else
  echo "==> Port conflict handling disabled by AEVATAR_REINSTALL_RESTART_ON_PORT_CONFLICT=${RESTART_ON_PORT_CONFLICT}."
fi

if dotnet tool list --global | awk -v tool="${TOOL_PACKAGE_ID}" '
  NR > 2 && $1 == tool { found = 1 }
  END { exit(found ? 0 : 1) }
'; then
  echo "==> Found existing global tool. Uninstalling ${TOOL_PACKAGE_ID}..."
  dotnet tool uninstall --global "${TOOL_PACKAGE_ID}"
else
  echo "==> Global tool ${TOOL_PACKAGE_ID} is not installed. Skipping uninstall."
fi

FRONTEND_DIR="${REPO_ROOT}/tools/Aevatar.Tools.Cli/Frontend"
if [[ -f "${FRONTEND_DIR}/package.json" ]]; then
  echo "==> Building frontend..."
  (cd "${FRONTEND_DIR}" && npm ci --ignore-scripts 2>/dev/null || npm install && npx vite build)
else
  echo "==> Frontend directory not found, skipping frontend build."
fi

echo "==> Packing tool..."
dotnet pack "${PROJECT_PATH}" -c "${CONFIGURATION}"

if [[ ! -d "${PACKAGE_SOURCE_DIR}" ]]; then
  echo "Package source directory not found: ${PACKAGE_SOURCE_DIR}" >&2
  exit 1
fi

echo "==> Installing tool from local source only: ${PACKAGE_SOURCE_DIR}"
TMP_NUGET_CONFIG="$(mktemp)"
trap 'rm -f "${TMP_NUGET_CONFIG}"' EXIT
cat > "${TMP_NUGET_CONFIG}" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="${PACKAGE_SOURCE_DIR}" />
  </packageSources>
</configuration>
EOF

dotnet tool install --global --configfile "${TMP_NUGET_CONFIG}" --no-cache "${TOOL_PACKAGE_ID}"

if [[ "${APP_RESTART_NEEDED}" -eq 1 ]]; then
  echo "==> Restarting ${TOOL_PACKAGE_ID} app on port ${APP_PORT}..."
  nohup "${TOOL_PACKAGE_ID}" app --no-browser --port "${APP_PORT}" > "${APP_LOG_FILE}" 2>&1 &
  APP_PID=$!
  sleep 1
  if ! kill -0 "${APP_PID}" 2>/dev/null; then
    echo "Failed to restart ${TOOL_PACKAGE_ID} app. Check logs: ${APP_LOG_FILE}" >&2
    exit 1
  fi
  echo "==> Restarted ${TOOL_PACKAGE_ID} app (pid=${APP_PID}). Logs: ${APP_LOG_FILE}"
fi

echo "==> Done. You can run:"
echo "    ${TOOL_PACKAGE_ID}"
