#!/usr/bin/env bash
# peek.sh — controller wakeup 快速 sweep
#
# per Auric 2026-05-21 "主动发现问题" + "codex 可以执行得很好,为什么你做不到":
# controller 每 wakeup 第一动作应该用这个 script 一眼看全状态,
# 避免人工 grep / parse 出错(pr1_num empty bug 那种)。
#
# 输出:
#   1. 活跃 codex 数 + 每个的 log 名(harness-tracked vs detached 分别标)
#   2. 完成 markers + 推荐下一步路由(per skill route table)
#   3. 每个 open auto-loop PR 的 CI + reviewer 状态
#   4. monitor zero_streak 最大值(过去 10 tick)
#
# Usage: bash tools/refactor-loop/peek.sh
#
# ⟦AI:AUTO-LOOP⟧

set -e
cd /Users/auric/aevatar
git fetch origin --quiet 2>/dev/null

echo "═══════════════ peek $(date -u +%H:%M:%SZ) ═══════════════"

# 1. Active codex
n=$(ps -ef | grep -E "timeout (3600|5400) codex" | grep -v grep | wc -l | tr -d ' ')
echo ""
echo "▍活跃 codex: ${n}"
if [ "$n" -gt 0 ]; then
  ps -ef | grep -E "timeout (3600|5400) codex" | grep -v grep | \
    sed -E 's/.*--log [^ ]*\/([^ ]+)\.log.*/  • \1/' | sort
fi

# 2. Recently finished markers (last 60 min) + routing hint
echo ""
echo "▍最近 60 min 完成 codex(marker → 推荐下一步):"
find .refactor-loop/logs -name "*.log" -mmin -60 -type f 2>/dev/null | while read f; do
  base=$(basename "$f" .log)
  # Skip in-progress (no EXIT line)
  exit_line=$(grep "^EXIT=" "$f" 2>/dev/null | tail -1)
  [ -z "$exit_line" ] && continue
  marker=$(grep -hE "^(AUDIT_DONE|AUDIT_INCOMPLETE|IMPLEMENT_DONE|IMPLEMENT_BLOCKED|FIX_DONE|FIX_BLOCKED|REVIEW_DONE|SOLVER_DONE|META_JUDGE_DONE|META_RESOLVED|TEST_ADD_DONE)" "$f" 2>/dev/null | tail -1 | head -c 100)
  [ -z "$marker" ] && continue
  # Routing hint
  hint=""
  case "$marker" in
    IMPLEMENT_DONE:*:ok*)    hint="→ commit/push + open PR + 3 reviewer r1" ;;
    IMPLEMENT_DONE:*:partial|IMPLEMENT_DONE:*:blocked) hint="→ inspect + re-prompt or escalate" ;;
    IMPLEMENT_BLOCKED:*)     hint="→ inspect blocker + meta-reflect" ;;
    FIX_DONE:*)              hint="→ commit/push + 3 reviewer r+1" ;;
    FIX_BLOCKED:*)           hint="→ meta-reflect" ;;
    REVIEW_DONE:*:approve)   hint="→ wait other 2 reviewers,then merge if all approve / mixed: ≥2 approve + 0 reject = merge" ;;
    REVIEW_DONE:*:comment)   hint="→ advisory; wait other reviewers" ;;
    REVIEW_DONE:*:reject)    hint="→ wait other 2 reviewers,then fix r+1" ;;
    SOLVER_DONE:*)           hint="→ wait other 2 solvers,then meta-judge" ;;
    META_JUDGE_DONE:consensus:*) hint="→ implement codex (worktree + spawn)" ;;
    META_JUDGE_DONE:converge:*)  hint="→ re-spawn 3 solver with convergence question" ;;
    META_JUDGE_DONE:escalate:philosophy:*) hint="→ label escalate-human + ASCII problem banner" ;;
    META_JUDGE_DONE:escalate:*)  hint="→ reflector codex" ;;
    META_RESOLVED:retry-fix:*)   hint="→ implement codex(or fix r+1 if PR exists)" ;;
    META_RESOLVED:re-design:*)   hint="→ close PR + Phase 9 fresh round" ;;
    META_RESOLVED:re-cluster:*)  hint="→ close PR + audit re-split" ;;
    META_RESOLVED:drop:*)        hint="→ close PR + close issue wontfix" ;;
    META_RESOLVED:escalate-human:*) hint="→ label 🆘 + push notify" ;;
    AUDIT_DONE:*)            hint="→ 验证 cluster evidence + 开 design issues + 派 implement" ;;
    AUDIT_INCOMPLETE:*)      hint="→ re-dispatch audit with missing pieces" ;;
    TEST_ADD_DONE:*)         hint="→ commit/push 等 CI" ;;
  esac
  echo "  • ${base}: ${marker}"
  [ -n "$hint" ] && echo "    ${hint}"
done

# 3. Open auto-loop PRs + state
echo ""
echo "▍Open auto-loop PRs:"
gh pr list --label "auto-loop" --state open --json number,title --jq '.[]' | while IFS= read -r line; do
  num=$(echo "$line" | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['number'])" 2>/dev/null)
  title=$(echo "$line" | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['title'][:60])" 2>/dev/null)
  [ -z "$num" ] && continue
  fail=$(gh pr checks "$num" --json bucket --jq '[.[] | select(.bucket=="fail") | 1] | length' 2>/dev/null)
  pending=$(gh pr checks "$num" --json bucket --jq '[.[] | select(.bucket=="pending") | 1] | length' 2>/dev/null)
  pass=$(gh pr checks "$num" --json bucket --jq '[.[] | select(.bucket=="pass") | 1] | length' 2>/dev/null)
  state=$(gh pr view "$num" --json mergeStateStatus --jq '.mergeStateStatus' 2>/dev/null)
  echo "  • PR #${num} [${state}] CI: fail=${fail} pending=${pending} pass=${pass} — ${title}"
done

# 4. Monitor recent zero_streak max
echo ""
echo "▍Monitor zero_streak (过去 10 tick):"
tail -10 .refactor-loop/logs/concurrency-monitor.log 2>/dev/null | \
  grep -oE "zero_streak=[0-9]+" | sort -t= -k2 -rn | head -1 | sed 's/^/  最大: /'
zero_now=$(tail -1 .refactor-loop/logs/concurrency-monitor.log 2>/dev/null | grep -oE "zero_streak=[0-9]+" | head -1)
[ -n "$zero_now" ] && echo "  当前: ${zero_now}"

# 5. Open auto-loop issues + label state
echo ""
echo "▍Open auto-loop issues:"
gh issue list --label "auto-loop" --state open --json number,title,labels --jq '.[] | "  • #\(.number) labels=[\(.labels | map(.name) | map(select(. | startswith("🔍") or startswith("🛠") or startswith("⚙") or startswith("⏸") or startswith("🆘") or startswith("👤") or startswith("🤖"))) | join(", "))] — \(.title | .[0:55])"' 2>/dev/null

echo ""
echo "═══════════════════════════════════════════════════"
