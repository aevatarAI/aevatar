#!/usr/bin/env bash
# .claude/skills/codex-refactor-loop/scripts/comment-monitor.sh
#
# 独立运行的 comment monitor:
# - 自己跑 gh api 查 design issue / PR 新评论
# - 检测到 team-member 新评论 → 自己加 👀 react(不等 controller)
# - 同时 emit `new-team-comment: <issue> <author> <comment-id>` 到 stdout
#   (controller 通过 Monitor tool 包它,把 stdout 行变成 task-notification)
# - 跑 forever,除非外部 kill
#
# 用法(controller 通过 Monitor tool 包):
#   Monitor(persistent: true, command: ".claude/skills/codex-refactor-loop/scripts/comment-monitor.sh")
#
# state 文件:.refactor-loop/comment-monitor-state.json
# Schema(2026-05-30 重写,降 graphql 用量):
#   {
#     "targets": [123, 456],            # cached open issue/PR numbers
#     "last_targets_refresh": 1780150000,
#     "issue_last_check": {"123": "2026-05-30T15:00:00Z"},
#     "comments_seen": {"<id>": "seen"}
#   }
# 优化:
#   - INTERVAL 30s → 90s(每小时 tick 120 → 40)
#   - targets list 用 REST(`gh api repos/.../issues?labels=...`)+ 缓存 5 min,不再每 tick graphql list
#   - 每 issue/PR 用 REST `comments?since=<last_check>` 增量拉(走 REST 配额,免 graphql)
#   - graphql /h: ~4800 → ~40(list 调用降到 12/h)

set -u
REPO="aevatarAI/aevatar"
STATE_FILE="${STATE_FILE:-/Users/auric/aevatar/.refactor-loop/comment-monitor-state.json}"
INTERVAL="${INTERVAL:-90}"
TARGETS_REFRESH_INTERVAL="${TARGETS_REFRESH_INTERVAL:-300}"

mkdir -p "$(dirname "$STATE_FILE")"
[ -f "$STATE_FILE" ] || echo '{"targets":[],"last_targets_refresh":0,"issue_last_check":{},"comments_seen":{}}' > "$STATE_FILE"

# Migrate old schema (flat comment_id → "seen") to new schema
if jq -e '.comments_seen' "$STATE_FILE" > /dev/null 2>&1; then
  : # already new schema
else
  tmp=$(mktemp)
  jq '{targets: [], last_targets_refresh: 0, issue_last_check: {}, comments_seen: .}' "$STATE_FILE" > "$tmp" && mv "$tmp" "$STATE_FILE"
fi

# Maintainer whitelist (handles + git author names) per SKILL.md
is_team_member() {
  case "$1" in
    loning|Loning|eanzhao|louis4li|louis.li|jason|jason-aelf|AbigailDeng|potter|potter-sun) return 0 ;;
    *) return 1 ;;
  esac
}

# Skip controller / writer-codex own posts. body first line check.
# 包括:
# - "## 🤖" (codex artifact)
# - "## 📊" (controller status banner per SKILL.md status-banner)
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
  # 主判定:sentinel ⟦AI:AUTO-LOOP⟧ 在 body 任意位置 → AI post(per SKILL "AI 内容标识符")
  case "$2" in
    *"⟦AI:AUTO-LOOP⟧"*) return 0 ;;
    *"Generated with Claude Code"*) return 0 ;;
  esac
  # Legacy emoji-prefix(过渡期老评论无 sentinel)— 任何 ## + emoji + ... 第一行
  case "$1" in
    "## 🤖"*|"## 📊"*|"## 📢"*|"## 📎"*|"## ✅"*|"## 🆘"*|"## 🎉"*|"## 🔄"*|"## ⏸️"*|"## 🔍"*|"## 🛠️"*|"## 🚀"*|"## 👀"*|"## 🔧"*|"## ⚙️"*|"## Phase "*|"## Studio "*|"## Workflow "*|"## iter"*) return 0 ;;
    *) return 1 ;;
  esac
}

seen() {
  jq -e --arg id "$1" '.comments_seen | has($id)' "$STATE_FILE" > /dev/null 2>&1
}

mark_seen() {
  local id="$1" tmp
  tmp=$(mktemp)
  jq --arg id "$id" '.comments_seen += {($id): "seen"}' "$STATE_FILE" > "$tmp" && mv "$tmp" "$STATE_FILE"
}

set_issue_last_check() {
  local n="$1" iso="$2" tmp
  tmp=$(mktemp)
  jq --arg n "$n" --arg iso "$iso" '.issue_last_check[$n] = $iso' "$STATE_FILE" > "$tmp" && mv "$tmp" "$STATE_FILE"
}

