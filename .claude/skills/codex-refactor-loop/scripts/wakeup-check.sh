#!/usr/bin/env bash
# wakeup-check.sh — controller wakeup 第一动作:
# 1) 检查 daemon liveness（死了报警）
# 2) 报告活跃 codex floor
# 3) 按 SKILL 优先级表 Step 0..G 顺序扫描可 actionable 任务
# 4) 无任务时推荐审计 backfill
#
# 输出格式稳定，AI 直接 parse "ACTION:" 行决定下一步派 codex。
# 不修改 GitHub / 不派 codex，仅诊断 + 推荐。
#
# Per Auric 2026-05-29: "增加一个脚本，每次唤醒的时候机械的调用该脚本，检查
# 各 daemon，同时按照顺序获取任务，无任务时推荐跑审计任务给 AI."

set -u
cd "$(git rev-parse --show-toplevel 2>/dev/null || echo /Users/auric/aevatar)"

LOGDIR=".refactor-loop/logs"
NOW_EPOCH=$(date -u +%s)

epoch_of() {
    local ts="$1"
    date -j -u -f "%Y-%m-%dT%H:%M:%SZ" "$ts" +%s 2>/dev/null \
        || date -u -d "$ts" +%s 2>/dev/null \
        || echo 0
}

# ============================================================================
echo "=== DAEMON HEALTH ==="
for d in concurrency_monitor.py comment-monitor.sh codex-progress-reporter.sh dev_sync_daemon.py triage-monitor.sh; do
    c=$(ps -ef | grep "$d" | grep -v grep | wc -l | tr -d ' ')
    if [ "$c" -ge 1 ]; then
        echo "$d: $c OK"
    else
        echo "$d: 0 DEAD — ACTION: restart with 'nohup .../$d >> $LOGDIR/${d%.*}.log 2>&1 & disown'"
    fi
done

# ============================================================================
echo ""
echo "=== FLOOR ==="
# Each codex spawn creates 3 ps rows: `timeout NNNN codex exec` (parent wrapper) +
# `node .../codex exec` (Node host) + native `codex` binary. Counting all three
# inflates ACTIVE 3x. Filter to the wrapper line only — it carries the --log path
# used for categorization too.
CODEX_LINES=$(ps -ef | grep -E "timeout (3600|5400) codex exec" | grep -v grep)
ACTIVE=$(echo "$CODEX_LINES" | grep -cE "timeout (3600|5400) codex exec" | tr -d ' \n')
[ -z "$CODEX_LINES" ] && ACTIVE=0
AUDIT=$(echo "$CODEX_LINES" | grep -c "audit-iter" | tr -d ' \n')
IMPL=$(echo "$CODEX_LINES" | grep -c "implement" | tr -d ' \n')
FIX=$(echo "$CODEX_LINES" | grep -c "fix-pr" | tr -d ' \n')
REVIEW=$(echo "$CODEX_LINES" | grep -c "review-pr" | tr -d ' \n')
PHASE9=$(echo "$CODEX_LINES" | grep -cE "(phase9|solver|meta-judge)" | tr -d ' \n')
ACTIVE=${ACTIVE:-0}; AUDIT=${AUDIT:-0}; IMPL=${IMPL:-0}; FIX=${FIX:-0}; REVIEW=${REVIEW:-0}; PHASE9=${PHASE9:-0}
OTHER=$(( ACTIVE - AUDIT - IMPL - FIX - REVIEW - PHASE9 ))
echo "active=$ACTIVE (audit=$AUDIT impl=$IMPL fix=$FIX review=$REVIEW phase9=$PHASE9 other=$OTHER)"
if [ -f .refactor-loop/.auto-stopped ]; then
    echo "STOP MARKER: .refactor-loop/.auto-stopped exists — no dispatch allowed"
fi

# ============================================================================
echo ""
echo "=== STEP 0: MILESTONE ==="
M_FOUND=0
M_LABELS=$(gh label list --search "milestone:" --json name --jq '.[].name' 2>/dev/null | grep -E "^milestone:" | sort)
if [ -z "$M_LABELS" ]; then
    echo "(no milestone labels defined)"
