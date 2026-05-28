#!/bin/bash
# codex-progress-reporter.sh
# Per Auric 2026-05-19 "每 10 分钟更新一次各 codex 进展到 issue/PR".
# 每 600s 扫 .refactor-loop/markers/*.json; 对 running marker 的 log 提取 tail; edit-in-place
# 一条 progress comment 到关联 issue/PR. 首次创建, 后续 update 同一 comment id.
# 不会膨胀评论数. 完成时(state=done)自动删除 progress comment 并清理 marker.
#
# State: .refactor-loop/codex-progress-state.json
#   { "<log-basename>": { "target": "<num>", "kind": "issue|pr", "comment_id": <id>, "last_md5": "<sha>", "finished": false } }
#
# 启动: bash .claude/skills/codex-refactor-loop/scripts/codex-progress-reporter.sh &
# 停止: kill <pid>

set -u  # 不用 -e/pipefail — daemon 必须存活,subshell 偶发 non-zero 不应导致整个 daemon 死

REPO_ROOT="${CODEX_PROGRESS_REPO_ROOT:-$(cd "$(dirname "$0")/../../../.." && pwd)}"
cd "$REPO_ROOT"

INTERVAL="${INTERVAL:-600}"
STATE_DIR=".refactor-loop"
STATE_FILE="$STATE_DIR/codex-progress-state.json"
MARKER_DIR="$STATE_DIR/markers"
PROMPTS_DIR="$STATE_DIR/prompts"

mkdir -p "$STATE_DIR" "$MARKER_DIR"
[ -f "$STATE_FILE" ] || echo "{}" > "$STATE_FILE"

log_msg() { echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $*" >&2; }

# parse log basename → target number (issue or PR)
# 优先从对应 prompt 文件 grep "#NNN"; 次选从 log 文件名 pattern 提取
parse_target() {
  local base=$1
  local n=""
  # review-pr*/fix-pr*/phase9-issue* 等文件名本身带权威 target → 优先文件名
  case "$base" in
    review-pr*) n=$(echo "$base" | sed -nE 's/^review-pr([0-9]+).*/\1/p') ;;
    fix-pr*) n=$(echo "$base" | sed -nE 's/^fix-pr([0-9]+).*/\1/p') ;;
    phase9-issue*) n=$(echo "$base" | sed -nE 's/^phase9-issue([0-9]+).*/\1/p') ;;
  esac
  if [ -z "$n" ]; then
    # implement-/verify- 等 cluster log 没有 PR 号 → 从 prompt body 取最后 #NNN
    # meta-cluster prompts 如 "(关 #711 #713 #715)" → primary 是最后(#715)
    local prompt="$PROMPTS_DIR/$base.md"
    if [ -f "$prompt" ]; then
      n=$(grep -oE "#[0-9]+" "$prompt" 2>/dev/null | tail -1 | tr -d '#' || true)
    fi
  fi
  echo "$n"
}

# return "pr" or "issue"
parse_kind() {
  local n=$1
  if gh pr view "$n" --json number >/dev/null 2>&1; then
    echo "pr"
  else
    echo "issue"
  fi
}

is_finished() {
  [ "$(jq -r '.state // empty' "$1" 2>/dev/null)" = "done" ]
}

is_zombie() {
  [ "$(jq -r '.state // empty' "$1" 2>/dev/null)" = "zombie" ]
}

has_exit_marker() {
  tail -5 "$1" 2>/dev/null | grep -q '^EXIT='
}

is_stale_without_exit() {
  local log=$1 mtime now age
  mtime=$(stat -f %m "$log" 2>/dev/null || stat -c %Y "$log")
  now=$(date +%s)
  age=$(( now - mtime ))
  [ "$age" -gt 1800 ] && ! has_exit_marker "$log"
}

extract_tail() {
  # 取 last 25 行, 跳过 EXIT/DONE_AT marker (不影响)
  tail -30 "$1" | head -25
}

elapsed_sec() {
  local log=$1 mtime now
  mtime=$(stat -f %B "$log" 2>/dev/null || stat -c %Y "$log")
  now=$(date +%s)
  echo $(( now - mtime ))
}

