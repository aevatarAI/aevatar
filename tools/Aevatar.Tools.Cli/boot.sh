#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_PATH="${SCRIPT_DIR}/Aevatar.Tools.Cli.csproj"
CONFIGURATION="${AEVATAR_APP_CONFIGURATION:-Debug}"

usage() {
  cat <<'EOF'
Usage:
  ./tools/Aevatar.Tools.Cli/boot.sh [port]
  ./tools/Aevatar.Tools.Cli/boot.sh [app args...]

Description:
  Starts `aevatar app` through `dotnet run` without requiring a global tool install.
  If the target port is occupied, the process on that port will be killed before
  the app starts again.

Examples:
  ./tools/Aevatar.Tools.Cli/boot.sh
  ./tools/Aevatar.Tools.Cli/boot.sh 7788
  ./tools/Aevatar.Tools.Cli/boot.sh --port 7788 --no-browser
  ./tools/Aevatar.Tools.Cli/boot.sh --api-base http://localhost:5001

Environment:
  AEVATAR_APP_CONFIGURATION   dotnet run build configuration, default Debug
EOF
}

if [[ ! -f "${PROJECT_PATH}" ]]; then
  echo "Project file not found: ${PROJECT_PATH}" >&2
  exit 1
fi

declare -a app_args=()
PORT="${AEVATAR_APP_PORT:-6688}"

if [[ $# -gt 0 ]]; then
  case "${1}" in
    -h|--help)
      usage
      exit 0
      ;;
    ''|*[!0-9]*)
      ;;
    *)
      PORT="${1}"
      app_args+=(--port "${1}")
      shift
      ;;
  esac
fi

while [[ $# -gt 0 ]]; do
  case "${1}" in
    --port)
      if [[ $# -lt 2 ]]; then
        echo "Missing value for ${1}" >&2
        exit 1
      fi
      PORT="${2}"
      app_args+=("${1}" "${2}")
      shift 2
      ;;
    --port=*)
      PORT="${1#--port=}"
      app_args+=("${1}")
      shift
      ;;
    --configuration|-c)
      if [[ $# -lt 2 ]]; then
        echo "Missing value for ${1}" >&2
        exit 1
      fi
      CONFIGURATION="${2}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      app_args+=("${1}")
      shift
      ;;
  esac
done

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

echo "==> Launching aevatar app via dotnet run"
echo "==> Project: ${PROJECT_PATH}"
echo "==> Configuration: ${CONFIGURATION}"
echo "==> Port: ${PORT}"
if [[ ${#app_args[@]} -gt 0 ]]; then
  echo "==> App args: ${app_args[*]}"
else
  echo "==> App args: <default>"
fi

if kill_processes_on_port "${PORT}"; then
  echo "==> Port ${PORT} has been released."
else
  echo "==> Port ${PORT} is free."
fi

cmd=(
  dotnet run
  --project "${PROJECT_PATH}"
  -c "${CONFIGURATION}"
  --
  app
)

if [[ ${#app_args[@]} -gt 0 ]]; then
  cmd+=("${app_args[@]}")
fi

exec "${cmd[@]}"