else
    for ml in $(echo "$M_LABELS" | grep "^milestone:p0:") $(echo "$M_LABELS" | grep -v "^milestone:p0:"); do
        ISSUES=$(gh issue list --state open --label "$ml" --json number --jq '.[].number' 2>/dev/null | tr '\n' ' ')
        if [ -n "$ISSUES" ]; then
            echo "$ml: issues=$ISSUES — ACTION: prioritize these over Step A-G"
            M_FOUND=1
        fi
    done
    [ "$M_FOUND" -eq 0 ] && echo "(milestone labels exist but no open issue stamped)"
fi

# ============================================================================
echo ""
echo "=== STEP A: STALE IMPLEMENTING (IMPLEMENT_DONE marker but no PR, OR never dispatched) ==="
A_COUNT=0
while IFS= read -r n; do
    [ -z "$n" ] && continue
    log=$(ls -t "$LOGDIR"/implement-*"${n}"*.log 2>/dev/null | head -1)
    [ -z "$log" ] && log="$LOGDIR/implement-issue${n}.log"
    # 检查关联 PR 是否存在
    pr=$(gh pr list --state open --search "#${n}" --json number --jq '.[0].number' 2>/dev/null)
    if [ ! -f "$log" ]; then
        # 从未派过 implement codex（log 不存在）— 若也无 PR,需 controller 派 implement
        if [ -z "$pr" ]; then
            echo "#${n}: NO implement log AND NO open PR — ACTION: dispatch implement codex (label suggests in-flight but never started)"
            A_COUNT=$((A_COUNT+1))
        fi
        continue
    fi
    marker=$(tail -10 "$log" | grep -E "^(IMPLEMENT_DONE|EXIT)" | tail -1)
    age_min=$(( (NOW_EPOCH - $(stat -f %m "$log" 2>/dev/null || echo "$NOW_EPOCH")) / 60 ))
    if [ -z "$pr" ] && echo "$marker" | grep -qE "(IMPLEMENT_DONE.*:ok|EXIT=0)"; then
        echo "#${n}: log=$(basename "$log") age=${age_min}min marker=\"$marker\" — ACTION: controller commit+push+open PR + Phase 8 reviewer × 3"
        A_COUNT=$((A_COUNT+1))
    fi
done < <(gh issue list --state open --label "🛠️ phase:implementing" --json number --jq '.[].number' 2>/dev/null)
[ "$A_COUNT" -eq 0 ] && echo "(none — implementing issues all have open PR or in-flight)"

# ============================================================================
echo ""
echo "=== STEP B: STALE REVIEWING (REVIEW_DONE × 3 or FIX_DONE pending action) ==="
B_COUNT=0
while IFS= read -r pr; do
    [ -z "$pr" ] && continue
    # 找最新 round 的 reviewer log
    last_review_round=$(ls "$LOGDIR"/review-pr${pr}-*-r*.log 2>/dev/null | grep -oE "r[0-9]+" | sort -Vu | tail -1)
    last_fix_round=$(ls "$LOGDIR"/fix-pr${pr}-r*.log 2>/dev/null | grep -oE "r[0-9]+" | sort -Vu | tail -1)
    [ -z "$last_review_round" ] && last_review_round="r0"
    [ -z "$last_fix_round" ] && last_fix_round="r0"

    # 当前 review round 的 verdicts
    if [ "$last_review_round" != "r0" ]; then
        verdicts=$(grep -hE "^REVIEW_DONE:" "$LOGDIR"/review-pr${pr}-*-${last_review_round}.log 2>/dev/null | grep -oE ":(approve|comment|reject)" | sort | uniq -c | tr '\n' ' ')
        n_done=$(grep -lE "^REVIEW_DONE:" "$LOGDIR"/review-pr${pr}-*-${last_review_round}.log 2>/dev/null | wc -l | tr -d ' ')
        if [ "$n_done" -ge 3 ]; then
            n_reject=$(grep -hE "^REVIEW_DONE:.*:reject" "$LOGDIR"/review-pr${pr}-*-${last_review_round}.log 2>/dev/null | wc -l | tr -d ' ')
            n_approve=$(grep -hE "^REVIEW_DONE:.*:approve" "$LOGDIR"/review-pr${pr}-*-${last_review_round}.log 2>/dev/null | wc -l | tr -d ' ')
            if [ "$n_reject" -gt 0 ]; then
                # 看是否已派 fix
                fix_round_num=$(echo "$last_fix_round" | grep -oE '[0-9]+')
                review_round_num=$(echo "$last_review_round" | grep -oE '[0-9]+')
                if [ "$fix_round_num" -lt "$review_round_num" ]; then
                    echo "PR ${pr}: review-${last_review_round} verdicts=[$verdicts] — ACTION: spawn fix codex r$((review_round_num+1)) (3 reject pending)"
                    B_COUNT=$((B_COUNT+1))
                fi
            elif [ "$n_approve" -eq 3 ]; then
                # 全 approve,检查 CI
                ci_pass=$(gh pr checks "$pr" --required --json bucket --jq 'all(.[]; .bucket == "pass")' 2>/dev/null)
                if [ "$ci_pass" = "true" ]; then
                    echo "PR ${pr}: review-${last_review_round} all approve + CI green — ACTION: merge_pr ${pr}"
                    B_COUNT=$((B_COUNT+1))
                fi
            fi
        fi
    fi
