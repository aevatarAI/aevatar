#!/usr/bin/env bash
# sweep_pending_triage.sh — controller per-wakeup helper to process new-triage-issue events
#
# 用法:controller wakeup 在 sync 后调用
#   bash tools/refactor-loop/sweep_pending_triage.sh
#
# 输出:0 或多行 `DISPATCH_TRIAGE:<issue>:<author>` — controller 据此派 triage codex
# state:.refactor-loop/.triage-events-processed-offset 防重复
#
# ⟦AI:AUTO-LOOP⟧

set -u

REPO_ROOT="${REPO_ROOT:-/Users/auric/aevatar}"
PENDING_LOG="$REPO_ROOT/.refactor-loop/.controller-pending-events.log"
OFFSET_FILE="$REPO_ROOT/.refactor-loop/.triage-events-processed-offset"

[ -f "$PENDING_LOG" ] || exit 0
[ -f "$OFFSET_FILE" ] || echo "0" > "$OFFSET_FILE"

prev_offset=$(cat "$OFFSET_FILE" 2>/dev/null || echo 0)
cur_offset=$(wc -l < "$PENDING_LOG" 2>/dev/null | tr -d ' ')

if [ "$cur_offset" -le "$prev_offset" ]; then
  exit 0
fi

# Get new lines since last offset, filter for new-triage-issue events
sed -n "$((prev_offset+1)),${cur_offset}p" "$PENDING_LOG" | grep "new-triage-issue " | while IFS=' ' read -r ts kind issue author rest; do
  [ -z "$issue" ] && continue
  echo "DISPATCH_TRIAGE:${issue}:${author}"
done

# Update offset
echo "$cur_offset" > "$OFFSET_FILE"
