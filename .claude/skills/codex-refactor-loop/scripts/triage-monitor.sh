#!/usr/bin/env bash
# triage-monitor.sh — daemon 60s 周期扫 auto-loop-triage label,emit event 给 controller
#
# per Auric 2026-05-23 "外部 issues, 非系统主动提的, 能否接入流程"
#
# 设计:
# - 60s 周期 gh issue list --label "auto-loop-triage" --state open
# - 对每个未处理的 issue:
#   - 写 event 到 .refactor-loop/.controller-pending-events.log:
#     `<ISO8601> new-triage-issue <issue> <author>`
#   - state 存 .refactor-loop/triage-monitor-state.json(seen issue id)
# - 不自己派 codex(controller 责任)
# - 启动: nohup bash .claude/skills/codex-refactor-loop/scripts/triage-monitor.sh >> .refactor-loop/logs/triage-monitor.log 2>&1 & disown
#
# ⟦AI:AUTO-LOOP⟧

set -u

REPO_ROOT="${REPO_ROOT:-/Users/auric/aevatar}"
INTERVAL="${INTERVAL:-60}"
STATE_FILE="$REPO_ROOT/.refactor-loop/triage-monitor-state.json"
PENDING_LOG="$REPO_ROOT/.refactor-loop/.controller-pending-events.log"

mkdir -p "$REPO_ROOT/.refactor-loop"
[ -f "$STATE_FILE" ] || echo "{}" > "$STATE_FILE"

log() {
  echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $*"
}

log "triage-monitor started: interval=${INTERVAL}s"

while true; do
  # Query open issues with auto-loop-triage label
  issues=$(gh issue list --label "auto-loop-triage" --state open --json number,author --jq '.[] | "\(.number) \(.author.login)"' 2>/dev/null)
  if [ -z "$issues" ]; then
    sleep "$INTERVAL"
    continue
  fi

  while read -r issue author; do
    [ -z "$issue" ] && continue
    seen=$(jq -r --arg n "$issue" '.[$n] // "no"' "$STATE_FILE" 2>/dev/null)
    if [ "$seen" = "no" ]; then
      ts=$(date -u +%Y-%m-%dT%H:%M:%SZ)
      # mark seen FIRST(防 spawn 失败后无限重派)
      tmp=$(mktemp)
      jq --arg n "$issue" --arg ts "$ts" '. + {($n): $ts}' "$STATE_FILE" > "$tmp" && mv "$tmp" "$STATE_FILE"
      # Materialize triage codex prompt — use envsubst since template has ${ISSUE_NUMBER}
      # placeholders. Previous sed approach only replaced literal #560 and left
      # ${ISSUE_NUMBER} unresolved → validate_prompt rejected → spawn-codex exited
      # silently → daemon marked seen but nothing actually ran.
      # Per Auric 2026-05-29 "daemon 是在运行么? daemon没问题么?": bug discovered when
      # 22 issues sat labeled but no TRIAGE_DONE markers appeared.
      prompt_file="$REPO_ROOT/.refactor-loop/prompts/triage-issue-${issue}.md"
      ISSUE_NUMBER="$issue" AUTHOR="$author" envsubst < "$REPO_ROOT/.claude/skills/codex-refactor-loop/prompts/triage-external-issue.md" > "$prompt_file" 2>/dev/null
      if grep -qE '\$\{ISSUE_NUMBER\}|\$\{AUTHOR\}' "$prompt_file"; then
        log "FATAL: prompt for issue #$issue still has unresolved placeholders — skipping spawn"
        continue
      fi
      # Spawn triage codex(nohup disown — daemon 自己派,不需 harness 跟踪;codex 自己 update GitHub)
      log_file="$REPO_ROOT/.refactor-loop/logs/triage-issue-${issue}.log"
      ISSUE_NUMBER="$issue" nohup bash "$REPO_ROOT/.claude/skills/codex-refactor-loop/scripts/spawn-codex.sh" \
        --cd "$REPO_ROOT" \
        --prompt "$prompt_file" \
        --log "$log_file" \
        --timeout 5400 >> "$REPO_ROOT/.refactor-loop/logs/triage-monitor.log" 2>&1 &
      disown
      log "spawned: triage codex for issue #$issue (author=$author)"
    fi
  done <<< "$issues"

  sleep "$INTERVAL"
done
