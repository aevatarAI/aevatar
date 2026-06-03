#!/usr/bin/env bash
# spawn-codex.sh — standardized `codex exec` wrapper for the codex-refactor-loop skill.
#
# Pattern (intended use): invoke via Bash with run_in_background: true so the harness
# tracks completion and wakes the controller on exit.
#
# Usage:
#   spawn-codex.sh --cd <dir> --prompt <prompt-file> --log <log-file> --timeout <seconds>
#                  [--model <model>] [--add-dir <dir>] [--execution-id <id>]
#                  [--prompt-text "..."] [--dry-run]
#
# Required flags: --cd, --prompt OR --prompt-text, --log, --timeout.
#
# File-based contract (mandatory per maintainer 2026-05-19 "提示词直接写到一个临时文件就可以,
# 输出也输出到一个临时文件, 方便debug"):
#   - Prompt is read from --prompt <file>, OR --prompt-text "..." writes a /tmp temp file.
#   - Output is written to --log <file>. Caller chooses path (typically .refactor-loop/logs/).
#   - At accepted start, this wrapper prints
#     `ACCEPTED: execution_id=<id> ack_stage=accepted prompt=<path> log=<path> timeout=<seconds>` to stdout,
#     and also keeps legacy SPAWN/DONE stderr banners for existing readers.
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
EXECUTION_ID=""
ADD_DIRS=()
DRY_RUN=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --cd)          CD="$2"; shift 2;;
    --prompt)      PROMPT="$2"; shift 2;;
    --prompt-text) PROMPT_TEXT="$2"; shift 2;;
    --log)         LOG="$2"; shift 2;;
    --timeout)     TIMEOUT="$2"; shift 2;;
    --model)       MODEL="$2"; shift 2;;
    --execution-id) EXECUTION_ID="$2"; shift 2;;
    --add-dir)     ADD_DIRS+=("$2"); shift 2;;
    --dry-run)     DRY_RUN=1; shift;;
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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SHARED_PROMPT="$SCRIPT_DIR/../prompts/_shared.md"
RENDERED_PROMPT="$PROMPT"

if [[ ! -f "$SHARED_PROMPT" ]]; then
  echo "shared prompt not found: $SHARED_PROMPT" >&2
  exit 2
fi

# Refactor (#1148): Old pattern: prompt inline repeated hard rules. New principle: shared _shared.md prepend via spawn-codex.
if ! head -5 "$PROMPT" | grep -q '^# Shared hard rules$'; then
  RENDERED_PROMPT=$(mktemp /tmp/codex-prompt-rendered.XXXXXXXX)
  {
    cat "$SHARED_PROMPT"
    printf '\n---\n\n'
    cat "$PROMPT"
  } > "$RENDERED_PROMPT"
fi

# Refactor (#1159): Old pattern: rendered prompts could contain unresolved envsubst placeholders
# (cluster '', audit-iter-.md, dollar-brace-VAR, double-brace-name), leaking blank context to codex.
# New principle: reject at render time so codex never runs with blank cluster context.
UNRESOLVED_PROMPT_PATTERN='cluster[[:space:]]*('"''"'|``)|audit-iter-(MISSING-NUM)?\.md|\$\{[A-Z_]+\}|\{\{[A-Za-z_][A-Za-z0-9_]*\}\}'
if grep -Eq "$UNRESOLVED_PROMPT_PATTERN" "$RENDERED_PROMPT"; then
  echo "rendered prompt contains unresolved or blank placeholders: $RENDERED_PROMPT" >&2
  grep -En "$UNRESOLVED_PROMPT_PATTERN" "$RENDERED_PROMPT" >&2 || true
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

LOG_DIR_ABS="$(cd "$(dirname "$LOG")" && pwd)"
if [[ "$(basename "$LOG_DIR_ABS")" = "logs" ]]; then
  STATE_DIR="$(cd "$LOG_DIR_ABS/.." && pwd)"
else
  STATE_DIR="$LOG_DIR_ABS"
fi
MARKER_DIR="$STATE_DIR/markers"
BASE="$(basename "$LOG" .log)"
# Default execution_id to log basename for backward compatibility with legacy
# marker readers + tests that expect markers/<base>.{running,done}.json.
# Callers may override with --execution-id for distinct lineage tracking.
if [[ -z "$EXECUTION_ID" ]]; then
  EXECUTION_ID="$BASE"
fi
RUNNING_MARKER="$MARKER_DIR/$EXECUTION_ID.running.json"
DONE_MARKER="$MARKER_DIR/$EXECUTION_ID.done.json"

if (( DRY_RUN == 1 )); then
  echo "ACCEPTED: execution_id=$EXECUTION_ID ack_stage=accepted prompt=$RENDERED_PROMPT log=$LOG timeout=$TIMEOUT"
  echo "SPAWN: prompt=$RENDERED_PROMPT log=$LOG cd=$CD timeout=${TIMEOUT}s${MODEL:+ model=$MODEL} execution_id=$EXECUTION_ID dry-run=1" >&2
  head -5 "$RENDERED_PROMPT"
  exit 0
fi

