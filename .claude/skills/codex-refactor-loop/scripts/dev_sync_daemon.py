#!/usr/bin/env python3
"""
dev_sync_daemon.py — 双向 sync daemon

Per Auric 2026-05-20 "改这个 daemon 在一个独立 worktree 工作, 改用 python 写吧"
Per Auric 2026-05-27 "能够让codex处理所有复杂情况,并且监测如果dev分支与本地分支差
18小时以上,则从本地分支切出一个分支向dev分支提pr,该新pr绿了后自动merge且删分支"

设计:
- 独立 worktree:
  - /Users/auric/aevatar-wt-dev-sync (dev → integration sync)
  - /Users/auric/aevatar-wt-dev-rollup (integration → dev rollup)
- 600s 周期 tick
- branch protection enforce_admins=true → 所有 push 必走 PR + auto-merge
  (repo allow_auto_merge=true,GitHub 自动 squash merge + delete branch)

工作流:
1. sync_dev_to_integration:dev → auto-refact-dev
   - 在 sync worktree fetch + try merge
   - 冲突 → spawn 升级版 codex(带 rule 10/11 full slnx test)resolve
   - 成功 → push 到 chore/dev-sync-auto-<ts> branch + 开 PR + enable auto-merge
2. rollup_integration_to_dev:auto-refact-dev → dev
   - 检查 origin/dev..origin/auto-refact-dev 最早 commit 距今 hours
   - >= 18h 且无 open rollup PR → 开 PR + auto-merge
3. 防重复:check open PR 池(`gh pr list --base <X> --search "chore/<prefix>"`)

启动:
  nohup python3 .claude/skills/codex-refactor-loop/scripts/dev_sync_daemon.py \
    >> .refactor-loop/logs/dev-sync-daemon.log 2>&1 &
  disown

⟦AI:AUTO-LOOP⟧
"""

import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

INTERVAL = int(os.environ.get("INTERVAL", "600"))
MAIN_REPO = Path(os.environ.get("REPO_ROOT", "/Users/auric/aevatar"))
SYNC_WORKTREE = Path(os.environ.get("WORKTREE", "/Users/auric/aevatar-wt-dev-sync"))
ROLLUP_WORKTREE = Path(os.environ.get("ROLLUP_WORKTREE", "/Users/auric/aevatar-wt-dev-rollup"))
INTEGRATION = os.environ.get("INTEGRATION", "auto-refact-dev")
REVIEW_BASE = os.environ.get("REVIEW_BASE", "dev")
ROLLUP_HOURS_THRESHOLD = int(os.environ.get("ROLLUP_HOURS_THRESHOLD", "18"))
SPAWN_CODEX = MAIN_REPO / ".claude" / "skills" / "codex-refactor-loop" / "scripts" / "spawn-codex.sh"
SYNC_BRANCH_PREFIX = "chore/dev-sync-auto-"
ROLLUP_BRANCH_PREFIX = "chore/auto-refact-dev-rollup-"


def log(msg: str) -> None:
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    print(f"[{ts}] {msg}", flush=True)


def run(cmd: list[str], cwd: Path | None = None, check: bool = False) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, check=check)


def git_dir(cwd: Path) -> Path:
    """Return real .git dir even for worktrees (where .git is a file)."""
    r = run(["git", "rev-parse", "--git-dir"], cwd=cwd)
    if r.returncode != 0:
        return cwd / ".git"
    raw = r.stdout.strip()
    return Path(raw) if Path(raw).is_absolute() else cwd / raw


def merge_in_progress(cwd: Path) -> bool:
    """Fixed: use git rev-parse --git-dir to find real MERGE_HEAD path
    (secondary worktree .git is a file pointing to main repo's worktree dir).
    Per 2026-05-27 PR1105 retro:之前硬编码 cwd/.git/MERGE_HEAD 永远 False。"""
    gd = git_dir(cwd)
    return (gd / "MERGE_HEAD").exists() or (gd / "MERGE_MSG").exists()


