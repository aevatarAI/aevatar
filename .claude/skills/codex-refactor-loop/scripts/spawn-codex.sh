#!/usr/bin/env bash
# spawn-codex.sh — standardized `codex exec` wrapper for the codex-refactor-loop skill.
#
# Pattern (intended use): invoke via Bash with run_in_background: true so the harness
# tracks completion and wakes the controller on exit.
#
# Usage:
#   spawn-codex.sh --cd <dir> --prompt <prompt-file> --log <log-file> --timeout <seconds>
#                  [--model <model>] [--add-dir <dir>] [--prompt-text "..."]
#
# Required flags: --cd, --prompt OR --prompt-text, --log, --timeout.
#
# File-based contract (mandatory per Auric 2026-05-19 "提示词直接写到一个临时文件就可以,
# 输出也输出到一个临时文件, 方便debug"):
#   - Prompt is read from --prompt <file>, OR --prompt-text "..." writes a /tmp temp file.
#   - Output is written to --log <file>. Caller chooses path (typically .refactor-loop/logs/).
#   - At start, this wrapper prints `SPAWN: prompt=<path> log=<path> cd=<dir> timeout=<s>` to stderr
#     so callers / `tail` see exact paths immediately. At end, prints `DONE: log=<path> exit=<N>`.
#   - Debug recipe: `cat <prompt-path>` to see what codex got; `cat <log-path>` to see what it did.
#
# Forbidden:
#   - Passing prompt content via argv (would hit shell length limit + obscure debug).
#   - Sending codex output to /dev/null or stdout-only (loses debug trail).
#   - Timeout < 3600s (project-wide minimum, see CLAUDE.md "Codex CLI 调用规范").

set -euo pipefail

CD=""
PROMPT=""
PROMPT_TEXT=""
LOG=""
TIMEOUT=""
MODEL=""
ADD_DIRS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --cd)          CD="$2"; shift 2;;
    --prompt)      PROMPT="$2"; shift 2;;
    --prompt-text) PROMPT_TEXT="$2"; shift 2;;
    --log)         LOG="$2"; shift 2;;
    --timeout)     TIMEOUT="$2"; shift 2;;
    --model)       MODEL="$2"; shift 2;;
    --add-dir)     ADD_DIRS+=("$2"); shift 2;;
    *)             echo "unknown flag: $1" >&2; exit 2;;
  esac
done

# If --prompt-text is given, materialize a temp prompt file so codex still reads from a file.
# Caller can find the file via the SPAWN: stderr banner if they want to debug.
if [[ -n "$PROMPT_TEXT" && -z "$PROMPT" ]]; then
  # macOS mktemp only substitutes trailing Xs (BSD); Linux GNU substitutes anywhere.
  # Use trailing-X pattern for cross-platform; codex doesn't care about file extension.
  PROMPT=$(mktemp /tmp/codex-prompt.XXXXXXXX)
  printf '%s\n' "$PROMPT_TEXT" > "$PROMPT"
elif [[ -n "$PROMPT_TEXT" && -n "$PROMPT" ]]; then
  echo "pass either --prompt OR --prompt-text, not both" >&2
  exit 2
fi

for var in CD PROMPT LOG TIMEOUT; do
  if [[ -z "${!var}" ]]; then
    echo "missing required flag --${var,,} (use --prompt <file> or --prompt-text \"...\" for prompt)" >&2
    exit 2
  fi
done

if [[ ! -f "$PROMPT" ]]; then
  echo "prompt file not found: $PROMPT" >&2
  exit 2
fi

# Project-wide minimum codex timeout: 3600s (1 hour). See CLAUDE.md
# "Codex CLI 调用规范". Shorter timeouts cause codex to truncate
# deep-scan / multi-file refactor work and inflate the controller's
# rework rate. Override only by passing --allow-short-timeout (none
# of the built-in phase prompts do).
if (( TIMEOUT < 3600 )); then
  echo "codex timeout ${TIMEOUT}s is below the project-wide 3600s minimum" >&2
  echo "raise --timeout to >= 3600 (see CLAUDE.md Codex CLI 调用规范)" >&2
  exit 2
fi

mkdir -p "$(dirname "$LOG")"

# Debug banner — caller / `tail` sees exact paths immediately (per Auric 2026-05-19).
# Both prompt + log are real files on disk; debug by `cat <prompt-path>` and `cat <log-path>`.
echo "SPAWN: prompt=$PROMPT log=$LOG cd=$CD timeout=${TIMEOUT}s${MODEL:+ model=$MODEL}" >&2

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

for d in "${ADD_DIRS[@]+"${ADD_DIRS[@]}"}"; do
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

echo "DONE: log=$LOG exit=$EXIT prompt=$PROMPT" >&2

exit "$EXIT"