mkdir -p "$MARKER_DIR"
STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
RUNNING_TMP="$MARKER_DIR/.$EXECUTION_ID.running.json.$$"
# Refactor (iter158/cluster-1172-markers-event-design):
# Old: spawn-codex 只写 log,reporter 必须 tail log 末尾 grep EXIT marker 判断 finished;
#      reporter 每 600s 扫 LOG_DIR/*.log 全部(~970 files),97% 浪费 CPU + IO。
# New: spawn-codex 启动时写 .refactor-loop/markers/<execution_id>.running.json,EXIT 后
#      atomic rename → <execution_id>.done.json(含 verdict from tail grep + exit_code + finished_at)。
#      reporter 只扫 markers/(O(in-flight count)),不再 tail log。
# Refactor (issue1369/first-slice):
#   Old pattern: daemon detached codex via nohup/Popen, bypassing harness tracking.
#   New principle: daemon writes pending event; controller dispatches via spawn-codex.sh; harness tracked + task-notification on exit.
jq -n \
  --arg execution_id "$EXECUTION_ID" \
  --arg ack_stage "accepted" \
  --arg base "$BASE" \
  --arg prompt "$RENDERED_PROMPT" \
  --arg log_path "$LOG" \
  --arg started_at "$STARTED_AT" \
  --argjson timeout "$TIMEOUT" \
  '{execution_id: $execution_id, ack_stage: $ack_stage, prompt: $prompt, log: $log_path, timeout: $timeout, started_at: $started_at, base: $base, log_path: $log_path, state: "running"}' \
  > "$RUNNING_TMP"
mv "$RUNNING_TMP" "$RUNNING_MARKER"
rm -f "$DONE_MARKER"

# Debug banner — caller / `tail` sees exact paths immediately (per maintainer 2026-05-19).
# Both prompt + log are real files on disk; debug by `cat <prompt-path>` and `cat <log-path>`.
echo "ACCEPTED: execution_id=$EXECUTION_ID ack_stage=accepted prompt=$RENDERED_PROMPT log=$LOG timeout=$TIMEOUT"
echo "SPAWN: prompt=$RENDERED_PROMPT source_prompt=$PROMPT log=$LOG cd=$CD timeout=${TIMEOUT}s${MODEL:+ model=$MODEL} execution_id=$EXECUTION_ID" >&2

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
run_codex_with_timeout() {
  if command -v timeout >/dev/null 2>&1; then
    timeout "$TIMEOUT" codex "${ARGS[@]}" < "$RENDERED_PROMPT" > "$LOG" 2>&1
    return $?
  fi
  if command -v gtimeout >/dev/null 2>&1; then
    gtimeout "$TIMEOUT" codex "${ARGS[@]}" < "$RENDERED_PROMPT" > "$LOG" 2>&1
    return $?
  fi

  local timeout_flag
  timeout_flag="$(mktemp /tmp/spawn-codex-timeout.XXXXXXXX)"
  rm -f "$timeout_flag"

  codex "${ARGS[@]}" < "$RENDERED_PROMPT" > "$LOG" 2>&1 &
  local codex_pid=$!
  (
    sleep "$TIMEOUT"
    if kill -0 "$codex_pid" >/dev/null 2>&1; then
      : > "$timeout_flag"
      kill -TERM "$codex_pid" >/dev/null 2>&1 || true
      sleep 5
      kill -KILL "$codex_pid" >/dev/null 2>&1 || true
    fi
  ) &
  local watchdog_pid=$!

  wait "$codex_pid"
  local codex_exit=$?
  kill "$watchdog_pid" >/dev/null 2>&1 || true
  wait "$watchdog_pid" >/dev/null 2>&1 || true

  if [[ -f "$timeout_flag" ]]; then
    rm -f "$timeout_flag"
    return 124
  fi

  rm -f "$timeout_flag"
  return "$codex_exit"
}

set +e
run_codex_with_timeout
EXIT=$?
set -e

{
  echo "EXIT=$EXIT"
  echo "DONE_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
} >> "$LOG"

FINISHED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
VERDICT="$(tail -5 "$LOG" | grep -oE "^[A-Z_]+_DONE[:].*$|^REVIEW_DONE.*$|^META_JUDGE_DONE.*$|^META_RESOLVED.*$|^AUDIT_DONE.*$|^TRIAGE_DONE.*$|^FIX_DONE.*$" | tail -1 || true)"
DONE_TMP="$MARKER_DIR/.$EXECUTION_ID.done.json.$$"
jq -n \
  --arg execution_id "$EXECUTION_ID" \
  --arg ack_stage "accepted" \
  --arg base "$BASE" \
  --arg prompt "$RENDERED_PROMPT" \
  --arg log_path "$LOG" \
  --arg started_at "$STARTED_AT" \
  --arg done_at "$FINISHED_AT" \
  --arg finished_at "$FINISHED_AT" \
  --argjson timeout "$TIMEOUT" \
  --argjson exit_code "$EXIT" \
  --arg verdict "$VERDICT" \
  '{execution_id: $execution_id, ack_stage: $ack_stage, prompt: $prompt, log: $log_path, timeout: $timeout, started_at: $started_at, done_at: $done_at, exit_code: $exit_code, base: $base, log_path: $log_path, finished_at: $finished_at, verdict: $verdict, state: "done"}' \
  > "$DONE_TMP"
mv "$DONE_TMP" "$RUNNING_MARKER"
mv "$RUNNING_MARKER" "$DONE_MARKER"

echo "DONE: log=$LOG exit=$EXIT prompt=$RENDERED_PROMPT source_prompt=$PROMPT execution_id=$EXECUTION_ID" >&2

exit "$EXIT"