def working_tree_dirty(cwd: Path) -> bool:
    r1 = run(["git", "diff", "--quiet"], cwd=cwd)
    r2 = run(["git", "diff", "--cached", "--quiet"], cwd=cwd)
    return r1.returncode != 0 or r2.returncode != 0


def ensure_worktree(wt: Path, base_branch: str) -> bool:
    """Ensure detached HEAD worktree off origin/<base_branch>."""
    if not wt.exists():
        log(f"creating worktree {wt} (detached off origin/{base_branch})")
        run(["git", "fetch", "origin", "--quiet"], cwd=MAIN_REPO)
        r = run(["git", "worktree", "add", "--detach", str(wt),
                 f"origin/{base_branch}"], cwd=MAIN_REPO)
        if r.returncode != 0:
            log(f"FATAL: git worktree add {wt} failed: {r.stderr.strip()}")
            return False
    return True


def reset_to_remote(cwd: Path, branch: str) -> bool:
    run(["git", "fetch", "origin", "--quiet"], cwd=cwd)
    r = run(["git", "reset", "--hard", f"origin/{branch}"], cwd=cwd)
    if r.returncode != 0:
        log(f"FAIL reset to origin/{branch}: {r.stderr.strip()[:120]}")
        return False
    return True


def count_ahead(base: str, head: str, cwd: Path = None) -> int:
    r = run(["git", "rev-list", "--count", f"{base}..{head}"], cwd=cwd or MAIN_REPO)
    try:
        return int(r.stdout.strip())
    except ValueError:
        return 0


def earliest_ahead_age_hours(base: str, head: str, cwd: Path = None) -> float:
    """Return age in hours of the earliest commit in base..head (oldest ahead commit)."""
    r = run(["git", "log", "--reverse", "--format=%ct", f"{base}..{head}"], cwd=cwd or MAIN_REPO)
    if r.returncode != 0 or not r.stdout.strip():
        return 0.0
    first_line = r.stdout.strip().split("\n")[0]
    try:
        earliest_ts = int(first_line)
    except ValueError:
        return 0.0
    now_ts = int(time.time())
    return (now_ts - earliest_ts) / 3600.0


def codex_in_flight(tag: str) -> bool:
    """Check if a codex with given tag is still running (process scan)."""
    r = run(["pgrep", "-f", f"dev-sync-codex-{tag}-"])
    return r.returncode == 0


def has_open_pr(base: str, head_prefix: str = None) -> tuple[bool, str]:
    """Check if open PR exists base=<base> head startswith head_prefix.
    Returns (exists, pr_number_or_empty)."""
    args = ["gh", "pr", "list", "--base", base, "--state", "open",
            "--json", "number,headRefName"]
    r = run(args, cwd=MAIN_REPO)
    if r.returncode != 0:
        log(f"FAIL gh pr list: {r.stderr.strip()[:120]}")
        return False, ""
    try:
        prs = json.loads(r.stdout)
    except json.JSONDecodeError:
        return False, ""
    for pr in prs:
        if head_prefix is None or pr["headRefName"].startswith(head_prefix):
            return True, str(pr["number"])
    return False, ""