done < <(gh pr list --state open --label "auto-loop" --label "👀 phase:reviewing" --json number --jq '.[].number' 2>/dev/null)
[ "$B_COUNT" -eq 0 ] && echo "(none — reviewing PRs all in-flight or no consensus yet)"

# ============================================================================
echo ""
echo "=== STEP C: CI RED PR ==="
C_COUNT=0
while IFS= read -r pr; do
    [ -z "$pr" ] && continue
    failed=$(gh pr checks "$pr" --json bucket --jq '[.[] | select(.bucket=="fail") | 1] | length' 2>/dev/null)
    if [ -n "$failed" ] && [ "$failed" -gt 0 ]; then
        failed_names=$(gh pr checks "$pr" --json name,bucket --jq '[.[] | select(.bucket=="fail") | .name] | join(",")' 2>/dev/null)
        echo "PR ${pr}: failed=${failed} checks=[${failed_names}] — ACTION: pull fail logs + spawn fix codex"
        C_COUNT=$((C_COUNT+1))
    fi
done < <(gh pr list --state open --label "auto-loop" --json number --jq '.[].number' 2>/dev/null)
[ "$C_COUNT" -eq 0 ] && echo "(none — all auto-loop PR CI green or pending)"

# ============================================================================
echo ""
echo "=== STEP D: STUCK 3h+ ISSUES (escalated) ==="
D_COUNT=0
while IFS= read -r n; do
    [ -z "$n" ] && continue
    # 防重派:30 min 内有 reflector log 则跳过
    rfl=$(find "$LOGDIR" -name "meta-reflect-issue${n}*.log" -mmin -30 2>/dev/null | head -1)
    [ -n "$rfl" ] && continue
    last_human=$(gh issue view "$n" --json comments --jq '[.comments[] | select(.body | contains("⟦AI:AUTO-LOOP⟧") | not) | .createdAt][-1] // empty' 2>/dev/null | tr -d '"')
    [ -z "$last_human" ] && continue
    last_epoch=$(epoch_of "$last_human")
    delta_h=$(( (NOW_EPOCH - last_epoch) / 3600 ))
    if [ "$delta_h" -ge 3 ]; then
        echo "#${n}: last human ${delta_h}h ago — ACTION: spawn reflector codex (meta-reflect-issue${n}-rN+1.log)"
        D_COUNT=$((D_COUNT+1))
    fi
done < <((gh issue list --state open --label "🆘 human:卡死-需-rework" --json number --jq '.[].number' 2>/dev/null; \
         gh issue list --state open --label "👤 human:需-maintainer-决策" --json number --jq '.[].number' 2>/dev/null; \
         gh issue list --state open --label "auto-loop-stuck" --json number --jq '.[].number' 2>/dev/null) | sort -u)
[ "$D_COUNT" -eq 0 ] && echo "(none — no stuck issues older than 3h needing reflector)"