get_issue_last_check() {
  jq -r --arg n "$1" '.issue_last_check[$n] // ""' "$STATE_FILE"
}

refresh_targets() {
  # 用 REST list issues+PRs(issue endpoint 同时返回 issue 与 pull request),按 label 过滤
  # 不再用 `gh issue list / pr list`(graphql)
  local design_issues auto_loop_items now
  design_issues=$(gh api "repos/$REPO/issues?state=open&labels=refactor-design-needed&per_page=100" --jq '.[].number' 2>/dev/null)
  auto_loop_items=$(gh api "repos/$REPO/issues?state=open&labels=auto-loop&per_page=100" --jq '.[].number' 2>/dev/null)
  local merged=$(printf '%s\n%s\n' "$design_issues" "$auto_loop_items" | grep -v '^$' | sort -un)
  local json_list=$(printf '%s\n' "$merged" | jq -R 'tonumber? // empty' | jq -s '.')
  now=$(date -u +%s)
  local tmp=$(mktemp)
  jq --argjson list "$json_list" --argjson now "$now" '.targets = $list | .last_targets_refresh = $now' "$STATE_FILE" > "$tmp" && mv "$tmp" "$STATE_FILE"
}

while true; do
  # 判断是否需要 refresh targets list
  now=$(date -u +%s)
  last_refresh=$(jq -r '.last_targets_refresh // 0' "$STATE_FILE")
  if (( now - last_refresh >= TARGETS_REFRESH_INTERVAL )); then
    refresh_targets
  fi

  # 读 cached targets
  targets=$(jq -r '.targets[]?' "$STATE_FILE")

  for n in $targets; do
    # REST since 增量拉(GitHub REST 默认 ISO8601;为空则全拉)
    since=$(get_issue_last_check "$n")
    [ -z "$since" ] && since=$(date -u -v-1H +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d "1 hour ago" +%Y-%m-%dT%H:%M:%SZ)
    # 记录此 tick 开始时间作为下次 since(避免 race)
    tick_start_iso=$(date -u +%Y-%m-%dT%H:%M:%SZ)

    comments=$(gh api "repos/$REPO/issues/$n/comments?since=$since&per_page=100" --jq '.[] | {id, author: .user.login, body, created_at}' 2>/dev/null)
    set_issue_last_check "$n" "$tick_start_iso"
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
        # 通知 controller 缩 wakeup:append 到 pending events file
        # controller per-wakeup 第一步读此文件,有新 entry → 下次 wakeup 缩到 600s
        echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) new-team-comment $n $author $id" \
          >> /Users/auric/aevatar/.refactor-loop/.controller-pending-events.log
        # 立刻 post status 卡片让 maintainer 看到 daemon 已识别 + controller 在路上
        # (per Auric 2026-05-20 "加了'看到'表情后, codex 启动也并没有更新状态卡片")
        body_excerpt=$(echo "$body" | head -1 | head -c 80)
        tmp_banner=$(mktemp)
        cat > "$tmp_banner" <<EOF
## 📊 状态 — 已收到 maintainer 评论(daemon 识别)

| 维度 | 值 |
|---|---|
| 触发评论 | id=$id author=$author |
| 评论摘要 | $body_excerpt |
| daemon 反应 | 👀 eyes react 已加 |
| 下一步 | controller 下次 wakeup(≤25 min)读 daemon log → 派 fresh codex round(maintainer-reply-resets-the-round)→ 更新本卡片 |
| **是否需要人介入** | ❌ 否(自动响应中) |

🤖 comment-monitor.sh daemon

⟦AI:AUTO-LOOP⟧
EOF
        post_out=$(gh issue comment "$n" --repo "$REPO" --body-file "$tmp_banner" 2>&1)
        if [ $? -eq 0 ]; then
          echo "daemon-banner-posted: $n $id $(echo "$post_out" | grep -oE 'https://[^ ]+' | head -1)"
        else
          post_out=$(gh pr comment "$n" --repo "$REPO" --body-file "$tmp_banner" 2>&1)
          [ $? -eq 0 ] && echo "daemon-banner-posted: $n $id $(echo "$post_out" | grep -oE 'https://[^ ]+' | head -1)" \
            || echo "daemon-banner-FAILED: $n $id $(echo "$post_out" | head -1)"
        fi
        rm -f "$tmp_banner"
      else
        echo "new-team-comment: $n $author $id eyes-react-FAILED: $(echo "$react_out" | head -1)"
      fi
      mark_seen "$id"
    done < <(echo "$comments" | jq -c '.')
  done

  sleep "$INTERVAL"
done
