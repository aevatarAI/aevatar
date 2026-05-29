#!/usr/bin/env bash
# Read-only visible-loop worker log summarizer.
#
# Completion is intentionally strict: a log is finished only when its last
# five lines contain an exact EXIT=0 footer. Prompt templates and echoed marker
# examples are ignored, and worker markers are reported only after completion.

set -euo pipefail

usage() {
  echo "usage: visible-loop-log-summary.sh <log-path> [log-path...]" >&2
}

tail_exit_line() {
  local file="$1"
  tail -5 "$file" 2>/dev/null | grep -E "^EXIT=[0-9]+$" | tail -1 || true
}

tail_has_exit_zero() {
  local file="$1"
  tail -5 "$file" 2>/dev/null | grep -q "^EXIT=0$"
}

tail_done_at() {
  local file="$1"
  tail -5 "$file" 2>/dev/null | grep -E "^DONE_AT=" | tail -1 | cut -d= -f2- || true
}

normalize_marker_line() {
  local line="$1"
  line="${line#"${line%%[![:space:]]*}"}"
  line="${line#+}"
  line="${line#"${line%%[![:space:]]*}"}"
  printf '%s\n' "$line"
}

is_real_visible_worker_marker() {
  local line="$1"
  local prefix
  local role
  local status
  local reason
  local extra

  case "$line" in
    *"<"*|*">"*|*"EXIT=\$?"*|*"\$?"*) return 1 ;;
  esac

  [[ "$line" == VISIBLE_WORKER_DONE:* ]] || return 1

  IFS=: read -r prefix role status reason extra <<< "$line"
  [[ "$prefix" == "VISIBLE_WORKER_DONE" ]] || return 1
  [[ -n "${role:-}" && -n "${status:-}" && -n "${reason:-}" && -z "${extra:-}" ]] || return 1
  [[ "$status" == "ok" || "$status" == "blocked" ]] || return 1
  [[ "$role" =~ ^[A-Za-z0-9._-]+$ ]] || return 1
  [[ "$reason" =~ ^[A-Za-z0-9._/-]+$ ]] || return 1

  case "$role" in
    role|Role|ROLE|placeholder|example) return 1 ;;
  esac

  case "$reason" in
    reason|short-reason|SHORT_REASON|placeholder|example|todo|TODO) return 1 ;;
  esac

  return 0
}

latest_visible_worker_marker() {
  local file="$1"
  local line
  local normalized
  local marker=""

  while IFS= read -r line || [[ -n "$line" ]]; do
    normalized="$(normalize_marker_line "$line")"
    if is_real_visible_worker_marker "$normalized"; then
      marker="$normalized"
    fi
  done < "$file"

  printf '%s\n' "$marker"
}

summarize_log() {
  local file="$1"
  local base
  local exit_line
  local exit_code="none"
  local done_at
  local finished="false"
  local marker="none"

  base="$(basename "$file")"

  if [[ ! -f "$file" ]]; then
    echo "$base exists=false finished=false exit=none done_at=none marker=none path=$file"
    return 0
  fi

  exit_line="$(tail_exit_line "$file")"
  if [[ -n "$exit_line" ]]; then
    exit_code="${exit_line#EXIT=}"
  fi

  done_at="$(tail_done_at "$file")"
  if [[ -z "$done_at" ]]; then
    done_at="none"
  fi

  if tail_has_exit_zero "$file"; then
    finished="true"
    marker="$(latest_visible_worker_marker "$file")"
    if [[ -z "$marker" ]]; then
      marker="none"
    fi
  fi

  echo "$base exists=true finished=$finished exit=$exit_code done_at=$done_at marker=$marker path=$file"
}

if [[ $# -eq 0 ]]; then
  usage
  exit 2
fi

for log_path in "$@"; do
  summarize_log "$log_path"
done
