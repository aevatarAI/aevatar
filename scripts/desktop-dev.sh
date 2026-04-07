#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

FRONTEND_DIR="${REPO_ROOT}/tools/Aevatar.Tools.Cli/Frontend"
DESKTOP_DIR="${REPO_ROOT}/tools/Aevatar.Tools.Cli/Desktop"
BOOT_SCRIPT="${REPO_ROOT}/src/Aevatar.Mainnet.Host.Api/boot.sh"

API_PORT="${AEVATAR_DESKTOP_API_PORT:-5080}"
API_URL="${AEVATAR_API_URL:-http://localhost:${API_PORT}}"
VITE_PORT="${AEVATAR_DESKTOP_VITE_PORT:-5174}"
SKIP_INSTALL="${AEVATAR_DESKTOP_SKIP_INSTALL:-0}"
MODE="dev"
NO_BACKEND=0

usage() {
  cat <<'EOF'
Usage:
  ./scripts/desktop-dev.sh [options]

Description:
  Build and launch the Aevatar Desktop (Electron) application.

  By default, starts the local .NET backend, Vite dev server, and Electron.
  Use --no-backend to skip the backend (e.g. when it's already running).
  Use --api-url to connect to a custom or remote backend.

  Press Ctrl-C to stop all processes.

Options:
  --prod               Production mode: build Frontend first, no Vite dev server
  --api-url URL        Backend API base URL. Default: http://localhost:5080
  --api-port PORT      Local backend port. Default: 5080
  --vite-port PORT     Vite dev server port (dev mode only). Default: 5174
  --no-backend         Skip starting the local .NET backend
  --skip-install       Skip npm install even if node_modules is missing
  --install            Force npm install before starting
  -h, --help           Show this help

Environment:
  AEVATAR_API_URL                 Backend API URL (same as --api-url)
  AEVATAR_DESKTOP_API_PORT        Local backend port (same as --api-port)
  AEVATAR_DESKTOP_VITE_PORT       Vite port (same as --vite-port)
  AEVATAR_DESKTOP_SKIP_INSTALL    Set to 1 to skip install
EOF
}

FORCE_INSTALL=0

while [[ $# -gt 0 ]]; do
  case "${1}" in
    --prod)
      MODE="prod"; shift ;;
    --api-url)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      API_URL="${2}"; NO_BACKEND=1; shift 2 ;;
    --api-url=*)
      API_URL="${1#--api-url=}"; NO_BACKEND=1; shift ;;
    --api-port)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      API_PORT="${2}"; API_URL="http://localhost:${API_PORT}"; shift 2 ;;
    --api-port=*)
      API_PORT="${1#--api-port=}"; API_URL="http://localhost:${API_PORT}"; shift ;;
    --vite-port)
      [[ $# -lt 2 ]] && { echo "Missing value for ${1}" >&2; exit 1; }
      VITE_PORT="${2}"; shift 2 ;;
    --vite-port=*)
      VITE_PORT="${1#--vite-port=}"; shift ;;
    --no-backend)
      NO_BACKEND=1; shift ;;
    --skip-install)
      SKIP_INSTALL=1; shift ;;
    --install)
      FORCE_INSTALL=1; shift ;;
    -h|--help)
      usage; exit 0 ;;
    *)
      echo "Unknown option: ${1}" >&2; usage; exit 1 ;;
  esac
done

# ── Dependency check ──

need_install() {
  local dir="$1"
  [[ "${FORCE_INSTALL}" == "1" ]] && return 0
  [[ "${SKIP_INSTALL}" == "1" ]] && return 1
  [[ ! -d "${dir}/node_modules" ]] && return 0
  return 1
}

if need_install "${FRONTEND_DIR}"; then
  echo "==> Installing Frontend dependencies..."
  (cd "${FRONTEND_DIR}" && npm install)
fi

if need_install "${DESKTOP_DIR}"; then
  echo "==> Installing Desktop dependencies..."
  (cd "${DESKTOP_DIR}" && npm install)
fi

# ── Kill stale processes on the Vite port ──

if [[ "${MODE}" == "dev" ]]; then
  stale_pids="$(lsof -tiTCP:"${VITE_PORT}" -sTCP:LISTEN 2>/dev/null || true)"
  if [[ -n "${stale_pids}" ]]; then
    echo "==> Killing stale process(es) on port ${VITE_PORT}..."
    echo "${stale_pids}" | xargs kill 2>/dev/null || true
    sleep 0.5
  fi
