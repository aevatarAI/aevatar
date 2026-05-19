#!/usr/bin/env bash
# tools/refactor-loop/comment-monitor.sh
#
# 独立运行的 comment monitor:
# - 自己跑 gh api 查 design issue / PR 新评论
# - 检测到 team-member 新评论 → 自己加 👀 react(不等 controller)
# - 同时 emit `new-team-comment: <issue> <author> <comment-id>` 到 stdout
#   (controller 通过 Monitor tool 包它,把 stdout 行变成 task-notification)
# - 跑 forever,除非外部 kill
#
# 用法(controller 通过 Monitor tool 包):
#   Monitor(persistent: true, command: "tools/refactor-loop/comment-monitor.sh")
#
# state 文件:.refactor-loop/comment-monitor-state.json (JSON map: comment_id → "seen")

set -u
REPO="aevatarAI/aevatar"
STATE_FILE="${STATE_FILE:-/Users/auric/aevatar/.refactor-loop/comment-monitor-state.json}"
INTERVAL="${INTERVAL:-30}"

mkdir -p "$(dirname "$STATE_FILE")"
[ -f "$STATE_FILE" ] || echo '{}' > "$STATE_FILE"

# Maintainer whitelist (handles + git author names) per SKILL.md
is_team_member() {
  case "$1" in
    loning|Loning|eanzhao|louis4li|louis.li|jason|jason-aelf|AbigailDeng|potter|potter-sun) return 0 ;;
    *) return 1 ;;
  esac
}

# Skip controller / writer-codex own posts. body first line check.
# 包括:
# - "## 🤖" (controller status marker)
# - "## 📢 cc" (cc 原作者)
# - "## 📎" (attachment / raw)
# - "## ✅" (consensus reached / merged)
# - "## 🎉" (celebration)
# - "## 🔄" (rebase / round dispatched)
# - "## Phase " (writer-codex 标题如 "## Phase 9 r2 已收敛..." / "## Phase 8 ...")
# - "## Studio " / "## Workflow " / "## iter1" (writer-codex 通常用 cluster 主题做标题)
# - "Generated with Claude Code" 后缀
# - 任何 body 内含 "POSTED:phase" 标记的(writer-codex 自身 marker 不会出现在 body,但若误传则 skip)
is_controller_post() {
  case "$1" in
    "## 🤖"*|"## 📢 cc"*|"## 📎"*|"## ✅"*|"## 🎉"*|"## 🔄"*|"## Phase "*|"## Studio "*|"## Workflow "*|"## iter"*) return 0 ;;
    *) ;;
  esac
  # 兜底:body 任意位置含 "Generated with Claude Code" 也 skip
  case "$2" in
    *"Generated with Claude Code"*) return 0 ;;
    *) return 1 ;;
  esac
}

seen() {
  jq -e --arg id "$1" 'has($id)' "$STATE_FILE" > /dev/null 2>&1
}

mark_seen() {
  local id="$1" tmp
  tmp=$(mktemp)
  jq --arg id "$id" '. + {($id): "seen"}' "$STATE_FILE" > "$tmp" && mv "$tmp" "$STATE_FILE"
}

while true; do
  # Auto-discover targets: open issues with refactor-design-needed label + open PRs with auto-loop label
  targets=$(
    {
      gh issue list --repo "$REPO" --state open --label "refactor-design-needed" --json number -q '.[].number' 2>/dev/null
      gh pr list --repo "$REPO" --state open --label "auto-loop" --json number -q '.[].number' 2>/dev/null
    } | sort -u
  )

  for n in $targets; do
    # Try issue then pr
    comments=$(gh api "repos/$REPO/issues/$n/comments" --jq '.[] | {id, author: .user.login, body, created_at}' 2>/dev/null)
    [ -z "$comments" ] && continue

    while IFS= read -r raw; do
      id=$(jq -r '.id' <<<"$raw")
      author=$(jq -r '.author' <<<"$raw")
      body=$(jq -r '.body' <<<"$raw")
      created=$(jq -r '.created_at' <<<"$raw")
      [ -z "$id" ] && continue

      if seen "$id"; then continue; fi

      first_line=$(echo "$body" | head -1)
      if is_controller_post "$first_line" "$body"; then
        mark_seen "$id"
        continue
      fi

      if ! is_team_member "$author"; then
        # Not a team member; mark seen so we don't keep checking,
        # but log a one-line event for controller to decide (e.g. PushNotification)
        mark_seen "$id"
        echo "new-outsider-comment: $n $author $id (skipped reply per security gate)"
        continue
      fi

      # Team member new comment → 立刻 eyes react
      react_out=$(gh api "repos/$REPO/issues/comments/$id/reactions" -X POST -f content=eyes 2>&1)
      react_ok=$?
      if [ $react_ok -eq 0 ]; then
        echo "new-team-comment: $n $author $id eyes-reacted-at=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
      else
        echo "new-team-comment: $n $author $id eyes-react-FAILED: $(echo "$react_out" | head -1)"
      fi
      mark_seen "$id"
    done < <(echo "$comments" | jq -c '.')
  done

  sleep "$INTERVAL"
done