# ============================================================================
echo ""
echo "=== STEP E: UNTOUCHED OPEN ISSUE 3h+ ==="
E_COUNT=0
while IFS= read -r line; do
    [ -z "$line" ] && continue
    n=$(echo "$line" | awk '{print $1}')
    u=$(echo "$line" | awk '{print $2}')
    last_epoch=$(epoch_of "$u")
    age_h=$(( (NOW_EPOCH - last_epoch) / 3600 ))
    if [ "$age_h" -ge 3 ]; then
        echo "#${n}: untouched ${age_h}h — ACTION: gh issue edit ${n} --add-label \"auto-loop-triage\""
        E_COUNT=$((E_COUNT+1))
        [ "$E_COUNT" -ge 5 ] && break  # cap per wakeup
    fi
done < <(gh issue list --state open --limit 50 --json number,author,updatedAt,labels --jq '
    .[] | select(
        (.author.login | endswith("[bot]") | not)
        and ([.labels[].name] | (contains(["🎉 phase:merged"]) or contains(["auto-loop-triage"]) or contains(["🔍 phase:design-solving"]) or contains(["🛠️ phase:implementing"]) or contains(["🚀 phase:pr-open"]) or contains(["phase11-not-eligible"]) or contains(["🆘 human:卡死-需-rework"]) or contains(["👤 human:需-maintainer-决策"])) | not)
    ) | "\(.number) \(.updatedAt)"' 2>/dev/null)
[ "$E_COUNT" -eq 0 ] && echo "(none — no untouched issues older than 3h needing triage)"

# ============================================================================
echo ""
echo "=== STEP F: PHASE 9 — pending judge / pending r1 solver ==="
F_COUNT=0
F2_COUNT=0
while IFS= read -r n; do
    [ -z "$n" ] && continue
    # 最新 round 的 solver logs
    latest_round=$(ls "$LOGDIR"/phase9-issue${n}-r*-*.log 2>/dev/null | grep -oE 'r[0-9]+' | sort -Vu | tail -1)
    if [ -z "$latest_round" ]; then
        # NO solver log AT ALL → r1 not dispatched yet
        # per Auric 2026-05-29 "一堆issues没完成, 为什么发新审计?" — design-solving
        # issue without r1 solver = unprocessed backlog, MUST take priority over audit
        echo "#${n}: NO solver yet — ACTION: spawn r1 minimal/structural/delete solvers"
        F2_COUNT=$((F2_COUNT+1))
        continue
    fi
    solver_done=$(grep -lE "EXIT=0" "$LOGDIR"/phase9-issue${n}-${latest_round}-{minimal,structural,delete}.log 2>/dev/null | wc -l | tr -d ' ')
    judge_log="$LOGDIR/phase9-issue${n}-${latest_round}-judge.log"
    if [ "$solver_done" -eq 3 ] && [ ! -f "$judge_log" ]; then
        echo "#${n}: ${latest_round} 3 solvers done, no judge log — ACTION: spawn meta-judge for ${latest_round}"
        F_COUNT=$((F_COUNT+1))
    fi
done < <(gh issue list --state open --label "🔍 phase:design-solving" --json number --jq '.[].number' 2>/dev/null)
if [ "$F_COUNT" -eq 0 ] && [ "$F2_COUNT" -eq 0 ]; then
    echo "(none — no Phase 9 issue waiting for judge or r1 solver dispatch)"
fi
# F2 issues each consume 3 codex slots (minimal/structural/delete)
F_COUNT=$(( F_COUNT + F2_COUNT * 3 ))

# ============================================================================
echo ""
echo "=== STEP G: AUDIT BACKFILL (only when A-F all empty — per Auric 2026-05-29) ==="
G_BUSY=$(( A_COUNT + B_COUNT + C_COUNT + D_COUNT + E_COUNT + F_COUNT ))
LAST_ITER=$(ls .refactor-loop/runs/audit-iter-*.md 2>/dev/null | grep -oE 'iter-[0-9]+' | sort -V | tail -1 | grep -oE '[0-9]+')
LAST_LOG_ITER=$(ls "$LOGDIR"/audit-iter-*.log 2>/dev/null | grep -oE 'iter-[0-9]+' | sort -V | tail -1 | grep -oE '[0-9]+')
NEXT_ITER=$((${LAST_LOG_ITER:-${LAST_ITER:-0}} + 1))
echo "last audit run: iter-${LAST_ITER:-?} (log iter-${LAST_LOG_ITER:-?})"
# Per Auric 2026-05-29 "一堆issues没完成, 为什么发新审计?" — REVERTING the prior
# "always available" decision. Audit IS backfill, not floor-filler. When design-solving
# issues sit without solvers (Step F2) or any actionable A-F work exists, audit is
# WRONG because: (a) new audit produces more design issues that won't be solved
# without floor for solvers; (b) backlog grows without bound. Audit only when truly
# nothing else to do.
if [ "$G_BUSY" -gt 0 ]; then
    echo "Step A-F has $G_BUSY actionable items — DO NOT dispatch audit until cleared"
else
    if [ ! -f "$LOGDIR/audit-iter-${NEXT_ITER}.log" ]; then
        echo "ACTION: spawn audit-iter-${NEXT_ITER} (backfill — A-F all empty)"
    else
        echo "audit-iter-${NEXT_ITER} log already exists — skip"
    fi
fi

# ============================================================================
echo ""
echo "==================================================================="
echo "=== HARD GATE — AI MUST DISPATCH BEFORE END-TURN ================="
echo "==================================================================="
FLOOR_REQUIRED=10
NEEDED=$(( FLOOR_REQUIRED - ACTIVE ))
if [ -f .refactor-loop/.auto-stopped ]; then
    echo "STOP_ACTIVE=1 — dispatch FORBIDDEN (auto-stopped marker exists)"
    echo "AI may end-turn without dispatch."
elif [ "$NEEDED" -le 0 ]; then
    echo "floor_actual=$ACTIVE floor_required=$FLOOR_REQUIRED gap=0"
    echo "FLOOR_OK=1 — AI may end-turn (or ScheduleWakeup) without dispatching new codex."
else
    echo "floor_actual=$ACTIVE floor_required=$FLOOR_REQUIRED gap=$NEEDED"
    echo "MUST_DISPATCH=$NEEDED — AI MUST spawn $NEEDED codex BEFORE ScheduleWakeup / end-turn."
    echo ""
    echo "ACTIONABLE WORK QUEUE (pick top $NEEDED by priority):"
    QUEUE_REMAINING=$NEEDED
    take() {
        # $1 = how many to take from this priority, $2 = label
        local n="$1"; local lbl="$2"
        [ "$QUEUE_REMAINING" -le 0 ] && return
        [ "$n" -le 0 ] && return
        local take_n="$n"
        [ "$take_n" -gt "$QUEUE_REMAINING" ] && take_n="$QUEUE_REMAINING"
        echo "  TAKE $take_n × $lbl"
        QUEUE_REMAINING=$(( QUEUE_REMAINING - take_n ))
    }
    [ "$M_FOUND" = "1" ] && take 1 "P0 milestone-related codex"
    take "$A_COUNT" "P1 controller-action: open PR + 3 reviewer for stale implementing issue"
    take "$B_COUNT" "P2 controller-action: fix/merge for stale reviewing PR"
    take "$C_COUNT" "P3 spawn fix codex for CI-red PR"
    take "$D_COUNT" "P4 spawn reflector for stuck 3h+ issue"
    take "$E_COUNT" "P5 controller-action: gh issue edit --add-label auto-loop-triage (daemon spawns triage codex)"
    take "$F_COUNT" "P6 spawn judge OR r1 solvers for Phase 9 design-solving issue (each F2 = 3 slots)"
    # Per Auric 2026-05-29 "一堆issues没完成, 为什么发新审计?" — audit is backfill,
    # not floor-filler. Only spawn audit when A-F all empty. If gap remains after
    # P0-P6 are exhausted, that means the actionable backlog truly is processed.
    if [ "$G_BUSY" -eq 0 ] && [ "$QUEUE_REMAINING" -gt 0 ]; then
        take "$QUEUE_REMAINING" "P9 spawn audit-iter-${NEXT_ITER}+ (BACKFILL — A-F empty only)"
    fi
    if [ "$QUEUE_REMAINING" -gt 0 ]; then
        echo "  UNFILLED=$QUEUE_REMAINING — A-F has codex-slot work but already in flight; OK to end-turn"
    fi
    echo ""
    echo "PRIORITY RULE (per Auric 2026-05-29 '一堆issues没完成, 为什么发新审计?'): processing existing design-solving / implementing / reviewing / fix backlog ALWAYS comes before new audit. Audit creates more design issues, growing the backlog if solvers are not running."
fi

echo ""
echo "=== DONE ==="
