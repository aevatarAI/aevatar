#!/usr/bin/env bash
# tools/refactor-loop/dev-sync-daemon.sh
#
# 自动 merge origin/dev → auto-refact-dev 的 daemon(per Auric 2026-05-20
# "写一个独立脚本, 自动 merge dev 到 auto-refact-dev 分支. 如果有冲突让脚本
# 调用 codex 解决冲突合并. daemon 运行").
#
# - 每 INTERVAL 秒(默认 600s=10 min)check `git rev-list HEAD..origin/dev`
# - behind > 0 → 尝试 ff-only / no-ff merge
# - 冲突 → spawn-codex.sh 派 codex 自动 resolve
# - 成功 → git push origin auto-refact-dev
# - 写 log: .refactor-loop/logs/dev-sync-daemon.log

set -u

INTERVAL="${INTERVAL:-600}"
REPO_ROOT="${REPO_ROOT:-/Users/auric/aevatar}"
INTEGRATION="${INTEGRATION:-auto-refact-dev}"
REVIEW_BASE="${REVIEW_BASE:-dev}"
SPAWN_CODEX="$REPO_ROOT/.claude/skills/codex-refactor-loop/scripts/spawn-codex.sh"

log() {
  echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $*"
}

is_codex_resolving() {
  # 检查是否已有 in-flight dev-sync-codex(防重复派)
  pgrep -f "dev-sync-codex-" >/dev/null 2>&1
}

dispatch_codex_resolve() {
  local prompt_file log_file timestamp
  timestamp=$(date +%s)
  prompt_file="$REPO_ROOT/.refactor-loop/prompts/dev-sync-conflict-${timestamp}.md"
  log_file="$REPO_ROOT/.refactor-loop/logs/dev-sync-codex-${timestamp}.log"

  cat > "$prompt_file" <<EOF
# 任务:resolve merge conflict on $INTEGRATION ← origin/$REVIEW_BASE sync

## Context

dev-sync-daemon 自动 merge \`origin/$REVIEW_BASE\` → \`$INTEGRATION\` 时遇到冲突。你
需要 resolve 所有冲突 + \`git add\` 已 resolve 的文件 + \`git merge --continue\`。
**你不 push**,daemon 后续 push。

## 任务

1. \`cd $REPO_ROOT\`
2. \`git status\` 看 conflict 文件
3. 读每个冲突文件,理解 dev 改动 vs $INTEGRATION 改动
4. 合并(保留两者实质改动:tests / new files / docs / production code)
5. \`git add <files>\`
6. \`git merge --continue\`(用 default merge message,带 \`Sync $INTEGRATION with $REVIEW_BASE\`)
7. \`dotnet build aevatar.slnx --nologo\` 验证编译(失败修)
8. 完成 marker:\`DEV_SYNC_RESOLVED:<files-resolved>\` 或 \`DEV_SYNC_BLOCKED:<reason>\`

## 硬约束

- ❌ \`git push\` / \`git merge --abort\` / \`git reset --hard\`(daemon 控制)
- ❌ 删任一边的实质改动(test code / production code / docs / proto)
- ❌ 不 commit before \`git merge --continue\`(merge 会自动产生 merge commit)
- ❌ 写新产线代码(只 resolve conflicts + build verify)
- proto 字段冲突(同字段号 不同语义)→ \`DEV_SYNC_BLOCKED:proto-schema-conflict\` 说明

完成时把以下 marker 写到 stdout:
- 成功:\`DEV_SYNC_RESOLVED:<file1>,<file2>,...\`
- 阻塞:\`DEV_SYNC_BLOCKED:<reason>:<short>\`

⟦AI:AUTO-LOOP⟧
EOF

  log "dispatching codex: prompt=$prompt_file log=$log_file"
  nohup "$SPAWN_CODEX" \
    --cd "$REPO_ROOT" \
    --prompt "$prompt_file" \
    --log "$log_file" \
    --timeout 5400 >/dev/null 2>&1 &
  disown
}

cd "$REPO_ROOT" || { log "FATAL cd $REPO_ROOT"; exit 1; }

log "dev-sync-daemon started: interval=${INTERVAL}s repo=$REPO_ROOT $REVIEW_BASE → $INTEGRATION"

while true; do
  cd "$REPO_ROOT" 2>/dev/null || { log "FAIL cd $REPO_ROOT"; sleep "$INTERVAL"; continue; }

  # HEAD 必须在 integration branch
  current=$(git rev-parse --abbrev-ref HEAD 2>/dev/null)
  if [ "$current" != "$INTEGRATION" ]; then
    log "skip tick: HEAD=$current(not $INTEGRATION)"
    sleep "$INTERVAL"
    continue
  fi

  # Working tree dirty → skip(controller 可能在工作 / 上次 merge 未完)
  if ! git diff --quiet 2>/dev/null || ! git diff --cached --quiet 2>/dev/null; then
    # 检查是不是 merge in progress
    if [ -f .git/MERGE_HEAD ]; then
      if is_codex_resolving; then
        log "skip: merge in progress + codex resolving"
      else
        log "WARN: merge in progress but NO codex running — dispatching resolve"
        dispatch_codex_resolve
      fi
    else
      log "skip: working tree dirty (no merge in progress)"
    fi
    sleep "$INTERVAL"
    continue
  fi

  git fetch origin --quiet
  behind=$(git rev-list --count "HEAD..origin/$REVIEW_BASE" 2>/dev/null || echo 0)

  if [ "$behind" -eq 0 ]; then
    log "up-to-date with origin/$REVIEW_BASE"
    sleep "$INTERVAL"
    continue
  fi

  log "behind origin/$REVIEW_BASE by $behind commits, attempting sync"

  # 尝试 ff-only
  ff_out=$(git merge --ff-only "origin/$REVIEW_BASE" 2>&1)
  ff_rc=$?
  if [ $ff_rc -eq 0 ] && echo "$ff_out" | grep -qE "Fast-forward|Already up to date"; then
    log "ff-merged with origin/$REVIEW_BASE (+$behind commits)"
    push_out=$(git push origin "$INTEGRATION" 2>&1)
    push_rc=$?
    if [ $push_rc -eq 0 ]; then
      log "pushed $INTEGRATION → origin"
    else
      log "FAIL push: $(echo "$push_out" | head -1)"
    fi
    sleep "$INTERVAL"
    continue
  fi

  # ff-only 失败 → no-ff merge
  log "ff-only failed, attempting no-ff merge"
  merge_out=$(git merge --no-ff -m "Sync $INTEGRATION with $REVIEW_BASE (auto by dev-sync-daemon)" "origin/$REVIEW_BASE" 2>&1)
  merge_rc=$?

  if [ $merge_rc -eq 0 ]; then
    log "no-ff merge-committed +$behind commits"
    push_out=$(git push origin "$INTEGRATION" 2>&1)
    if [ $? -eq 0 ]; then
      log "pushed $INTEGRATION → origin"
    else
      log "FAIL push after merge: $(echo "$push_out" | head -1)"
    fi
    sleep "$INTERVAL"
    continue
  fi

  # 冲突 → dispatch codex resolve
  if [ -f .git/MERGE_HEAD ]; then
    log "CONFLICT detected (merge in progress)"
    if is_codex_resolving; then
      log "codex already resolving, skip this tick"
    else
      dispatch_codex_resolve
    fi
  else
    log "FAIL merge but no MERGE_HEAD: $(echo "$merge_out" | head -1)"
  fi

  sleep "$INTERVAL"
done
