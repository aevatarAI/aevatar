#!/usr/bin/env bash
#
# boot.sh — (Re)start Mainnet Host API + aevatar console web frontend.
#
# Usage:
#   ./tools/Aevatar.Tools.Cli/boot.sh [--port PORT] [--no-browser]
#
# If any process is already listening on the Mainnet port, it is killed first.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
MAINNET_PROJECT="${REPO_ROOT}/src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj"
FRONTEND_DIR="${REPO_ROOT}/apps/aevatar-console-web"
CONFIGURATION="${AEVATAR_APP_CONFIGURATION:-Debug}"

API_PORT="${AEVATAR_APP_PORT:-5080}"
FRONTEND_PORT="${AEVATAR_CONSOLE_FRONTEND_PORT:-5173}"
NO_BROWSER=false

# ── Parse args ──────────────────────────────────────────────────
usage() {
  cat <<'EOF'
Usage:
  ./tools/Aevatar.Tools.Cli/boot.sh [--port PORT] [--no-browser]

Description:
  Kills any existing process on the API port, starts the Mainnet Host API,
  starts the aevatar console web frontend, and opens the browser.

Examples:
  ./tools/Aevatar.Tools.Cli/boot.sh
  ./tools/Aevatar.Tools.Cli/boot.sh --port 5080
  ./tools/Aevatar.Tools.Cli/boot.sh --no-browser

Environment:
  AEVATAR_APP_CONFIGURATION   dotnet build configuration (default: Debug)
  AEVATAR_APP_PORT            Mainnet API port (default: 5080)
EOF
}

while [[ $# -gt 0 ]]; do
  case "${1}" in
    --port)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      API_PORT="${2}"; shift 2 ;;
    --port=*)
      API_PORT="${1#--port=}"; shift ;;
    --no-browser)
      NO_BROWSER=true; shift ;;
    -h|--help)
      usage; exit 0 ;;
    *)
      if [[ "${1}" =~ ^[0-9]+$ ]]; then
        API_PORT="${1}"; shift
      else
        echo "Unknown option: ${1}" >&2; usage; exit 1
      fi ;;
  esac
done

# ── Port management ─────────────────────────────────────────────
list_listening_pids() {
  lsof -tiTCP:"${1}" -sTCP:LISTEN 2>/dev/null | sort -u || true
}

kill_port() {
  local port="$1"
  local pids
  pids="$(list_listening_pids "${port}")"
  [[ -z "${pids}" ]] && return 0

  echo "==> Port ${port} is occupied. PID(s): $(echo "${pids}" | tr '\n' ' ')"
  echo "==> Stopping..."
  while IFS= read -r pid; do
    [[ -z "${pid}" ]] && continue
    kill "${pid}" 2>/dev/null || true
  done <<< "${pids}"

  for _ in $(seq 1 20); do
    [[ -z "$(list_listening_pids "${port}")" ]] && return 0
    sleep 0.25
  done

  pids="$(list_listening_pids "${port}")"
  if [[ -n "${pids}" ]]; then
    echo "==> Force killing remaining PID(s)..."
    while IFS= read -r pid; do
      [[ -z "${pid}" ]] && continue
      kill -9 "${pid}" 2>/dev/null || true
    done <<< "${pids}"
    sleep 1
  fi

  if [[ -n "$(list_listening_pids "${port}")" ]]; then
    echo "Failed to free port ${port}." >&2; exit 1
  fi
}

cleanup() {
  echo ""
  echo "==> Shutting down..."
  [[ -n "${MAINNET_PID:-}" ]] && kill "${MAINNET_PID}" 2>/dev/null || true
  [[ -n "${FRONTEND_PID:-}" ]] && kill "${FRONTEND_PID}" 2>/dev/null || true
  wait 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# ── Validate ────────────────────────────────────────────────────
if [[ ! -f "${MAINNET_PROJECT}" ]]; then
  echo "Mainnet project not found: ${MAINNET_PROJECT}" >&2; exit 1
fi

# ── Kill existing processes ─────────────────────────────────────
kill_port "${API_PORT}"
echo "==> Port ${API_PORT} is free."

# ── Banner ──────────────────────────────────────────────────────
# Use 127.0.0.1 to match NyxID's registered redirect_uri
API_URL="http://127.0.0.1:${API_PORT}"
FRONTEND_URL="http://127.0.0.1:${FRONTEND_PORT}"

echo ""
echo "╔═══════════════════════════════════════════════════════════╗"
echo "║                      aevatar app                          ║"
echo "╠═══════════════════════════════════════════════════════════╣"
printf "║  %-57s ║\n" "API:      ${API_URL}"
printf "║  %-57s ║\n" "Frontend: ${FRONTEND_URL}"
printf "║  %-57s ║\n" "Press Ctrl+C to stop"
echo "╚═══════════════════════════════════════════════════════════╝"
echo ""

# ── Start Mainnet Host API (background) ─────────────────────────
echo "==> Starting Mainnet Host API..."
dotnet run \
  --project "${MAINNET_PROJECT}" \
  -c "${CONFIGURATION}" \
  --urls "${API_URL}" &
MAINNET_PID=$!

# ── Start frontend dev server (background) ──────────────────────
if [[ -d "${FRONTEND_DIR}" ]] && [[ -f "${FRONTEND_DIR}/package.json" ]]; then
  echo "==> Installing frontend dependencies..."
  (cd "${FRONTEND_DIR}" && pnpm install --frozen-lockfile 2>&1 | tail -3)

  echo "==> Starting frontend dev server..."
  (
    cd "${FRONTEND_DIR}"
    export AEVATAR_API_TARGET="${API_URL}"
    export AEVATAR_STUDIO_API_TARGET="${API_URL}"
    export AEVATAR_CONSOLE_FRONTEND_PORT="${FRONTEND_PORT}"
    pnpm start:dev 2>&1 | sed 's/^/[frontend] /'
  ) &
  FRONTEND_PID=$!
else
  echo "==> Frontend not found at ${FRONTEND_DIR}, skipping."
fi

# ── Open browser (wait for frontend to be ready) ────────────────
if [[ "${NO_BROWSER}" != "true" ]]; then
  (
    echo "==> Waiting for frontend to compile..."
    for _ in $(seq 1 120); do
      if curl -sf -o /dev/null "${FRONTEND_URL}/"; then
        echo "==> Opening browser: ${FRONTEND_URL}"
        if [[ "$(uname)" == "Darwin" ]]; then
          open "${FRONTEND_URL}" 2>/dev/null
        elif command -v xdg-open >/dev/null 2>&1; then
          xdg-open "${FRONTEND_URL}" 2>/dev/null
        fi
        exit 0
      fi
      sleep 1
    done
    echo "==> Frontend did not start within 120s. Open manually: ${FRONTEND_URL}"
  ) &
fi

# ── Wait for either to exit ─────────────────────────────────────
wait "${MAINNET_PID}"
