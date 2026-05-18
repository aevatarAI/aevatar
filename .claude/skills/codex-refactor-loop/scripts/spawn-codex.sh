#!/usr/bin/env bash
# spawn-codex.sh — standardized `codex exec` wrapper for the codex-refactor-loop skill.
#
# Pattern (intended use): invoke via Bash with run_in_background: true so the harness
# tracks completion and wakes the controller on exit.
#
# Usage:
#   spawn-codex.sh --cd <dir> --prompt <prompt-file> --log <log-file> --timeout <seconds>
#                  [--model <model>] [--add-dir <dir>]
#
# Required flags: --cd, --prompt, --log, --timeout.

set -euo pipefail

CD=""
PROMPT=""
LOG=""
TIMEOUT=""
MODEL=""
ADD_DIRS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --cd)        CD="$2"; shift 2;;
    --prompt)    PROMPT="$2"; shift 2;;
    --log)       LOG="$2"; shift 2;;
    --timeout)   TIMEOUT="$2"; shift 2;;
    --model)     MODEL="$2"; shift 2;;
    --add-dir)   ADD_DIRS+=("$2"); shift 2;;
    *)           echo "unknown flag: $1" >&2; exit 2;;
  esac
done

for var in CD PROMPT LOG TIMEOUT; do
  if [[ -z "${!var}" ]]; then
    echo "missing required flag --${var,,}" >&2
    exit 2
  fi
done

if [[ ! -f "$PROMPT" ]]; then
  echo "prompt file not found: $PROMPT" >&2
  exit 2
fi

mkdir -p "$(dirname "$LOG")"

# Standard args:
# - --dangerously-bypass-approvals-and-sandbox: required for unattended mode (caller-supplied authorization).
# - --skip-git-repo-check: worktrees count as repos, but defensive.
# - -C: working directory (the worktree for implement/verify; trunk for audit).
ARGS=(
  exec
  --dangerously-bypass-approvals-and-sandbox
  --skip-git-repo-check
  -C "$CD"
)

for d in "${ADD_DIRS[@]}"; do
  ARGS+=(--add-dir "$d")
done

if [[ -n "$MODEL" ]]; then
  ARGS+=(-m "$MODEL")
fi

# Read prompt from stdin via the `-` placeholder so very long prompts don't hit argv limits.
ARGS+=(-)

# Run with a hard wall-clock timeout. The harness watches the process; codex exits naturally
# on completion. Append EXIT/DONE_AT footers so controller can post-mortem from log alone.
set +e
timeout "$TIMEOUT" codex "${ARGS[@]}" < "$PROMPT" > "$LOG" 2>&1
EXIT=$?
set -e

{
  echo "EXIT=$EXIT"
  echo "DONE_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
} >> "$LOG"

exit "$EXIT"