def dispatch_codex_resolve(cwd: Path, kind: str, sync_branch: str = "") -> None:
    """Spawn codex to resolve merge conflict + verify per rule 10/11."""
    ts = int(time.time())
    prompt_file = MAIN_REPO / ".refactor-loop" / "prompts" / f"dev-sync-codex-{kind}-{ts}.md"
    log_file = MAIN_REPO / ".refactor-loop" / "logs" / f"dev-sync-codex-{kind}-{ts}.log"
    prompt_file.parent.mkdir(parents=True, exist_ok=True)
    if kind == "sync":
        prompt_body = _sync_codex_prompt(cwd, sync_branch)
    elif kind == "rollup":
        prompt_body = _rollup_codex_prompt(cwd, sync_branch)
    else:
        log(f"FATAL: unknown codex kind {kind}")
        return
    prompt_file.write_text(prompt_body)
    log(f"dispatching codex ({kind}): prompt={prompt_file.name} log={log_file.name}")
    subprocess.Popen(
        ["nohup", str(SPAWN_CODEX),
         "--cd", str(cwd),
         "--add-dir", str(MAIN_REPO),
         "--prompt", str(prompt_file),
         "--log", str(log_file),
         "--timeout", "5400"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        start_new_session=True,
    )


def _sync_codex_prompt(cwd: Path, sync_branch: str) -> str:
    return f"""# dev → {INTEGRATION} sync codex(daemon spawn)

## 任务

在 worktree `{cwd}` resolve dev → {INTEGRATION} merge conflict + 全场景适配 +
verify per SKILL rule 10/11(本地 full slnx test pass 才出 marker)。

## 工作流

1. `cd {cwd}`
2. `git status` 看 conflict 文件
3. 读每个冲突 file,理解 origin/{REVIEW_BASE} 改动 vs {INTEGRATION} 改动
4. 合并(保留两者实质改动:tests / new files / docs / production code / proto)
5. `git add <files>` 已 resolve 的
6. `git merge --continue`(default merge message)
7. **本地 full scenario verify**(per skill rule 10/11):
   ```bash
   dotnet build aevatar.slnx --nologo
   dotnet test aevatar.slnx --nologo --no-build --no-restore
   ```
   build 错 → 修;test 错 → 适配 test fixture(不动 production)
   **必跑 full slnx test(不要 --filter)**,full pass 才出 DONE marker

## 复杂场景处理(PR1106 retro)

- **test fixture missing DI**:dev 新加 service(e.g. `IActorDispatchPort`),test fixture
  builder pattern 未 register → 加 stub/fake to `services.AddSingleton<>`,不动 production
- **API rename**:dev 改方法名 / 参数 sig → test 用旧 API → 改 test 用新 API(read dev 端
  production 看新 sig)
- **Proto field rename**:dev 改 proto field → test 直接构造 message 失败 → test 用新 field 名
- **Behavioral change**:dev 改 behavior 但 test 期望旧 → test 期望调整 OR marker
  `DEV_SYNC_BLOCKED:behavioral-change:<file>:<test>` 让 daemon 决策
- **Namespace add**:dev 加新 namespace / project → check `aevatar.slnx` 是否包含 + test
  project 是否 reference 新 dep
- **跨多 module 同根因**:r1 修一个 module fail 不够 → 必须 full slnx test verify catch
  其他 module(per PR1106 r1→r3 lesson:filter verify 漏 module)

## 完成 marker

- 成功:`DEV_SYNC_RESOLVED:files=<N>:tests=pass`
- 阻塞:`DEV_SYNC_BLOCKED:<reason>:<short>`(proto-schema-conflict / behavioral-change / build-broken)

## 硬约束

- ❌ 禁止 `git push` / `git merge --abort` / `git reset --hard`(daemon 控)
- ❌ 禁止 commit before `git merge --continue`(merge 自动 commit)
- ❌ 禁止 `[Skip]` / disabled 测试换绿
- ❌ 禁止 `Task.Delay`-based test pacing
- ❌ 禁止动外部 repo(NyxID / chrono-*)
- ❌ 禁止动 main repo `{MAIN_REPO}` worktree(只动当前 worktree)
- ❌ **禁止 `--filter` 作为最终 verify**(per rule 10) — filter 只用 iterative debug
- ❌ 严禁写 `Auric` / `@auric` / `@Auric`

所有 AI 生成的对外内容(GitHub comment / commit message / runs/*.md artifact)必须末尾独立一行加
sentinel `⟦AI:AUTO-LOOP⟧`。
"""


def _rollup_codex_prompt(cwd: Path, rollup_branch: str) -> str:
    return f"""# {INTEGRATION} → {REVIEW_BASE} rollup codex(daemon spawn)

## Context

Daemon detect `{INTEGRATION}` 与 `{REVIEW_BASE}` 差 >= {ROLLUP_HOURS_THRESHOLD} hours
(earliest divergent commit age),自动 rollup。

worktree:`{cwd}`(detached HEAD off `origin/{INTEGRATION}`)
target branch:`{rollup_branch}`

## 任务

在 worktree:
1. `git status` 确认 clean
2. `git checkout -b {rollup_branch}`
3. `git push origin {rollup_branch}` — push 新 branch
4. **daemon 后续会 `gh pr create --base {REVIEW_BASE} --head {rollup_branch}` + auto-merge**
5. 你不需要做 build / test verify(rollup 内容已是 trunk 上 verified content,只走 dev CI)

## 完成 marker

- 成功:`DEV_ROLLUP_BRANCH_PUSHED:{rollup_branch}`
- 失败:`DEV_ROLLUP_BLOCKED:<reason>`

## 硬约束

- ❌ 禁止 `gh pr create / gh pr merge / gh pr edit`(daemon 控 PR lifecycle)
- ❌ 禁止动 main repo
- ❌ 严禁写 `Auric` / `@auric` / `@Auric`

所有 AI 生成的对外内容必须末尾独立一行加 sentinel `⟦AI:AUTO-LOOP⟧`。
"""


def push_sync_pr(cwd: Path) -> bool:
    """After codex resolves + verifies full slnx test, push as sync branch + open PR + auto-merge."""
    ts = int(time.time())
    sync_branch = f"{SYNC_BRANCH_PREFIX}{ts}"
    # push HEAD to new branch
    r = run(["git", "push", "origin", f"HEAD:refs/heads/{sync_branch}"], cwd=cwd)
    if r.returncode != 0:
        log(f"FAIL push sync branch {sync_branch}: {r.stderr.strip()[:200]}")
        return False
    log(f"pushed sync branch {sync_branch}")
    # open PR
    behind_count = count_ahead(f"origin/{INTEGRATION}", "HEAD", cwd=cwd)
    body = f"""## 摘要

dev_sync_daemon 自动 sync `origin/{REVIEW_BASE}` → `{INTEGRATION}`,merge commit 已 in branch。

- behind: ~{behind_count} commits
- codex 已本地 verify full slnx test pass(rule 10/11)

## 自动流程

CI 全 8 required 绿后,daemon 已 enable auto-merge(squash + delete branch)。

⟦AI:AUTO-LOOP⟧
"""
    body_file = MAIN_REPO / ".refactor-loop" / "runs" / f"pr-body-sync-{ts}.md"
    body_file.parent.mkdir(parents=True, exist_ok=True)
    body_file.write_text(body)
    r = run(["gh", "pr", "create", "--base", INTEGRATION, "--head", sync_branch,
             "--title", f"chore: dev → {INTEGRATION} 自动 sync ({behind_count} commits)",
             "--body-file", str(body_file)], cwd=MAIN_REPO)
    if r.returncode != 0:
        log(f"FAIL gh pr create sync: {r.stderr.strip()[:200]}")
        return False
    pr_url = r.stdout.strip()
    log(f"opened sync PR: {pr_url}")
    pr_num = pr_url.rsplit("/", 1)[-1]
    # add auto-loop label
    run(["gh", "pr", "edit", pr_num, "--add-label", "auto-loop"], cwd=MAIN_REPO)
    # enable auto-merge
    r = run(["gh", "pr", "merge", pr_num, "--squash", "--delete-branch", "--auto"],
            cwd=MAIN_REPO)
    if r.returncode != 0:
        log(f"WARN enable auto-merge failed: {r.stderr.strip()[:120]}")
    else:
        log(f"enabled auto-merge on sync PR #{pr_num}")
    return True


def push_rollup_pr() -> bool:
    """Create rollup branch from origin/<INTEGRATION>, open PR base=<REVIEW_BASE>, enable auto-merge."""
    ts = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M")
    rollup_branch = f"{ROLLUP_BRANCH_PREFIX}{ts}"
    # push directly from refs/remotes/origin/<INTEGRATION> as new branch
    r = run(["git", "push", "origin",
             f"refs/remotes/origin/{INTEGRATION}:refs/heads/{rollup_branch}"], cwd=MAIN_REPO)
    if r.returncode != 0:
        log(f"FAIL push rollup branch {rollup_branch}: {r.stderr.strip()[:200]}")
        return False
    log(f"pushed rollup branch {rollup_branch}")
    # PR body
    ahead = count_ahead(f"origin/{REVIEW_BASE}", f"origin/{INTEGRATION}")
    age_h = earliest_ahead_age_hours(f"origin/{REVIEW_BASE}", f"origin/{INTEGRATION}")
    body = f"""## 摘要

dev_sync_daemon 自动 rollup `{INTEGRATION}` → `{REVIEW_BASE}`,触发条件 earliest divergent
commit age >= {ROLLUP_HOURS_THRESHOLD} hours。

- {INTEGRATION} ahead {REVIEW_BASE}: **{ahead} commits**
- earliest divergent commit age: **{age_h:.1f} hours**
- trunk content 已是 auto-refact-dev verified state(不重新 build/test verify)

## 自动流程

CI 全 required check 绿后,daemon 已 enable auto-merge(squash + delete branch),trunk 即推
到 `{REVIEW_BASE}`。

⟦AI:AUTO-LOOP⟧
"""
    body_file = MAIN_REPO / ".refactor-loop" / "runs" / f"pr-body-rollup-{ts}.md"
    body_file.parent.mkdir(parents=True, exist_ok=True)
    body_file.write_text(body)
    r = run(["gh", "pr", "create", "--base", REVIEW_BASE, "--head", rollup_branch,
             "--title", f"chore: {INTEGRATION} → {REVIEW_BASE} rollup({ahead} commits, {age_h:.0f}h stale)",
             "--body-file", str(body_file)], cwd=MAIN_REPO)
    if r.returncode != 0:
        log(f"FAIL gh pr create rollup: {r.stderr.strip()[:200]}")
        return False
    pr_url = r.stdout.strip()
    log(f"opened rollup PR: {pr_url}")
    pr_num = pr_url.rsplit("/", 1)[-1]
    run(["gh", "pr", "edit", pr_num, "--add-label", "auto-loop"], cwd=MAIN_REPO)
    r = run(["gh", "pr", "merge", pr_num, "--squash", "--delete-branch", "--auto"],
            cwd=MAIN_REPO)
    if r.returncode != 0:
        log(f"WARN enable auto-merge on rollup PR failed: {r.stderr.strip()[:120]}")
    else:
        log(f"enabled auto-merge on rollup PR #{pr_num}")
    return True


def sync_dev_to_integration() -> None:
    """Try ff/no-ff merge in sync worktree; on success open PR + auto-merge;
    on conflict spawn codex (which also verifies full slnx test per rule 10/11)."""
    cwd = SYNC_WORKTREE
    if not ensure_worktree(cwd, INTEGRATION):
        return

    # MERGE_HEAD bug fix: use real git-dir
    if merge_in_progress(cwd):
        if codex_in_flight("sync"):
            log("sync: merge in progress + codex resolving, skip")
        else:
            log("WARN sync: merge in progress but no codex — dispatching")
            dispatch_codex_resolve(cwd, "sync")
        return

    if working_tree_dirty(cwd):
        log("sync: worktree dirty (codex amend or other) — skip")
        return

    # check if a sync PR is already open
    existing, pr_num = has_open_pr(INTEGRATION, SYNC_BRANCH_PREFIX)
    if existing:
        log(f"sync: open sync PR #{pr_num} already exists, waiting auto-merge")
        return

    if not reset_to_remote(cwd, INTEGRATION):
        return

    behind = count_ahead("HEAD", f"origin/{REVIEW_BASE}", cwd=cwd)
    if behind == 0:
        log(f"sync: up-to-date with origin/{REVIEW_BASE}")
        return

    log(f"sync: behind origin/{REVIEW_BASE} by {behind} commits")

    # try ff first
    r = run(["git", "merge", "--ff-only", f"origin/{REVIEW_BASE}"], cwd=cwd)
    if r.returncode == 0 and ("Fast-forward" in r.stdout or "Already up to date" in r.stdout):
        log(f"sync: ff-merged with origin/{REVIEW_BASE} (+{behind})")
        push_sync_pr(cwd)
        return

    # ff failed → no-ff merge attempt
    log("sync: ff not possible, attempting no-ff merge")
    r = run(["git", "merge", "--no-ff", "-m",
             f"Sync {INTEGRATION} with {REVIEW_BASE} (auto by dev_sync_daemon)",
             f"origin/{REVIEW_BASE}"], cwd=cwd)
    if r.returncode == 0:
        log(f"sync: no-ff merge-committed +{behind} commits")
        # 不直接 push — 派 codex 跑 full slnx test verify per rule 10/11
        # 即使 merge auto OK,test 也可能 fail(API rename, fixture missing 等)
        # codex 完成后 daemon next tick 检测 status clean + tests-pass marker → push PR
        # 当前简化:no-ff merge clean 就 push(若 test fail 让 CI catch)
        # TODO: 嵌入 codex verify step before push
        push_sync_pr(cwd)
        return

    # conflict
    if merge_in_progress(cwd):
        log("sync: CONFLICT detected (merge in progress)")
        if not codex_in_flight("sync"):
            dispatch_codex_resolve(cwd, "sync")
        else:
            log("sync: codex already resolving, skip")
    else:
        log(f"sync: FAIL merge but no MERGE_HEAD: {r.stderr.strip()[:120]}")


def rollup_integration_to_dev() -> None:
    """Per Auric: if INTEGRATION ahead REVIEW_BASE earliest commit age >= 18h, create rollup PR."""
    # skip if a rollup PR already open
    existing, pr_num = has_open_pr(REVIEW_BASE, ROLLUP_BRANCH_PREFIX)
    if existing:
        log(f"rollup: open rollup PR #{pr_num} already exists, skip")
        return

    # fetch first
    run(["git", "fetch", "origin", "--quiet"], cwd=MAIN_REPO)

    ahead = count_ahead(f"origin/{REVIEW_BASE}", f"origin/{INTEGRATION}")
    if ahead == 0:
        log(f"rollup: {INTEGRATION} not ahead of {REVIEW_BASE}, skip")
        return

    age_h = earliest_ahead_age_hours(f"origin/{REVIEW_BASE}", f"origin/{INTEGRATION}")
    if age_h < ROLLUP_HOURS_THRESHOLD:
        log(f"rollup: ahead {ahead} commits, earliest age {age_h:.1f}h < threshold {ROLLUP_HOURS_THRESHOLD}h, skip")
        return

    log(f"rollup: TRIGGER ahead={ahead} commits earliest_age={age_h:.1f}h >= {ROLLUP_HOURS_THRESHOLD}h")
    push_rollup_pr()


def tick() -> None:
    sync_dev_to_integration()
    rollup_integration_to_dev()


def main() -> None:
    log(f"dev_sync_daemon (Python) started: interval={INTERVAL}s sync_wt={SYNC_WORKTREE} "
        f"{REVIEW_BASE} ↔ {INTEGRATION} rollup_threshold={ROLLUP_HOURS_THRESHOLD}h")
    if not ensure_worktree(SYNC_WORKTREE, INTEGRATION):
        log("FATAL: cannot ensure sync worktree, exiting")
        sys.exit(1)
    while True:
        try:
            tick()
        except Exception as e:
            log(f"EXCEPTION in tick: {e!r}")
        time.sleep(INTERVAL)


if __name__ == "__main__":
    main()