fi

# ── Start local .NET backend ──

if [[ "${NO_BACKEND}" == "0" ]]; then
  if [[ ! -f "${BOOT_SCRIPT}" ]]; then
    echo "Backend boot script not found: ${BOOT_SCRIPT}" >&2
    echo "Use --no-backend or --api-url to skip local backend." >&2
    exit 1
  fi
  echo "==> Starting local backend on port ${API_PORT}..."
  AEVATAR_APP_PORT="${API_PORT}" bash "${BOOT_SCRIPT}" --port "${API_PORT}"

  # Wait for backend to be ready
  echo "==> Waiting for backend (localhost:${API_PORT})..."
  for _ in $(seq 1 40); do
    if curl -fsS "http://localhost:${API_PORT}/api/health" >/dev/null 2>&1; then
      break
    fi
    sleep 0.5
  done
  if ! curl -fsS "http://localhost:${API_PORT}/api/health" >/dev/null 2>&1; then
    echo "Backend failed to start. Check ${REPO_ROOT}/src/Aevatar.Mainnet.Host.Api/boot.log" >&2
    exit 1
  fi
  echo "==> Backend ready"
fi

# ── Compile Desktop TypeScript (both modes need this) ──

echo "==> Compiling Desktop main process..."
(cd "${DESKTOP_DIR}" && npx tsc)

# ── Cleanup on exit ──

PIDS=()
cleanup() {
  echo ""
  echo "==> Shutting down..."
  if [[ ${#PIDS[@]} -gt 0 ]]; then
    for pid in "${PIDS[@]}"; do
      kill "${pid}" 2>/dev/null || true
    done
  fi
  wait 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# ── Mode: prod ──

if [[ "${MODE}" == "prod" ]]; then
  echo "==> Building Frontend (production)..."
  (cd "${FRONTEND_DIR}" && ELECTRON_BUILD=1 npx vite build)

  echo "==> Launching Electron (prod, API: ${API_URL})..."
  (cd "${DESKTOP_DIR}" && AEVATAR_API_URL="${API_URL}" npx electron dist/main.js) &
  ELECTRON_PID=$!
  PIDS+=("${ELECTRON_PID}")

  echo "==> Desktop running (prod mode)"
  echo "    API:      ${API_URL}"
  echo "    Electron: ${ELECTRON_PID}"
  echo ""
  echo "    Press Ctrl-C to stop."

  wait "${ELECTRON_PID}" 2>/dev/null || true
  exit 0
fi

# ── Mode: dev ──

echo "==> Starting Vite dev server on port ${VITE_PORT} (proxy → ${API_URL})..."
(cd "${FRONTEND_DIR}" && AEVATAR_API_URL="${API_URL}" npx vite --port "${VITE_PORT}" --strictPort) &
VITE_PID=$!
PIDS+=("${VITE_PID}")

echo "==> Waiting for Vite (localhost:${VITE_PORT})..."
for _ in $(seq 1 60); do
  if curl -fsS "http://localhost:${VITE_PORT}" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "${VITE_PID}" 2>/dev/null; then
    echo "Vite dev server failed to start." >&2
    exit 1
  fi
  sleep 0.5
done

if ! curl -fsS "http://localhost:${VITE_PORT}" >/dev/null 2>&1; then
  echo "Vite dev server did not become ready in time." >&2
  exit 1
fi

echo "==> Vite ready"

echo "==> Launching Electron (dev, API: ${API_URL})..."
(cd "${DESKTOP_DIR}" && AEVATAR_API_URL="${API_URL}" VITE_DEV_URL="http://localhost:${VITE_PORT}" npx electron dist/main.js --dev) &
ELECTRON_PID=$!
PIDS+=("${ELECTRON_PID}")

echo "==> Desktop running (dev mode)"
echo "    Backend:  ${API_URL}"
echo "    Vite:     http://localhost:${VITE_PORT}"
echo "    Vite PID: ${VITE_PID}"
echo "    Electron: ${ELECTRON_PID}"
echo ""
echo "    Press Ctrl-C to stop."

wait "${ELECTRON_PID}" 2>/dev/null || true