build_body() {
  local base=$1 log=$2 finished=$3
  local elapsed_s
  elapsed_s=$(elapsed_sec "$log")
  local elapsed_min=$(( elapsed_s / 60 ))
  local tail_block
  tail_block=$(extract_tail "$log")
  cat <<EOF
## 📊 codex 进展 $base (⏳ 进行中; 已跑 ${elapsed_min} min)

\`\`\`
$tail_block
\`\`\`

> 自动更新每 10 分钟;edit-in-place 不堆评论;**codex 完成后此 comment 自动删除**(per Auric "完成后删掉就好了 否则太占空间")。
🤖 controller progress reporter
EOF
}

state_get() {
  local key=$1 field=$2
  jq -r --arg k "$key" --arg f "$field" '.[$k][$f] // empty' "$STATE_FILE"
}

state_set() {
  local key=$1 target=$2 kind=$3 cid=$4 md5=$5 finished=$6
  local tmp
  tmp=$(mktemp)
  jq --arg k "$key" --arg t "$target" --arg kd "$kind" --argjson cid "$cid" --arg m "$md5" --argjson fin "$finished" \
    '.[$k] = {target: $t, kind: $kd, comment_id: $cid, last_md5: $m, finished: $fin}' \
    "$STATE_FILE" > "$tmp" && mv "$tmp" "$STATE_FILE"
}

post_or_update() {
  local base=$1 marker=$2 log=$3
  local target kind cid prev_md5 finished
  target=$(state_get "$base" "target")
  if [ -z "$target" ]; then
    target=$(parse_target "$base")
    if [ -z "$target" ]; then
      if is_finished "$marker"; then
        rm -f "$marker"
        log_msg "done processed for $base (marker removed, no target)"
      else
        log_msg "skip $base: no target"
      fi
      return
    fi
    kind=$(parse_kind "$target")
    cid="null"
    prev_md5=""
  else
    kind=$(state_get "$base" "kind")
    cid=$(state_get "$base" "comment_id")
    prev_md5=$(state_get "$base" "last_md5")
  fi
  [ -z "$cid" ] && cid="null"

  if is_finished "$marker"; then
    finished="true"
  elif is_zombie "$marker"; then
    log_msg "skip zombie marker $base"
    return
  else
    finished="false"
  fi

  # 已 finished 且上次已记录 finished=true → skip
  local prev_finished
  prev_finished=$(state_get "$base" "finished")
  if [ "$finished" = "true" ] && [ "$prev_finished" = "true" ]; then
    rm -f "$marker"
    log_msg "done processed for $base (marker removed, already finished)"
    return
  fi

  # 状态从 in-flight → finished: 删 comment + 从 state 移除 (per Auric "完成后删掉就好了, 否则太占空间")
  if [ "$finished" = "true" ] && [ -n "$cid" ] && [ "$cid" != "null" ] && [ "$cid" != "0" ]; then
    if gh api -X DELETE "repos/aevatarAI/aevatar/issues/comments/$cid" >/dev/null 2>&1; then
      log_msg "deleted progress comment for $base (finished, cid=$cid was=$kind #$target)"
    else
      log_msg "FAIL to delete comment $cid for $base (already gone?); marking finished anyway"
    fi
    # mark finished in state so we don't re-create next tick
    state_set "$base" "$target" "$kind" "0" "deleted" "true"
    rm -f "$marker"
    log_msg "done processed for $base (marker removed)"
    return
  fi

  if [ "$finished" = "true" ]; then
    state_set "$base" "$target" "$kind" "0" "done-no-progress-comment" "true"
    rm -f "$marker"
    log_msg "done processed for $base (marker removed, no progress comment)"
    return
  fi

  local body cur_md5
  body=$(build_body "$base" "$log" "$finished")
  cur_md5=$(echo "$body" | md5)

  # 内容没变化且没 finish 切换 → skip
  if [ "$cur_md5" = "$prev_md5" ] && [ "$finished" = "$prev_finished" ]; then
    return
  fi

  local body_file
  body_file="/tmp/codex-progress-$$-$(date +%s%N)-$RANDOM.md"
  echo "$body" > "$body_file"

  if [ "$cid" = "null" ] || [ -z "$cid" ]; then
    # 首次见到。Per Auric 2026-05-20 "GitHub 反映原则" + "实际状态 = GitHub 状态":
    # 短 codex 在 reporter tick 间 spawn+complete,首次见到时已 finished — 之前 silent skip
    # 导致 GitHub 上看不到任何 codex 痕迹(maintainer 不知道有事发生)。
    # 修法:首见 finished log → post 一张简短"✅ 已完成"banner(不删,留下 GitHub 痕迹)。
    # Controller 后续 sweep 处理 marker 时再覆盖 / append 新 banner。
    local url
    # parse_kind fallback:先 try $kind,fail 再 try 另一个(防 #700 issue 被当 pr)
    if [ "$kind" = "pr" ]; then
      url=$(gh pr comment "$target" --body-file "$body_file" 2>/dev/null | tail -1)
      [ -z "$url" ] && {
        url=$(gh issue comment "$target" --body-file "$body_file" 2>/dev/null | tail -1)
        [ -n "$url" ] && kind="issue"
      }
    else
      url=$(gh issue comment "$target" --body-file "$body_file" 2>/dev/null | tail -1)
      [ -z "$url" ] && {
        url=$(gh pr comment "$target" --body-file "$body_file" 2>/dev/null | tail -1)
        [ -n "$url" ] && kind="pr"
      }
    fi
    local new_cid
    new_cid=$(echo "$url" | grep -oE 'issuecomment-[0-9]+' | sed 's/issuecomment-//')
    if [ -n "$new_cid" ]; then
      state_set "$base" "$target" "$kind" "$new_cid" "$cur_md5" "$finished"
      log_msg "created progress comment for $base → $kind #$target (cid=$new_cid)"
    else
      log_msg "FAIL to create comment for $base → $kind #$target"
    fi
  else
    # edit 现有 comment
    if gh api -X PATCH "repos/aevatarAI/aevatar/issues/comments/$cid" -F body=@"$body_file" >/dev/null 2>&1; then
      state_set "$base" "$target" "$kind" "$cid" "$cur_md5" "$finished"
      log_msg "edited progress comment for $base (cid=$cid, finished=$finished)"
    else
      log_msg "FAIL to edit comment $cid for $base; will retry next tick"
    fi
  fi

  rm -f "$body_file"
}

run_tick() {
  log_msg "tick"
  # Refactor (iter158/cluster-1172-markers-event-design):
  # Old: for log in "$LOG_DIR"/*.log; tail -5 to detect EXIT。
  # New: for marker in .refactor-loop/markers/*.json;读 marker.state 字段判定 finished;
  #      不再 tail log content(spawn-codex 写 marker 是 source of truth)。
  for marker in "$MARKER_DIR"/*.json; do
    [ -f "$marker" ] || continue
    base=$(jq -r '.base // empty' "$marker" 2>/dev/null)
    state=$(jq -r '.state // empty' "$marker" 2>/dev/null)
    log=$(jq -r '.log_path // empty' "$marker" 2>/dev/null)
    if [ -z "$base" ] || [ -z "$state" ] || [ -z "$log" ]; then
      log_msg "skip invalid marker $marker"
      continue
    fi
    if [ ! -f "$log" ] && [ "$state" != "done" ]; then
      log_msg "skip $base: log not found: $log"
      continue
    fi
    if [ "$state" = "running" ] && is_stale_without_exit "$log"; then
      log_msg "skip zombie marker $base: stale log without EXIT"
      continue
    fi
    # 跳过 audit log(audit 完成后才能进 phase 2,不挂 issue);可以选 post 到 dashboard
    case "$base" in
      audit-iter-*|remote-ci-*)
        if [ "$state" = "done" ]; then
          rm -f "$marker"
          log_msg "done processed for $base (marker removed, no progress target)"
        fi
        continue
        ;;
    esac
    case "$state" in
      running|done|zombie) post_or_update "$base" "$marker" "$log" ;;
      *) log_msg "skip $base: unknown marker state=$state" ;;
    esac
  done
}

# 主 loop
while true; do
  run_tick
  if [ "${CODEX_PROGRESS_REPORTER_RUN_ONCE:-0}" = "1" ]; then
    break
  fi
  sleep "$INTERVAL"
done
