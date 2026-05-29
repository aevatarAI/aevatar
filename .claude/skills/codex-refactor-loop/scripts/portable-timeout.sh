#!/usr/bin/env bash
# Cross-platform timeout wrapper for codex-refactor-loop process scripts.
#
# Linux hosts keep using GNU timeout when it is available. macOS hosts with
# coreutils use gtimeout. Hosts with neither fall back to a shell watchdog that
# returns 124 after the wall-clock limit, matching GNU timeout's timeout exit.

set -euo pipefail

usage() {
  echo "usage: portable-timeout.sh [--select] <seconds> <command> [args...]" >&2
}

select_timeout_runner() {
  if [[ "${CODEX_LOOP_FORCE_TIMEOUT_FALLBACK:-0}" != "1" ]]; then
    if command -v timeout >/dev/null 2>&1; then
      echo "timeout"
      return 0
    fi
    if command -v gtimeout >/dev/null 2>&1; then
      echo "gtimeout"
      return 0
    fi
  fi

  echo "shell-fallback"
}

run_shell_timeout() {
  local duration="$1"
  shift

  local command_pid=""
  local timer_pid=""
  local exit_code=0
  local stdin_copy=""
  local timeout_marker
  timeout_marker="$(mktemp "${TMPDIR:-/tmp}/codex-loop-timeout.XXXXXXXX")"
  rm -f "$timeout_marker"
  stdin_copy="$(mktemp "${TMPDIR:-/tmp}/codex-loop-stdin.XXXXXXXX")"
  cat > "$stdin_copy"

  "$@" < "$stdin_copy" &
  command_pid=$!

  (
    sleep "$duration"
    printf 'timeout\n' > "$timeout_marker"
    kill -TERM "$command_pid" 2>/dev/null || exit 0
    sleep "${CODEX_LOOP_TIMEOUT_KILL_AFTER:-5}"
    kill -KILL "$command_pid" 2>/dev/null || true
  ) &
  timer_pid=$!

  set +e
  wait "$command_pid" 2>/dev/null
  exit_code=$?
  set -e

  if [[ -f "$timeout_marker" ]]; then
    wait "$timer_pid" 2>/dev/null || true
    rm -f "$timeout_marker"
    rm -f "$stdin_copy"
    return 124
  fi

  kill "$timer_pid" 2>/dev/null || true
  wait "$timer_pid" 2>/dev/null || true
  rm -f "$timeout_marker"
  rm -f "$stdin_copy"
  return "$exit_code"
}

if [[ "${1:-}" == "--select" ]]; then
  select_timeout_runner
  exit 0
fi

if [[ $# -lt 2 ]]; then
  usage
  exit 2
fi

duration="$1"
shift

if [[ ! "$duration" =~ ^[0-9]+$ ]] || (( duration <= 0 )); then
  echo "timeout seconds must be a positive integer: $duration" >&2
  exit 2
fi

runner="$(select_timeout_runner)"
case "$runner" in
  timeout|gtimeout)
    exec "$runner" "$duration" "$@"
    ;;
  shell-fallback)
    run_shell_timeout "$duration" "$@"
    exit $?
    ;;
  *)
    echo "unknown timeout runner: $runner" >&2
    exit 2
    ;;
esac
