#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: tools/fkst/supervise.sh [--background] [--restart] [-- <extra supervise args>]

Environment:
  FKST_ENV_FILE       Optional env file. Defaults to $HOME/.config/fkst/aevatar.env.
  FKST_PLATFORM_ROOT  Required unless set in FKST_ENV_FILE. Path to fkst-packages.
  FKST_HOST_ROOT      Optional. Defaults to this repository root.
  FKST_STATE_ROOT     Optional. Defaults to ${XDG_STATE_HOME:-$HOME/.local/state}/fkst/aevatar.
  FKST_DURABLE_ROOT   Optional. Defaults to $FKST_STATE_ROOT/durable.
  FKST_RUNTIME_ROOT   Optional. Defaults to $FKST_STATE_ROOT/runtime.
  FKST_RATE_POOL_ROOT Optional. Defaults to $FKST_STATE_ROOT/rate.
  FKST_LOG_DIR        Optional. Defaults to $FKST_STATE_ROOT/logs.

This script uses FKST host startup so package composition is read from
.fkst/compose/package-roots instead of being duplicated in this script.
USAGE
}

BACKGROUND=0
RESTART=0
EXTRA_ARGS=()
while [ "$#" -gt 0 ]; do
  case "$1" in
    --background)
      BACKGROUND=1
      shift
      ;;
    --restart)
      RESTART=1
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    --)
      shift
      EXTRA_ARGS=("$@")
      break
      ;;
    *)
      EXTRA_ARGS+=("$1")
      shift
      ;;
  esac
done

ENV_FILE="${FKST_ENV_FILE:-$HOME/.config/fkst/aevatar.env}"
if [ -f "$ENV_FILE" ]; then
  set -a
  # shellcheck disable=SC1090
  . "$ENV_FILE"
  set +a
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_ROOT="${FKST_HOST_ROOT:-$(cd "$SCRIPT_DIR/../.." && pwd)}"

if [ -z "${FKST_PLATFORM_ROOT:-}" ]; then
  cat >&2 <<'ERROR'
FKST_PLATFORM_ROOT is required. Set it in the environment or in $HOME/.config/fkst/aevatar.env.
Example:
  FKST_PLATFORM_ROOT=/path/to/fkst-packages
ERROR
  exit 2
fi

STATE_ROOT="${FKST_STATE_ROOT:-${XDG_STATE_HOME:-$HOME/.local/state}/fkst/aevatar}"
export FKST_DURABLE_ROOT="${FKST_DURABLE_ROOT:-$STATE_ROOT/durable}"
export FKST_RUNTIME_ROOT="${FKST_RUNTIME_ROOT:-$STATE_ROOT/runtime}"
export FKST_RATE_POOL_ROOT="${FKST_RATE_POOL_ROOT:-$STATE_ROOT/rate}"
LOG_DIR="${FKST_LOG_DIR:-$STATE_ROOT/logs}"

mkdir -p "$FKST_DURABLE_ROOT" "$FKST_RUNTIME_ROOT" "$FKST_RATE_POOL_ROOT" "$LOG_DIR"

cmd=(
  "$FKST_PLATFORM_ROOT/scripts/run.sh" host
  --host-root "$HOST_ROOT"
  --platform-root "$FKST_PLATFORM_ROOT"
  -- supervise
  --durable-root "$FKST_DURABLE_ROOT"
  --runtime-root "$FKST_RUNTIME_ROOT"
)

if [ "$RESTART" -eq 1 ]; then
  cmd+=(--restart)
fi

if [ "${#EXTRA_ARGS[@]}" -gt 0 ]; then
  cmd+=("${EXTRA_ARGS[@]}")
fi

if [ "$BACKGROUND" -eq 1 ]; then
  log_file="$LOG_DIR/supervise.log"
  nohup "${cmd[@]}" > "$log_file" 2>&1 &
  pid=$!
  printf 'FKST supervise started: pid=%s log=%s\n' "$pid" "$log_file"
  exit 0
fi

exec "${cmd[@]}"
